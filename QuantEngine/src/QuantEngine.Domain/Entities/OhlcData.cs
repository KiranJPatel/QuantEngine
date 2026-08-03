namespace QuantEngine.Domain.Entities;

/// <summary>Immutable time-series OHLCV data for a single symbol.</summary>
public readonly record struct OhlcData(
    string     Symbol,
    DateTime[] Dates,
    double[]   Open,
    double[]   High,
    double[]   Low,
    double[]   Close,
    double[]   Volume)
{
    public int  Length  => Dates?.Length ?? 0;
    public bool IsValid => Length > 0 && Close?.Length == Length;
    public static OhlcData Empty(string symbol) =>
        new(symbol, [], [], [], [], [], []);
}
