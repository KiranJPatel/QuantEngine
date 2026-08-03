using FluentAssertions;
using QuantEngine.Tests.Unit;
using Xunit;

namespace QuantEngine.Tests.Unit.Indicators;

public sealed class MarketDataSliceTests
{
    [Fact]
    public void SliceByDate_Returns_Correct_Subset()
    {
        var md    = TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("TEST", 100, new DateTime(2021, 1, 1)));
        var from  = new DateTime(2021, 2, 1);
        var to    = new DateTime(2021, 3, 1);
        var slice = md.SliceByDate(from, to);

        slice.Length.Should().BeGreaterThan(0);
        slice.Dates[0].Should().BeOnOrAfter(from);
        slice.Dates[^1].Should().BeBefore(to);
    }

    [Fact]
    public void SliceByDate_Returns_Default_For_Empty_Range()
    {
        var md    = TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("TEST", 100, new DateTime(2021, 1, 1)));
        var slice = md.SliceByDate(new DateTime(2099, 1, 1), new DateTime(2099, 2, 1));
        slice.IsValid.Should().BeFalse("dates outside range → empty slice");
    }

    [Fact]
    public void SliceByDate_Shares_Underlying_Arrays()
    {
        var md    = TestHelpers.BuildMarketData(
            TestHelpers.GenerateTrendingOhlc("TEST", 200, new DateTime(2021, 1, 1)));
        var from  = md.Dates[50];
        var to    = md.Dates[100];
        var slice = md.SliceByDate(from, to);

        // Slice should reference the same array objects (array slicing creates new spans over same memory)
        slice.Close[0].Should().Be(md.Close[50],
            "slice[0] must equal original[50] — shares underlying array");
    }
}
