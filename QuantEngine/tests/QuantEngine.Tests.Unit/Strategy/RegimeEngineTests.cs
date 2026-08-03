using FluentAssertions;
using QuantEngine.Domain.Enums;
using QuantEngine.Strategy;
using QuantEngine.Tests.Unit;
using Xunit;

namespace QuantEngine.Tests.Unit.Strategy;

public sealed class RegimeEngineTests
{
    [Fact]
    public void GetRegime_Returns_Neutral_For_Out_Of_Bounds_Index()
    {
        var md     = TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("SPY", 300, new DateTime(2020, 1, 1)));
        var engine = new RegimeEngine(md, TestHelpers.DefaultIndOpts);

        engine.GetRegime(-1).Should().Be(RegimeState.Neutral);
        engine.GetRegime(md.Length + 99).Should().Be(RegimeState.Neutral);
    }

    [Fact]
    public void GetRegime_Returns_Neutral_When_Indicators_Are_NaN()
    {
        // Short series → warmup not complete → NaN indicators
        var md     = TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("SPY", 10, new DateTime(2020, 1, 1)));
        var engine = new RegimeEngine(md, TestHelpers.DefaultIndOpts);

        engine.GetRegime(md.Length - 1).Should().Be(RegimeState.Neutral,
            "NaN indicators must produce Neutral regime");
    }

    [Fact]
    public void GetRegime_Returns_BullTrending_For_Uptrend_With_High_Adx()
    {
        var md     = TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("SPY", 300, new DateTime(2020, 1, 1)));
        var engine = new RegimeEngine(md, TestHelpers.DefaultIndOpts);

        // After warmup, a strong uptrend should produce at least some BullTrending bars
        bool anyBull = Enumerable.Range(md.Length / 2, md.Length / 2)
            .Any(i => engine.GetRegime(i) == RegimeState.BullTrending);
        anyBull.Should().BeTrue("300-bar uptrend should produce BullTrending regime");
    }

    [Fact]
    public void GetRegime_Uses_Configured_AdxThreshold_Not_Hardcoded_20()
    {
        // TRADING LOGIC INVARIANT: regime and scorer must use the SAME AdxThreshold.
        // This was the v3→v4 bug: RegimeEngine was hard-coded to 20.0.
        var opts = TestHelpers.DefaultIndOpts with { AdxThreshold = 50.0 }; // very high
        var md   = TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("SPY", 300, new DateTime(2020, 1, 1)));
        var engine = new RegimeEngine(md, opts);

        // With threshold=50, no bar should reach BullTrending (typical ADX rarely hits 50)
        bool anyBull = Enumerable.Range(0, md.Length)
            .Any(i => engine.GetRegime(i) == RegimeState.BullTrending);
        anyBull.Should().BeFalse(
            "ADX threshold=50 should suppress all BullTrending — config must be respected");
    }
}
