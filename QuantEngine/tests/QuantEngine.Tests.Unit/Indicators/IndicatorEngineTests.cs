using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using QuantEngine.Indicators;
using QuantEngine.Tests.Unit;
using Xunit;

namespace QuantEngine.Tests.Unit.Indicators;

public sealed class IndicatorEngineTests
{
    private readonly IndicatorEngine _engine =
        new(NullLogger<IndicatorEngine>.Instance);

    [Fact]
    public void Build_Returns_Default_For_Empty_OhlcData()
    {
        var result = _engine.Build(
            QuantEngine.Domain.Entities.OhlcData.Empty("TEST"),
            TestHelpers.DefaultIndOpts);
        result.IsValid.Should().BeFalse();
        result.Length.Should().Be(0);
    }

    [Fact]
    public void Build_Output_Length_Equals_Input_Length()
    {
        var raw    = TestHelpers.GenerateTrendingOhlc("TEST", 200, new DateTime(2020, 1, 1));
        var result = _engine.Build(raw, TestHelpers.DefaultIndOpts);
        result.Length.Should().Be(raw.Length, "indicator array length must equal input length");
        result.HmaFast.Length.Should().Be(raw.Length);
        result.HmaSlow.Length.Should().Be(raw.Length);
        result.Adx.Length.Should().Be(raw.Length);
        result.Atr.Length.Should().Be(raw.Length);
        result.SuperTrend.Length.Should().Be(raw.Length);
        result.SuperTrendDir.Length.Should().Be(raw.Length);
    }

    [Fact]
    public void Build_First_Bars_Are_NaN_During_Warmup()
    {
        var raw    = TestHelpers.GenerateTrendingOhlc("TEST", 200, new DateTime(2020, 1, 1));
        var result = _engine.Build(raw, TestHelpers.DefaultIndOpts);

        // HmaSlow = 50: first ~50 bars must be NaN
        result.HmaSlow[0].Should().Be(double.NaN, "first HmaSlow value must be NaN (warmup)");
        result.HmaSlow[5].Should().Be(double.NaN, "early HmaSlow must be NaN");
    }

    [Fact]
    public void Build_Later_Bars_Are_Valid_After_Warmup()
    {
        var raw    = TestHelpers.GenerateTrendingOhlc("TEST", 300, new DateTime(2020, 1, 1));
        var result = _engine.Build(raw, TestHelpers.DefaultIndOpts);

        // After HmaSlow (50) bars of warmup, values must be valid
        int warmupEnd = TestHelpers.DefaultIndOpts.HmaSlow + 20;
        double.IsNaN(result.HmaSlow[warmupEnd]).Should().BeFalse(
            "HmaSlow must have valid values after warmup");
        double.IsNaN(result.HmaFast[warmupEnd]).Should().BeFalse(
            "HmaFast must have valid values after warmup");
    }

    [Fact]
    public void Build_Atr_Is_Positive_For_Valid_Bars()
    {
        var raw    = TestHelpers.GenerateTrendingOhlc("TEST", 300, new DateTime(2020, 1, 1));
        var result = _engine.Build(raw, TestHelpers.DefaultIndOpts);

        int warmup = TestHelpers.DefaultIndOpts.SupertrendAtrPeriod + 5;
        for (int i = warmup; i < result.Length; i++)
            if (!double.IsNaN(result.Atr[i]))
                result.Atr[i].Should().BeGreaterThan(0,
                    $"ATR at bar {i} must be positive");
    }

    [Fact]
    public void Build_SuperTrendDir_Is_Plus1_Or_Minus1_Or_Zero()
    {
        var raw    = TestHelpers.GenerateTrendingOhlc("TEST", 300, new DateTime(2020, 1, 1));
        var result = _engine.Build(raw, TestHelpers.DefaultIndOpts);

        for (int i = 0; i < result.Length; i++)
            result.SuperTrendDir[i].Should().BeOneOf(0, 1, -1,
                $"SuperTrendDir at bar {i} must be 0, 1, or -1");
    }

    [Fact]
    public void Build_Preserves_Input_Dates_Unchanged()
    {
        var raw    = TestHelpers.GenerateTrendingOhlc("TEST", 100, new DateTime(2021, 6, 1));
        var result = _engine.Build(raw, TestHelpers.DefaultIndOpts);
        result.Dates[0].Should().Be(raw.Dates[0]);
        result.Dates[^1].Should().Be(raw.Dates[^1]);
    }

    [Fact]
    public void Build_Is_Deterministic_For_Same_Input()
    {
        var raw = TestHelpers.GenerateTrendingOhlc("TEST", 200, new DateTime(2020, 1, 1));
        var r1  = _engine.Build(raw, TestHelpers.DefaultIndOpts);
        var r2  = _engine.Build(raw, TestHelpers.DefaultIndOpts);
        r1.HmaFast.Should().BeEquivalentTo(r2.HmaFast);
        r1.Adx.Should().BeEquivalentTo(r2.Adx);
    }
}
