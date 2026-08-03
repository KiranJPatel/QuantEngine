using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Options;
using QuantEngine.Indicators;
using QuantEngine.Indicators.Models;

namespace QuantEngine.Tests.Unit;

/// <summary>Factory helpers shared across all test classes.</summary>
public static class TestHelpers
{
    // ── Options factories ──────────────────────────────────────────────────────
    public static IOptions<T> Opt<T>(T value) where T : class => Options.Create(value);

    public static IndicatorsOptions DefaultIndOpts => new()
    {
        HmaFast = 20, HmaSlow = 50, SupertrendAtrPeriod = 10,
        SupertrendMultiplier = 3.0, AdxPeriod = 14, AdxThreshold = 20.0
    };

    public static StrategyOptions DefaultStratOpts => new()
    {
        TakeProfitAtrMultiple = 3.0, StopLossAtrMultiple = 1.5,
        TrailingStopAtrMultiple = 2.0, MinimumAlphaScore = 40.0
    };

    public static RiskOptions DefaultRiskOpts => new()
    {
        AccountRiskPerTradePct = 0.01, MaxOpenPositions = 5,
        MaxPortfolioHeat = 0.80, RegimeHeatPenalty = 0.50
    };

    public static BacktestOptions DefaultBtOpts => new()
    {
        InitialCapital = 100_000, CommissionPerShare = 0.005,
        SlippageAtrFrac = 0.0, ReportsFolder = "/tmp/qe_test_reports"
    };

    public static DataOptions DefaultDataOpts => new()
    {
        Start = new DateTime(2020, 1, 1), End = new DateTime(2022, 12, 31)
    };

    // ── OHLC data generators ───────────────────────────────────────────────────

    /// <summary>
    /// Generates a deterministic uptrending price series.
    /// Each bar rises by a fixed drift so HMA-fast always crosses above HMA-slow
    /// after the warmup period — guaranteeing entry signals for integrity testing.
    /// </summary>
    public static OhlcData GenerateTrendingOhlc(
        string symbol, int bars, DateTime start,
        double startPrice = 100.0, double dailyDrift = 0.003, double atrNorm = 1.0)
    {
        var dates  = new DateTime[bars];
        var open   = new double[bars];
        var high   = new double[bars];
        var low    = new double[bars];
        var close  = new double[bars];
        var volume = new double[bars];
        double price = startPrice;
        var rng = new Random(42); // deterministic seed for reproducible tests

        for (int i = 0; i < bars; i++)
        {
            dates[i]  = start.AddDays(i);
            open[i]   = price;
            close[i]  = price * (1 + dailyDrift + (rng.NextDouble() - 0.45) * 0.005);
            high[i]   = Math.Max(open[i], close[i]) * (1 + atrNorm * 0.005);
            low[i]    = Math.Min(open[i], close[i]) * (1 - atrNorm * 0.005);
            volume[i] = 1_000_000;
            price     = close[i];
        }
        return new OhlcData(symbol, dates, open, high, low, close, volume);
    }

    /// <summary>Generates a sideways / slightly bearish series that produces no entry signals.</summary>
    public static OhlcData GenerateFlatOhlc(
        string symbol, int bars, DateTime start, double startPrice = 100.0)
        => GenerateTrendingOhlc(symbol, bars, start, startPrice, dailyDrift: -0.001);

    /// <summary>Build a MarketData using real IndicatorEngine from test OhlcData.</summary>
    public static MarketData BuildMarketData(OhlcData raw, IndicatorsOptions? opts = null)
    {
        var engine = new IndicatorEngine(NullLogger<IndicatorEngine>.Instance);
        return engine.Build(raw, opts ?? DefaultIndOpts);
    }
}
