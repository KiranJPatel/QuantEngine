using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Backtesting;
using QuantEngine.Backtesting.Analytics;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Interfaces;
using QuantEngine.Domain.Options;
using QuantEngine.Host;
using QuantEngine.Host.Metrics;
using QuantEngine.Indicators;
using QuantEngine.Infrastructure.Persistence;
using QuantEngine.Reporting;
using QuantEngine.Trading;
using Serilog;

// ── Bootstrap Serilog before host build so early errors are captured ─────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
        .AddEnvironmentVariables("QE_")
        .Build())
    .CreateBootstrapLogger();

try
{
    using var host = Host.CreateDefaultBuilder(args)
        .ConfigureAppConfiguration((ctx, c) =>
        {
            c.AddJsonFile("appsettings.json",  optional: false, reloadOnChange: false);
            c.AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json",
                optional: true, reloadOnChange: false);
            c.AddEnvironmentVariables("QE_");   // QE_Data__BenchmarkSymbol=NIFTY50
            c.AddCommandLine(args);
        })
        .ConfigureServices((ctx, svc) => svc.AddQuantEngine(ctx.Configuration))
        .UseSerilog((ctx, _, lc) => lc.ReadFrom.Configuration(ctx.Configuration))
        .Build();

    // ── CancellationToken wired to Ctrl-C and SIGTERM ─────────────────────────
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Log.Warning("Shutdown signal received — cancelling pipeline");
        cts.Cancel();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    var sp  = host.Services;
    var log = sp.GetRequiredService<ILogger<Program>>();

    // ══════════════════════════════════════════════════════════════════════════
    //  STARTUP — parse CLI + read mode
    // ══════════════════════════════════════════════════════════════════════════
    bool authMode      = args.Contains("--auth",  StringComparer.OrdinalIgnoreCase);
    bool healthMode    = args.Contains("--health", StringComparer.OrdinalIgnoreCase);

    AppMode mode = AppMode.Backtest;
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i].Equals("--mode", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<AppMode>(args[i + 1], true, out var m))
            mode = m;

    if (mode == AppMode.Backtest) // fallback: read from appsettings
    {
        string cfgMode = host.Services.GetRequiredService<IConfiguration>()
            .GetValue<string>("AppMode") ?? "Backtest";
        if (Enum.TryParse<AppMode>(cfgMode, true, out var cfgM)) mode = cfgM;
    }

    // ── Startup validation (cross-field rules not covered by DataAnnotations) ─
    try
    {
        var data = sp.GetRequiredService<IOptions<DataOptions>>().Value;
        var ind  = sp.GetRequiredService<IOptions<IndicatorsOptions>>().Value;
        var strat= sp.GetRequiredService<IOptions<StrategyOptions>>().Value;
        var risk = sp.GetRequiredService<IOptions<RiskOptions>>().Value;
        var bt   = sp.GetRequiredService<IOptions<BacktestOptions>>().Value;
        var opt  = sp.GetRequiredService<IOptions<OptimizationOptions>>().Value;
        var live = sp.GetRequiredService<IOptions<LiveTradingOptions>>().Value;
        var brk  = sp.GetRequiredService<IOptions<BrokersOptions>>().Value;
        StartupValidator.Validate(data, ind, strat, risk, bt, opt, live, brk, mode);
    }
    catch (InvalidOperationException ex)
    {
        Log.Fatal("[Config] {Message}", ex.Message);
        return 1;
    }

    log.LogInformation("═══ QuantEngine v5.0 | Mode: {Mode} | PID: {Pid} ═══",
        mode, Environment.ProcessId);

    // ── Initialise database ───────────────────────────────────────────────────
    var db = sp.GetRequiredService<DatabaseManager>();
    await db.InitializeAsync(cts.Token);

    // ── Health check report ────────────────────────────────────────────────────
    if (healthMode || mode == AppMode.LiveTrade)
    {
        var healthSvc = sp.GetRequiredService<HealthCheckService>();
        var report    = await healthSvc.CheckHealthAsync(cts.Token);

        log.LogInformation("[Health] Overall status: {Status}", report.Status);
        foreach (var (name, entry) in report.Entries)
        {
            var lvl = entry.Status == HealthStatus.Healthy
                ? LogLevel.Information : LogLevel.Warning;
            log.Log(lvl, "[Health] {Name}: {Status} — {Desc}",
                name, entry.Status, entry.Description);
        }

        if (healthMode) return report.Status == HealthStatus.Unhealthy ? 2 : 0;

        if (report.Status == HealthStatus.Unhealthy)
        {
            log.LogCritical("[Health] Unhealthy subsystems detected — aborting LiveTrade");
            return 2;
        }
    }

    // ── --auth: generate broker access token, then exit ───────────────────────
    if (authMode)
    {
        using var broker = sp.GetRequiredService<IBroker>();
        await broker.GenerateAccessTokenAsync(cts.Token);
        log.LogInformation("[Auth] Token generated — update appsettings.json and rerun");
        return 0;
    }

    // ── Load universe ──────────────────────────────────────────────────────────
    var dataOpts = sp.GetRequiredService<IOptions<DataOptions>>().Value;
    if (!File.Exists(dataOpts.UniverseFilePath))
    {
        log.LogCritical("Universe file not found: {Path}", dataOpts.UniverseFilePath);
        return 1;
    }
    string[] symbols = JsonSerializer.Deserialize<string[]>(
        await File.ReadAllTextAsync(dataOpts.UniverseFilePath, cts.Token)) ?? [];

    if (!symbols.Contains(dataOpts.BenchmarkSymbol, StringComparer.OrdinalIgnoreCase))
        symbols = [..symbols, dataOpts.BenchmarkSymbol];

    log.LogInformation("Universe: {N} symbols | {S:yyyy-MM-dd} → {E:yyyy-MM-dd}",
        symbols.Length, dataOpts.Start, dataOpts.End);

    // ── Fetch OHLC (throttled to 4 concurrent requests) ──────────────────────
    var provider = sp.GetRequiredService<IOhlcProvider>();
    var engine   = sp.GetRequiredService<IndicatorEngine>();
    var indOpts  = sp.GetRequiredService<IOptions<IndicatorsOptions>>().Value;

    using var throttle = new SemaphoreSlim(4, 4);
    var rawData = await Task.WhenAll(symbols.Select(async sym =>
    {
        await throttle.WaitAsync(cts.Token);
        try
        {
            QuantEngineMetrics.YahooApiCalls.Add(1, new("symbol", sym));
            return await provider.GetOhlcAsync(sym, dataOpts.Start, dataOpts.End, cts.Token);
        }
        finally { throttle.Release(); }
    }));

    // ── Build indicator universe ───────────────────────────────────────────────
    var universe  = new Dictionary<string, QuantEngine.Indicators.Models.MarketData>(
        symbols.Length, StringComparer.OrdinalIgnoreCase);
    QuantEngine.Indicators.Models.MarketData benchmark = default;

    foreach (var raw in rawData)
    {
        if (!raw.IsValid)
        {
            log.LogWarning("Skipping {Sym} — no valid OHLC bars", raw.Symbol);
            continue;
        }
        var md = engine.Build(raw, indOpts);
        universe[raw.Symbol] = md;
        if (raw.Symbol.Equals(dataOpts.BenchmarkSymbol, StringComparison.OrdinalIgnoreCase))
            benchmark = md;
    }

    // ── Data health report ─────────────────────────────────────────────────────
    DataHealthReporter.Print(DataHealthReporter.Build(symbols, universe));

    // ── Warmup guard ──────────────────────────────────────────────────────────
    int minBars = indOpts.HmaSlow * 2 + indOpts.AdxPeriod + 10;
    if (!benchmark.IsValid || benchmark.Length < minBars)
    {
        log.LogCritical("Benchmark {B} has {N} bars; need ≥ {M} for indicator warmup.",
            dataOpts.BenchmarkSymbol, benchmark.Length, minBars);
        return 1;
    }
    log.LogInformation("Universe ready: {N} symbols | Benchmark {B}: {L} bars",
        universe.Count, benchmark.Symbol, benchmark.Length);

    // ══════════════════════════════════════════════════════════════════════════
    //  MODE DISPATCH
    // ══════════════════════════════════════════════════════════════════════════
    switch (mode)
    {
        case AppMode.Backtest:
        {
            var backtester = sp.GetRequiredService<PortfolioBacktester>();
            var reporter   = sp.GetRequiredService<ReportExporter>();
            var repo       = sp.GetRequiredService<IBacktestRepository>();
            var btOpts     = sp.GetRequiredService<IOptions<BacktestOptions>>().Value;

            string runId = Guid.NewGuid().ToString();
            var sw = Stopwatch.StartNew();

            using (Serilog.Context.LogContext.PushProperty("RunId", runId[..8]))
            {
                var res = backtester.RunCrossSectional(universe, benchmark, runId);
                sw.Stop();

                // Publish metrics
                QuantEngineMetrics.BacktestRuns.Add(1);
                QuantEngineMetrics.BacktestDurationMs.Record(sw.ElapsedMilliseconds);
                QuantEngineMetrics.TradesTotal.Add(res.Trades.Count);
                QuantEngineMetrics.TradesWon.Add(res.Metrics.WinningTrades);
                QuantEngineMetrics.TradesLost.Add(res.Metrics.TotalTrades - res.Metrics.WinningTrades);

                ReportExporter.PrintSummary(res.Metrics, runId, sw.ElapsedMilliseconds);

                var bench = PerformanceAnalytics.ComputeBenchmark(benchmark, btOpts.InitialCapital);
                ReportExporter.PrintBenchmarkComparison(
                    res.Metrics, bench, dataOpts.BenchmarkSymbol, btOpts.InitialCapital);

                reporter.ExportEquityCurve(benchmark.Dates, res.Metrics.EquityCurve, runId);
                reporter.ExportTrades(res.Trades, runId);

                await repo.SaveRunAsync(runId, JsonSerializer.Serialize(dataOpts),
                    res.Trades, res.Metrics, dataOpts.Start, dataOpts.End, cts.Token);
                log.LogInformation("[DB] Run {Id} saved", runId[..8]);
            }
            break;
        }

        case AppMode.Optimize:
        {
            var optOpts = sp.GetRequiredService<IOptions<OptimizationOptions>>().Value;

            if (optOpts.EnableWalkForward)
            {
                log.LogInformation("[WalkForward] IS={F:P0} OOS={O:P0}",
                    optOpts.InSampleFraction, 1 - optOpts.InSampleFraction);
                var wf    = sp.GetRequiredService<WalkForwardValidator>();
                var wfRes = wf.Run(universe, benchmark, cts.Token);
                ReportExporter.PrintWalkForward(wfRes);
            }
            else
            {
                var sw  = Stopwatch.StartNew();
                var opt = sp.GetRequiredService<GridOptimizer>();
                var res = opt.Run(universe, benchmark, cts.Token);
                sw.Stop();
                log.LogInformation("[Optimizer] Completed in {T:F1}s", sw.Elapsed.TotalSeconds);
                ReportExporter.PrintOptimizationGrid(res);
            }
            break;
        }

        case AppMode.PaperTrade:
        {
            var paper = sp.GetRequiredService<PaperTradingEngine>();
            paper.RunDailyUpdate(universe, benchmark);
            break;
        }

        case AppMode.LiveTrade:
        {
            log.LogInformation("[Live] Starting live session — broker: {B}",
                sp.GetRequiredService<IOptions<BrokersOptions>>().Value.ActiveBroker);
            using var broker  = sp.GetRequiredService<IBroker>();
            await using var feed = sp.GetRequiredService<IMarketDataFeed>();

            if (!await broker.AuthenticateAsync(cts.Token))
            {
                log.LogCritical("Broker authentication failed — aborting");
                return 1;
            }

            string liveRunId  = Guid.NewGuid().ToString();
            var    btOpts     = sp.GetRequiredService<IOptions<BacktestOptions>>().Value;
            string auditPath  = Path.Combine(btOpts.ReportsFolder,
                $"audit_{DateTime.UtcNow:yyyyMMdd}_{liveRunId[..8]}.csv");
            using var audit   = new QuantEngine.Infrastructure.Audit.AuditLogger(auditPath);
            log.LogInformation("[Live] Audit → {Path}", auditPath);

            var liveEngine = new LiveTradingEngine(
                broker, feed,
                provider,
                engine,
                sp.GetRequiredService<QuantEngine.Risk.LiveRiskManager>(),
                audit, liveRunId,
                sp.GetRequiredService<IOptions<LiveTradingOptions>>(),
                sp.GetRequiredService<IOptions<IndicatorsOptions>>(),
                sp.GetRequiredService<IOptions<StrategyOptions>>(),
                sp.GetRequiredService<IOptions<RiskOptions>>(),
                sp.GetRequiredService<IOptions<BacktestOptions>>(),
                sp.GetRequiredService<ILogger<LiveTradingEngine>>(),
                sp.GetRequiredService<ILogger<QuantEngine.Trading.LivePositionManager>>(),
                sp.GetRequiredService<ILogger<QuantEngine.Trading.OrderManagementSystem>>());

            await liveEngine.RunAsync(symbols, cts.Token);
            break;
        }
    }

    return 0;
}
catch (OperationCanceledException)
{
    Log.Warning("Pipeline cancelled.");
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Unhandled fatal exception");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
