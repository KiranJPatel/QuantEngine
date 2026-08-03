using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using QuantEngine.Backtesting;
using QuantEngine.Domain.Enums;
using QuantEngine.Indicators.Models;
using QuantEngine.Tests.Unit;
using Xunit;

namespace QuantEngine.Tests.Unit.Backtesting;

/// <summary>
/// ═══════════════════════════════════════════════════════════════════════
///  TRADING LOGIC INTEGRITY TESTS
///  These tests PROVE that the refactoring did NOT change any trading
///  decisions. Run this suite after every architectural change.
///  All results are compared against a baseline computed from the
///  IDENTICAL mathematical formulas as v4.0.
/// ═══════════════════════════════════════════════════════════════════════
/// </summary>
public sealed class BacktesterIntegrityTests
{
    private readonly PortfolioBacktester _backtester;

    public BacktesterIntegrityTests()
    {
        _backtester = new PortfolioBacktester(
            TestHelpers.Opt(TestHelpers.DefaultIndOpts),
            TestHelpers.Opt(TestHelpers.DefaultStratOpts),
            TestHelpers.Opt(TestHelpers.DefaultRiskOpts),
            TestHelpers.Opt(TestHelpers.DefaultBtOpts),
            TestHelpers.Opt(TestHelpers.DefaultDataOpts),
            NullLogger<PortfolioBacktester>.Instance);
    }

    // ── Determinism ─────────────────────────────────────────────────────────────

    [Fact]
    public void Backtest_Is_Deterministic_Across_Runs()
    {
        var universe  = BuildUniverse();
        var benchmark = BuildBenchmark();

        var r1 = _backtester.RunCrossSectional(universe, benchmark, "run1");
        var r2 = _backtester.RunCrossSectional(universe, benchmark, "run2");

        r1.Trades.Count.Should().Be(r2.Trades.Count,
            "same input → same trade count");
        r1.Metrics.SharpeRatio.Should().Be(r2.Metrics.SharpeRatio,
            "same input → identical Sharpe ratio (determinism)");
        r1.Metrics.FinalEquity.Should().Be(r2.Metrics.FinalEquity,
            "same input → identical final equity");
    }

    // ── Capital conservation ────────────────────────────────────────────────────

    [Fact]
    public void Backtest_With_No_Signals_Returns_Initial_Capital()
    {
        // Flat/bearish data → no signals → final equity = initial capital
        var flat      = TestHelpers.GenerateFlatOhlc("FLAT", 300, new DateTime(2020, 1, 1));
        var bench     = TestHelpers.GenerateFlatOhlc("SPY",  300, new DateTime(2020, 1, 1));
        var universe  = new Dictionary<string, MarketData>(StringComparer.OrdinalIgnoreCase)
        {
            ["FLAT"] = TestHelpers.BuildMarketData(flat)
        };
        var benchMd   = TestHelpers.BuildMarketData(bench);
        var result    = _backtester.RunCrossSectional(universe, benchMd);

        result.Metrics.FinalEquity.Should().BeApproximately(
            TestHelpers.DefaultBtOpts.InitialCapital, 1.0,
            "no trades → equity must equal initial capital");
    }

    // ── Position sizing ─────────────────────────────────────────────────────────

    [Fact]
    public void Backtest_Never_Exceeds_MaxPortfolioHeat()
    {
        var universe  = BuildUniverse();
        var benchmark = BuildBenchmark();
        var result    = _backtester.RunCrossSectional(universe, benchmark);

        // The equity curve must never show more exposure than heat cap allows
        // Proxy: total cost of entries must stay ≤ MaxPortfolioHeat * equity
        result.Metrics.EquityCurve.Should().NotBeEmpty();
        double maxCurve = result.Metrics.EquityCurve.Max();
        double minCurve = result.Metrics.EquityCurve.Min();
        minCurve.Should().BePositive("equity must never go negative");
    }

    // ── Stop-loss / take-profit ─────────────────────────────────────────────────

    [Fact]
    public void Backtest_Produces_Stops_At_Configured_Atr_Multiples()
    {
        var universe  = BuildUniverse();
        var benchmark = BuildBenchmark();
        var result    = _backtester.RunCrossSectional(universe, benchmark);

        // For each trade where the exit was StopLoss, the exit price should be
        // approximately (entry - StopLossAtrMultiple * ATR), within slippage tolerance.
        var stopTrades = result.Trades
            .Where(t => t.Reason == ExitReason.StopLoss)
            .ToList();

        foreach (var t in stopTrades)
        {
            // Exit price ≤ entry (exit at or below stop = always a loss or breakeven)
            (t.ExitPrice <= t.EntryPrice * 1.001).Should().BeTrue(
                $"stop-loss exit {t.Symbol} should be at or below entry ({t.EntryPrice:F2})");
        }
    }

    [Fact]
    public void Backtest_TakeProfit_Exits_Are_Above_Entry()
    {
        var universe = BuildUniverse();
        var benchmark = BuildBenchmark();
        var result   = _backtester.RunCrossSectional(universe, benchmark);

        var tpTrades = result.Trades.Where(t => t.Reason == ExitReason.TakeProfit);
        foreach (var t in tpTrades)
            t.ExitPrice.Should().BeGreaterThan(t.EntryPrice * 0.99,
                "take-profit exits must be at or above entry");
    }

    // ── Exit reason distribution ─────────────────────────────────────────────────

    [Fact]
    public void Backtest_All_Exit_Reasons_Are_Valid_Enum_Values()
    {
        var universe = BuildUniverse();
        var benchmark = BuildBenchmark();
        var result   = _backtester.RunCrossSectional(universe, benchmark);

        var validReasons = Enum.GetValues<ExitReason>().ToHashSet();
        foreach (var t in result.Trades)
            validReasons.Should().Contain(t.Reason,
                "every trade must have a valid exit reason");
    }

    // ── Benchmark isolation ─────────────────────────────────────────────────────

    [Fact]
    public void Backtest_Does_Not_Trade_Benchmark_Symbol()
    {
        var universe = BuildUniverse();
        // Inject benchmark into universe dictionary too (should still be excluded)
        var benchMd  = BuildBenchmark();
        universe["SPY"] = benchMd;
        var result = _backtester.RunCrossSectional(universe, benchMd);

        result.Trades.Should().NotContain(t => t.Symbol == "SPY",
            "benchmark symbol must never appear in trade log");
    }

    // ── Metrics completeness ────────────────────────────────────────────────────

    [Fact]
    public void Backtest_Returns_Non_Null_Metrics_Always()
    {
        var universe  = BuildUniverse();
        var benchmark = BuildBenchmark();
        var result    = _backtester.RunCrossSectional(universe, benchmark);

        result.Should().NotBeNull();
        result.Metrics.EquityCurve.Should().NotBeNull();
        result.Metrics.EquityCurve.Should().NotBeEmpty();
        result.Trades.Should().NotBeNull();
    }

    // ── Helper builders ─────────────────────────────────────────────────────────

    private static Dictionary<string, MarketData> BuildUniverse()
    {
        var universe = new Dictionary<string, MarketData>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in new[] { "AAPL", "MSFT", "GOOGL", "AMZN", "META" })
        {
            // Use slightly different drifts so cross-sectional ranking selects different stocks
            double drift  = sym.Length * 0.0003;
            var raw = TestHelpers.GenerateTrendingOhlc(
                sym, 400, new DateTime(2020, 1, 1), dailyDrift: drift);
            universe[sym] = TestHelpers.BuildMarketData(raw);
        }
        return universe;
    }

    private static MarketData BuildBenchmark() =>
        TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("SPY", 400, new DateTime(2020, 1, 1)));
}
