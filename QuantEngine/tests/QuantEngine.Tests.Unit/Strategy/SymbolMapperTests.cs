using FluentAssertions;
using QuantEngine.Domain.Utilities;
using Xunit;

namespace QuantEngine.Tests.Unit.Strategy;

public sealed class SymbolMapperTests
{
    [Theory]
    [InlineData("RELIANCE",    "RELIANCE.NS")]
    [InlineData("INFY",        "INFY.NS")]
    [InlineData("RELIANCE.NS", "RELIANCE.NS")]  // already has suffix — idempotent
    public void ToYahoo_Appends_Suffix_When_Missing(string input, string expected)
        => SymbolMapper.ToYahoo(input).Should().Be(expected);

    [Theory]
    [InlineData("RELIANCE.NS",  "RELIANCE")]
    [InlineData("TCS.BO",       "TCS")]
    [InlineData("PLAIN",        "PLAIN")]  // no suffix — unchanged
    public void FromYahoo_Strips_Suffix_Correctly(string input, string expected)
        => SymbolMapper.FromYahoo(input).Should().Be(expected);

    [Theory]
    [InlineData("RELIANCE", "NSE", "NSE:RELIANCE")]
    [InlineData("infy",     "BSE", "BSE:INFY")]    // lowercased → uppercased
    public void ToZerodhaQuote_Formats_Correctly(string sym, string exchange, string expected)
        => SymbolMapper.ToZerodhaQuote(sym, exchange).Should().Be(expected);

    [Theory]
    [InlineData("RELIANCE", "NSE_EQ", "NSE_EQ|RELIANCE")]
    [InlineData("tcs",      "BSE_EQ", "BSE_EQ|TCS")]
    public void ToUpstoxKey_Formats_Correctly(string sym, string exchange, string expected)
        => SymbolMapper.ToUpstoxKey(sym, exchange).Should().Be(expected);

    [Theory]
    [InlineData("NSE_EQ|RELIANCE", "RELIANCE")]
    [InlineData("NSE_EQ:INFY",     "INFY")]    // colon separator variant
    [InlineData("PLAIN",           "PLAIN")]   // no separator — unchanged
    public void FromUpstoxKey_Extracts_Symbol(string input, string expected)
        => SymbolMapper.FromUpstoxKey(input).Should().Be(expected);

    [Fact]
    public void ToYahoo_Then_FromYahoo_Is_Roundtrip()
    {
        const string sym = "BAJFINANCE";
        SymbolMapper.FromYahoo(SymbolMapper.ToYahoo(sym)).Should().Be(sym);
    }
}
