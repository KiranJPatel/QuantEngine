namespace QuantEngine.Domain.Utilities;

/// <summary>
/// Converts symbol formats between Yahoo Finance, Zerodha Kite, and Upstox API formats.
/// Pure utility — no external dependencies, safe to place in Domain.
/// </summary>
public static class SymbolMapper
{
    /// <summary>RELIANCE → RELIANCE.NS (Yahoo Finance NSE suffix).</summary>
    public static string ToYahoo(string symbol, string nseSuffix = ".NS")
        => symbol.Contains('.') ? symbol : symbol + nseSuffix;

    /// <summary>RELIANCE.NS → RELIANCE (strips the Yahoo suffix).</summary>
    public static string FromYahoo(string yahooSymbol)
        => yahooSymbol.Contains('.')
            ? yahooSymbol[..yahooSymbol.LastIndexOf('.')]
            : yahooSymbol;

    /// <summary>RELIANCE → NSE:RELIANCE (Zerodha REST quote parameter).</summary>
    public static string ToZerodhaQuote(string symbol, string exchange = "NSE")
        => $"{exchange}:{symbol.ToUpperInvariant()}";

    /// <summary>RELIANCE → NSE_EQ|RELIANCE (Upstox instrument_key).</summary>
    public static string ToUpstoxKey(string symbol, string exchange = "NSE_EQ")
        => $"{exchange}|{symbol.ToUpperInvariant()}";

    /// <summary>NSE_EQ|RELIANCE or NSE_EQ:RELIANCE → RELIANCE.</summary>
    public static string FromUpstoxKey(string key)
    {
        int idx = key.IndexOfAny(['|', ':']);
        return idx >= 0 ? key[(idx + 1)..] : key;
    }
}
