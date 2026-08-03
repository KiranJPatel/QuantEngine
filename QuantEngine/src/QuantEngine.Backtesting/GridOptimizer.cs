using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Backtesting.Models;
using QuantEngine.Domain.Options;
using QuantEngine.Indicators;
using QuantEngine.Indicators.Models;

namespace QuantEngine.Backtesting;

/// <summary>
/// Exhaustive grid search over indicator parameter combinations.
/// Parallel execution with progress reporting every 10% of combos.
/// </summary>
public sealed class GridOptimizer
{
    private readonly IndicatorsOptions  _baseInd;
    private readonly StrategyOptions    _strat;
    private readonly RiskOptions        _risk;
    private readonly BacktestOptions    _bt;
    private readonly DataOptions        _data;
    private readonly OptimizationOptions _opt;
    private readonly ILogger<GridOptimizer> _log;
    private readonly ILoggerFactory         _logFactory;

    public GridOptimizer(
        IOptions<IndicatorsOptions>   baseInd,
        IOptions<StrategyOptions>     strat,
        IOptions<RiskOptions>         risk,
        IOptions<BacktestOptions>     bt,
        IOptions<DataOptions>         data,
        IOptions<OptimizationOptions> opt,
        ILogger<GridOptimizer>        log,
        ILoggerFactory                logFactory)
    {
        _baseInd    = baseInd.Value;  _strat  = strat.Value;
        _risk       = risk.Value;     _bt     = bt.Value;
        _data       = data.Value;     _opt    = opt.Value;
        _log        = log;            _logFactory = logFactory;
    }

    public List<OptimizationResult> Run(
        Dictionary<string, MarketData> baseUniverse,
        MarketData baseBenchmark,
        CancellationToken ct = default)
    {
        var combos = (
            from fast in _opt.HmaFastRange
            from slow in _opt.HmaSlowRange where fast < slow
            from adx  in _opt.AdxThresholdRange
            from mult in _opt.SupertrendMultiplierRange
            select _baseInd with
            {
                HmaFast = fast, HmaSlow = slow,
                AdxThreshold = adx, SupertrendMultiplier = mult
            }
        ).ToList();

        int parallelism = _opt.Parallelism < 1
            ? Environment.ProcessorCount
            : Math.Min(_opt.Parallelism, Environment.ProcessorCount);

        _log.LogInformation("[Optimizer] {N} combos | parallelism={P}", combos.Count, parallelism);

        var bag  = new ConcurrentBag<OptimizationResult>();
        int done = 0;
        int step = Math.Max(1, combos.Count / 10);

        Parallel.ForEach(combos,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            indCfg =>
        {
            // Rebuild indicators fresh per combo — never share state across threads
            var optUniv = new Dictionary<string, MarketData>(
                baseUniverse.Count, StringComparer.OrdinalIgnoreCase);
            var engine  = new IndicatorEngine(
                _logFactory.CreateLogger<IndicatorEngine>());

            foreach (var kvp in baseUniverse)
            {
                var raw = kvp.Value.ToOhlcData();
                if (raw.IsValid) optUniv[kvp.Key] = engine.Build(raw, indCfg);
            }

            if (!optUniv.TryGetValue(baseBenchmark.Symbol, out var bench)) return;

            var indOpts = Options.Create(indCfg);
            var bt = new PortfolioBacktester(
                indOpts,
                Options.Create(_strat),
                Options.Create(_risk),
                Options.Create(_bt),
                Options.Create(_data),
                _logFactory.CreateLogger<PortfolioBacktester>());

            var res = bt.RunCrossSectional(optUniv, bench, Guid.NewGuid().ToString());
            bag.Add(new OptimizationResult(indCfg, _strat, res.Metrics));

            int n = Interlocked.Increment(ref done);
            if (n % step == 0)
                _log.LogInformation("[Optimizer] {D}/{T} combos", n, combos.Count);
        });

        return bag.OrderByDescending(r => r.Metrics.SharpeRatio)
                  .ThenByDescending(r => r.Metrics.CalmarRatio)
                  .ThenBy(r         => r.Metrics.MaxDrawdownPct)
                  .Take(_opt.TopN)
                  .ToList();
    }
}
