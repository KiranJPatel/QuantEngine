using FluentAssertions;
using QuantEngine.Backtesting.Analytics;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Enums;
using Xunit;

namespace QuantEngine.Tests.Unit.Backtesting;

public sealed class PerformanceAnalyticsTests
{
    // ── Sharpe Ratio ────────────────────────────────────────────────────────────

    [Fact]
    public void Sharpe_Returns_Zero_For_Less_Than_Two_Returns()
    {
        PerformanceAnalytics.CalcSharpe([]).Should().Be(0);
        PerformanceAnalytics.CalcSharpe([0.01]).Should().Be(0);
    }

    [Fact]
    public void Sharpe_Returns_Zero_For_Zero_Variance_Returns()
    {
        var constReturns = Enumerable.Repeat(0.001, 100).ToArray();
        double sharpe = PerformanceAnalytics.CalcSharpe(constReturns);
        // std ≈ 0 → Sharpe approaches infinity but we return 0 for std ≤ 1e-12
        // Actually: if all returns are equal, avg/std = undefined. Our impl returns 0.
        sharpe.Should().Be(0, "zero-variance returns → std=0 → Sharpe=0 (guard clause)");
    }

    [Fact]
    public void Sharpe_Is_Annualised_By_Sqrt252()
    {
        // For returns with mean=μ and std=σ, Sharpe = μ/σ * sqrt(252)
        double mu = 0.001, sigma = 0.005;
        var rng  = new Random(42);
        // Use Box-Muller to generate N(mu,sigma) returns
        var returns = Enumerable.Range(0, 252)
            .Select(_ =>
            {
                double u1 = 1 - rng.NextDouble(), u2 = 1 - rng.NextDouble();
                return mu + sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            }).ToArray();
        double actual   = PerformanceAnalytics.CalcSharpe(returns);
        double mean     = returns.Average();
        double std      = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Average());
        double expected = mean / std * Math.Sqrt(252);
        actual.Should().BeApproximately(expected, 0.01);
    }

    // ── Sortino Ratio ───────────────────────────────────────────────────────────

    [Fact]
    public void Sortino_Uses_Only_Negative_Returns_For_Denominator()
    {
        // Sortino with all positive returns → downside std = 0 → returns 0 (guard)
        var allPositive = Enumerable.Repeat(0.002, 100).ToArray();
        PerformanceAnalytics.CalcSortino(allPositive).Should().Be(0);
    }

    [Fact]
    public void Sortino_Is_Greater_Than_Sharpe_When_No_Downside_Skew()
    {
        // For asymmetric returns favouring upside, Sortino > Sharpe
        var rng     = new Random(99);
        var returns = Enumerable.Range(0, 252)
            .Select(_ => rng.NextDouble() < 0.65 ? 0.003 : -0.001)
            .ToArray();
        double sharpe  = PerformanceAnalytics.CalcSharpe(returns);
        double sortino = PerformanceAnalytics.CalcSortino(returns);
        sortino.Should().BeGreaterThan(sharpe, "upside-skewed returns → Sortino > Sharpe");
    }

    // ── MaxDrawdown ─────────────────────────────────────────────────────────────

    [Fact]
    public void MaxDrawdown_Returns_Zero_For_Empty_Curve()
    {
        PerformanceAnalytics.CalcMaxDrawdown([]).Should().Be(0);
    }

    [Fact]
    public void MaxDrawdown_Computes_Correctly_For_Known_Curve()
    {
        // Equity: 100→120→90→110 → peak=120, trough=90 → DD = (120-90)/120*100 = 25%
        var curve = new double[] { 100, 120, 90, 110 };
        PerformanceAnalytics.CalcMaxDrawdown(curve)
            .Should().BeApproximately(25.0, 0.001);
    }

    [Fact]
    public void MaxDrawdown_Is_Always_Non_Negative()
    {
        var curve = new double[] { 100, 105, 110, 108, 115, 112, 120 };
        PerformanceAnalytics.CalcMaxDrawdown(curve).Should().BeGreaterOrEqualTo(0);
    }

    // ── CAGR / Compute ──────────────────────────────────────────────────────────

    [Fact]
    public void Compute_FinalEquity_Matches_Last_Curve_Element()
    {
        var curve  = new double[] { 100_000, 101_000, 105_000, 108_000 };
        var trades = new List<Trade>();
        var m      = PerformanceAnalytics.Compute(trades, curve, 100_000,
            new DateTime(2020, 1, 1), new DateTime(2021, 1, 1));
        m.FinalEquity.Should().Be(108_000);
    }

    [Fact]
    public void Compute_WinRate_Is_Correct()
    {
        var trades = new[]
        {
            new Trade("A", DateTime.Today, DateTime.Today, 10, 12, 100,  200, ExitReason.TakeProfit),
            new Trade("B", DateTime.Today, DateTime.Today, 10, 9,  100, -100, ExitReason.StopLoss),
            new Trade("C", DateTime.Today, DateTime.Today, 10, 11, 100,  100, ExitReason.TakeProfit),
            new Trade("D", DateTime.Today, DateTime.Today, 10, 8,  100, -200, ExitReason.StopLoss)
        };
        var curve = Enumerable.Range(0, 252).Select(i => 100_000 + i * 10.0).ToArray();
        var m     = PerformanceAnalytics.Compute(trades, curve, 100_000,
            new DateTime(2020, 1, 1), new DateTime(2021, 1, 1));

        m.WinRate.Should().BeApproximately(0.5, 0.001, "2 winners out of 4");
        m.TotalTrades.Should().Be(4);
        m.WinningTrades.Should().Be(2);
    }

    [Fact]
    public void Compute_ProfitFactor_Is_GrossWin_Over_GrossLoss()
    {
        var trades = new[]
        {
            new Trade("A", DateTime.Today, DateTime.Today, 10, 12, 100, 300, ExitReason.TakeProfit),
            new Trade("B", DateTime.Today, DateTime.Today, 10,  9, 100, -100, ExitReason.StopLoss)
        };
        var curve = Enumerable.Repeat(100_000.0, 252).ToArray();
        var m     = PerformanceAnalytics.Compute(trades, curve, 100_000,
            new DateTime(2020, 1, 1), new DateTime(2021, 1, 1));
        m.ProfitFactor.Should().BeApproximately(3.0, 0.001, "300 gross win / 100 gross loss = 3.0");
    }

    [Fact]
    public void Compute_MaxConsecutiveLosses_Counts_Streaks()
    {
        var trades = new[]
        {
            new Trade("A", DateTime.Today, DateTime.Today, 10, 12, 1,  10, ExitReason.TakeProfit),
            new Trade("B", DateTime.Today, DateTime.Today, 10,  9, 1, -10, ExitReason.StopLoss),
            new Trade("C", DateTime.Today, DateTime.Today, 10,  9, 1, -10, ExitReason.StopLoss),
            new Trade("D", DateTime.Today, DateTime.Today, 10,  9, 1, -10, ExitReason.StopLoss),
            new Trade("E", DateTime.Today, DateTime.Today, 10, 12, 1,  10, ExitReason.TakeProfit)
        };
        var curve = Enumerable.Repeat(100_000.0, 252).ToArray();
        var m     = PerformanceAnalytics.Compute(trades, curve, 100_000,
            new DateTime(2020, 1, 1), new DateTime(2021, 1, 1));
        m.MaxConsecutiveLosses.Should().Be(3);
    }
}
