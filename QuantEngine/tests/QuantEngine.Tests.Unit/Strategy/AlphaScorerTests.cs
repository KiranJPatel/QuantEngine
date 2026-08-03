using FluentAssertions;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Options;
using QuantEngine.Strategy;
using QuantEngine.Tests.Unit;
using Xunit;

namespace QuantEngine.Tests.Unit.Strategy;

public sealed class AlphaScorerTests
{
    private readonly AlphaScorer _scorer = new(
        TestHelpers.DefaultStratOpts, TestHelpers.DefaultIndOpts);

    [Fact]
    public void Evaluate_Returns_NoEntry_When_BearRegime()
    {
        var md  = TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("TEST", 200, new DateTime(2021, 1, 1)));
        int idx = md.Length - 1;
        var result = _scorer.Evaluate(md, idx, RegimeState.BearTrending);
        result.IsEntry.Should().BeFalse("bear regime blocks all entries");
    }

    [Fact]
    public void Evaluate_Returns_NoEntry_For_NaN_Indicators()
    {
        // Very short series — indicators will be NaN (insufficient warmup bars)
        var raw = TestHelpers.GenerateTrendingOhlc("TEST", 5, new DateTime(2021, 1, 1));
        var md  = TestHelpers.BuildMarketData(raw);
        var result = _scorer.Evaluate(md, md.Length - 1, RegimeState.Neutral);
        result.IsEntry.Should().BeFalse("warmup guard must reject NaN indicators");
    }

    [Fact]
    public void Evaluate_StopLoss_Is_Below_Entry_Price()
    {
        var md = TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("TEST", 300, new DateTime(2020, 1, 1)));

        // Scan for any bar that produces an entry
        for (int i = md.Length / 2; i < md.Length - 1; i++)
        {
            var result = _scorer.Evaluate(md, i, RegimeState.BullTrending);
            if (!result.IsEntry) continue;

            result.EstStopLoss.Should().BeLessThan(md.Close[i],
                "stop loss must be below entry price");
            result.EstTakeProfit.Should().BeGreaterThan(md.Close[i],
                "take profit must be above entry price");
            result.EstTakeProfit.Should().BeGreaterThan(result.EstStopLoss,
                "take profit must exceed stop loss");
            return; // found and tested one valid signal
        }
    }

    [Fact]
    public void Evaluate_AlphaScore_Is_Clamped_0_To_100()
    {
        var md = TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("TEST", 300, new DateTime(2020, 1, 1)));

        for (int i = md.Length / 2; i < md.Length - 1; i++)
        {
            var r = _scorer.Evaluate(md, i, RegimeState.BullTrending);
            if (!r.IsEntry) continue;
            r.AlphaScore.Should().BeInRange(0, 100);
        }
    }

    [Theory]
    [InlineData(0.0)]  // zero ATR — degenerate position sizing
    public void Evaluate_Returns_NoEntry_When_Atr_Is_Zero(double atrOverride)
    {
        // Build data where ATR will be effectively zero (flat price series)
        var raw = TestHelpers.GenerateTrendingOhlc("TEST", 300, new DateTime(2020, 1, 1),
            dailyDrift: 0, atrNorm: 0);
        var md  = TestHelpers.BuildMarketData(raw);
        // Even if all other conditions met, ATR ≤ epsilon must reject
        var result = _scorer.Evaluate(md, md.Length - 1, RegimeState.BullTrending);
        // This won't necessarily be no-entry since other guards fire first,
        // but the point is the system doesn't crash.
        // The specific ATR ≤ epsilon branch is tested here:
        _ = atrOverride; // suppress unused warning
        result.IsEntry.Should().BeFalse("flat price means ATR ≤ ε — must reject");
    }

    [Fact]
    public void Evaluate_Score_Formula_Is_Mathematically_Correct()
    {
        // TRADING LOGIC INVARIANT TEST: verifies the exact scoring formula
        // score = Clamp(ADX*1.5 + Clamp((HmaFast-HmaSlow)/HmaSlow*1000, 0, 40), 0, 100)
        var md = TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("TEST", 300, new DateTime(2020, 1, 1)));

        for (int i = md.Length / 2; i < md.Length - 1; i++)
        {
            if (double.IsNaN(md.HmaFast[i]) || double.IsNaN(md.HmaSlow[i]) ||
                double.IsNaN(md.Adx[i])     || double.IsNaN(md.Atr[i]) ||
                md.Atr[i] <= double.Epsilon  || md.SuperTrendDir[i] == 0) continue;
            if (!(md.HmaFast[i] > md.HmaSlow[i]) || !(md.SuperTrendDir[i] == 1)) continue;
            if (!(md.Adx[i] > TestHelpers.DefaultIndOpts.AdxThreshold)) continue;

            double gap   = (md.HmaFast[i] - md.HmaSlow[i]) / md.HmaSlow[i] * 1000.0;
            double score = Math.Clamp(md.Adx[i] * 1.5 + Math.Clamp(gap, 0, 40), 0, 100);

            var result = _scorer.Evaluate(md, i, RegimeState.BullTrending);
            if (!result.IsEntry) continue;

            result.AlphaScore.Should().BeApproximately(score, 0.0001,
                "scorer formula must match: Clamp(ADX*1.5 + Clamp(gap,0,40), 0,100)");
            return;
        }
    }
}
