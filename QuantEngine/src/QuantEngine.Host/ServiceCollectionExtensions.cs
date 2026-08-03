using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Backtesting;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Interfaces;
using QuantEngine.Domain.Options;
using QuantEngine.Indicators;
using QuantEngine.Infrastructure.Audit;
using QuantEngine.Infrastructure.Brokers.Upstox;
using QuantEngine.Infrastructure.Brokers.Zerodha;
using QuantEngine.Infrastructure.Feeds.Upstox;
using QuantEngine.Infrastructure.Feeds.Zerodha;
using QuantEngine.Infrastructure.HealthChecks;
using QuantEngine.Infrastructure.MarketData.Cache;
using QuantEngine.Infrastructure.MarketData.Csv;
using QuantEngine.Infrastructure.MarketData.Yahoo;
using QuantEngine.Infrastructure.Persistence;
using QuantEngine.Reporting;
using QuantEngine.Risk;
using QuantEngine.Trading;

namespace QuantEngine.Host;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQuantEngine(
        this IServiceCollection svc, IConfiguration cfg)
    {
        // ── Strongly-typed Options with DataAnnotation validation ─────────────
        // ValidateDataAnnotations() checks [Range], [Required], [MinLength] at startup.
        // ValidateOnStart() makes failures throw during host.Build() — not on first use.
        svc.AddOptions<DataOptions>()
            .Bind(cfg.GetSection(DataOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        svc.AddOptions<IndicatorsOptions>()
            .Bind(cfg.GetSection(IndicatorsOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.HmaFast < o.HmaSlow,
                "Indicators.HmaFast must be strictly less than Indicators.HmaSlow")
            .ValidateOnStart();

        svc.AddOptions<StrategyOptions>()
            .Bind(cfg.GetSection(StrategyOptions.Section))
            .ValidateDataAnnotations()
            .Validate(o => o.TakeProfitAtrMultiple > o.StopLossAtrMultiple,
                "Strategy.TakeProfitAtrMultiple must exceed StopLossAtrMultiple for positive R:R")
            .ValidateOnStart();

        svc.AddOptions<RiskOptions>()
            .Bind(cfg.GetSection(RiskOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        svc.AddOptions<BacktestOptions>()
            .Bind(cfg.GetSection(BacktestOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        svc.AddOptions<OptimizationOptions>()
            .Bind(cfg.GetSection(OptimizationOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        svc.AddOptions<BrokersOptions>()
            .Bind(cfg.GetSection(BrokersOptions.Section))
            .ValidateOnStart();

        svc.AddOptions<ZerodhaOptions>()
            .Bind(cfg.GetSection(ZerodhaOptions.Section));   // no ValidateOnStart — optional

        svc.AddOptions<UpstoxOptions>()
            .Bind(cfg.GetSection(UpstoxOptions.Section));    // no ValidateOnStart — optional

        svc.AddOptions<LiveTradingOptions>()
            .Bind(cfg.GetSection(LiveTradingOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // ── IHttpClientFactory for adapters that don't need cookie jar ────────
        svc.AddHttpClient();

        // ── Core services ──────────────────────────────────────────────────────
        svc.AddSingleton<IndicatorEngine>();

        // ── Backtesting ────────────────────────────────────────────────────────
        svc.AddSingleton<PortfolioBacktester>();
        svc.AddSingleton<GridOptimizer>();
        svc.AddSingleton<WalkForwardValidator>();

        // ── Infrastructure: OhlcCache ──────────────────────────────────────────
        svc.AddSingleton<OhlcDiskCache>();

        // ── Infrastructure: OHLC Provider ─────────────────────────────────────
        svc.AddSingleton<IOhlcProvider>(sp =>
        {
            var dataOpts = sp.GetRequiredService<IOptions<DataOptions>>().Value;
            return dataOpts.Provider.Equals("CSV", StringComparison.OrdinalIgnoreCase)
                ? (IOhlcProvider)new CsvOhlcProvider(
                    Options.Create(dataOpts),
                    sp.GetRequiredService<ILogger<CsvOhlcProvider>>())
                : new YahooFinanceProvider(
                    Options.Create(dataOpts),
                    sp.GetRequiredService<OhlcDiskCache>(),
                    sp.GetRequiredService<ILogger<YahooFinanceProvider>>());
        });

        // ── Infrastructure: Broker ─────────────────────────────────────────────
        svc.AddSingleton<IBroker>(sp =>
        {
            var brokers = sp.GetRequiredService<IOptions<BrokersOptions>>().Value;
            return brokers.ActiveBroker switch
            {
                BrokerType.Zerodha => new ZerodhaAdapter(
                    sp.GetRequiredService<IOptions<ZerodhaOptions>>(),
                    sp.GetRequiredService<ILogger<ZerodhaAdapter>>()),
                BrokerType.Upstox => new UpstoxAdapter(
                    sp.GetRequiredService<IOptions<UpstoxOptions>>(),
                    sp.GetRequiredService<ILogger<UpstoxAdapter>>()),
                _ => throw new InvalidOperationException(
                    $"Brokers.ActiveBroker = '{brokers.ActiveBroker}' is not supported " +
                    "for live trading. Set to Zerodha or Upstox in appsettings.json.")
            };
        });

        // ── Infrastructure: Market Data Feed ──────────────────────────────────
        svc.AddSingleton<IMarketDataFeed>(sp =>
        {
            var brokers = sp.GetRequiredService<IOptions<BrokersOptions>>().Value;
            var live    = sp.GetRequiredService<IOptions<LiveTradingOptions>>().Value;
            return brokers.ActiveBroker switch
            {
                BrokerType.Zerodha when live.UseWebSocketFeed =>
                    new ZerodhaWebSocketFeed(
                        sp.GetRequiredService<IOptions<ZerodhaOptions>>(),
                        sp.GetRequiredService<ILogger<ZerodhaWebSocketFeed>>()),
                _ => new UpstoxRestPollFeed(
                    sp.GetRequiredService<IBroker>(),
                    sp.GetRequiredService<IOptions<LiveTradingOptions>>(),
                    sp.GetRequiredService<ILogger<UpstoxRestPollFeed>>())
            };
        });

        // ── Infrastructure: Persistence ────────────────────────────────────────
        svc.AddSingleton<DatabaseManager>(sp =>
        {
            string dbPath = cfg.GetValue<string>("Database:Path") ?? "quant_backtest.db";
            return new DatabaseManager(dbPath, sp.GetRequiredService<ILogger<DatabaseManager>>());
        });
        svc.AddSingleton<IBacktestRepository, BacktestRepository>();

        // ── Risk + Trading ─────────────────────────────────────────────────────
        svc.AddSingleton<LiveRiskManager>();
        svc.AddSingleton<PaperTradingEngine>();

        // ── Reporting ──────────────────────────────────────────────────────────
        svc.AddSingleton<ReportExporter>();

        // ── Health Checks ──────────────────────────────────────────────────────
        // Registered but only exercised if program calls healthCheckService.CheckHealthAsync().
        // In a future web-hosted version this maps to /health and /health/ready endpoints.
        svc.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database",
                tags: ["ready", "persistence"])
            .AddCheck<BrokerHealthCheck>("broker",
                tags: ["ready", "live"]);

        svc.AddSingleton<DatabaseHealthCheck>();
        svc.AddSingleton<BrokerHealthCheck>();

        return svc;
    }
}
