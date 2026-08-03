using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Backtesting.Models;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Options;
using QuantEngine.Indicators;
using QuantEngine.Indicators.Models;

namespace QuantEngine.Backtesting;

/// <summary>Splits data into IS/OOS windows, optimizes on IS, validates on OOS.</summary>
public sealed class WalkForwardValidator
{
    private readonly OptimizationOptions _opt;
    private readonly IndicatorsOptions   _baseInd;
    private readonly StrategyOptions     _strat;
    private readonly RiskOptions         _risk;
    private readonly BacktestOptions     _bt;
    private readonly DataOptions         _data;
    private readonly ILogger<WalkForwardValidator> _log;
    private readonly ILoggerFactory _logFactory;

    public WalkForwardValidator(
        IOptions<OptimizationOptions> opt,
        IOptions<IndicatorsOptions>   baseInd,
        IOptions<StrategyOptions>     strat,
        IOptions<RiskOptions>         risk,
        IOptions<BacktestOptions>     bt,
        IOptions<DataOptions>         data,
        ILogger<WalkForwardValidator> log,
        ILoggerFactory logFactory)
    {
        _opt     = opt.Value;     _baseInd = baseInd.Value;
        _strat   = strat.Value;   _risk    = risk.Value;
        _bt      = bt.Value;      _data    = data.Value;
        _log     = log;           _logFactory = logFactory;
    }

    public WalkForwardResult Run(
        Dictionary<string, MarketData> universe,
        MarketData benchmark,
        CancellationToken ct = default)
    {
        int splitIdx = Math.Max(1, (int)(benchmark.Length * _opt.InSampleFraction));
        if (splitIdx >= benchmark.Length - 5)
            throw new InvalidOperationException(
                "WalkForward split leaves fewer than 5 OOS bars.");

        DateTime cutoff    = benchmark.Dates[splitIdx];
        DateTime fullEnd   = benchmark.Dates[^1].AddDays(1);
        DateTime fullStart = benchmark.Dates[0];

        _log.LogInformation("[WalkForward] IS:{S}→{C}  OOS:{C}→{E}",
            fullStart, cutoff, cutoff, benchmark.Dates[^1]);

        // IS: rebuild indicators on IS data only
        var engine    = new IndicatorEngine(_logFactory.CreateLogger<IndicatorEngine>());
        var isUniverse = new Dictionary<string, MarketData>(
            universe.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in universe)
        {
            var rawIs = SliceToOhlc(kvp.Value, fullStart, cutoff);
            if (rawIs.IsValid) isUniverse[kvp.Key] = engine.Build(rawIs, _baseInd);
        }
        if (!isUniverse.TryGetValue(benchmark.Symbol, out var isBench) || !isBench.IsValid)
            throw new InvalidOperationException("IS benchmark empty.");

        var optimizer = new GridOptimizer(
            Options.Create(_baseInd), Options.Create(_strat), Options.Create(_risk),
            Options.Create(_bt), Options.Create(_data), Options.Create(_opt),
            _logFactory.CreateLogger<GridOptimizer>(), _logFactory);
        var isResults = optimizer.Run(isUniverse, isBench, ct);
        if (isResults.Count == 0) throw new InvalidOperationException("IS optimizer returned no results.");

        var best = isResults[0];
        _log.LogInformation("[WalkForward] Best IS Sharpe={S:F2} " +
            "HmaFast={F} HmaSlow={Sl} ADX={A} STx={M}",
            best.Metrics.SharpeRatio, best.Indicators.HmaFast, best.Indicators.HmaSlow,
            best.Indicators.AdxThreshold, best.Indicators.SupertrendMultiplier);

        // OOS: rebuild with best IS params on full data, then slice to OOS window
        var oosUniverse = new Dictionary<string, MarketData>(
            universe.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in universe)
        {
            var rawFull = SliceToOhlc(kvp.Value, fullStart, fullEnd);
            if (!rawFull.IsValid) continue;
            var built   = engine.Build(rawFull, best.Indicators);
            var sliced  = built.SliceByDate(cutoff, fullEnd);
            if (sliced.IsValid) oosUniverse[kvp.Key] = sliced;
        }
        if (!oosUniverse.TryGetValue(benchmark.Symbol, out var oosBench) || !oosBench.IsValid)
            throw new InvalidOperationException("OOS benchmark empty.");

        var oosCfgData = _data with { Start = cutoff, End = benchmark.Dates[^1] };
        var oosBacktester = new PortfolioBacktester(
            Options.Create(best.Indicators), Options.Create(best.Strategy),
            Options.Create(_risk), Options.Create(_bt), Options.Create(oosCfgData),
            _logFactory.CreateLogger<PortfolioBacktester>());

        var oosRes = oosBacktester.RunCrossSectional(oosUniverse, oosBench);
        return new WalkForwardResult(
            best.Indicators, best.Strategy, best.Metrics, oosRes.Metrics);
    }

    private static OhlcData SliceToOhlc(MarketData md, DateTime from, DateTime to)
    {
        int s = Array.BinarySearch(md.Dates, from); if (s < 0) s = ~s;
        int e = Array.BinarySearch(md.Dates, to);   if (e < 0) e = ~e;
        if (s >= e) return OhlcData.Empty(md.Symbol);
        return new OhlcData(md.Symbol,
            md.Dates[s..e], md.Open[s..e], md.High[s..e],
            md.Low[s..e], md.Close[s..e], md.Volume[s..e]);
    }
}
