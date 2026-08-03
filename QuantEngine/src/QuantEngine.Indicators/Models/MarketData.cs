using QuantEngine.Domain.Entities;
namespace QuantEngine.Indicators.Models;

/// <summary>
/// Immutable OHLCV plus computed indicator arrays for a single symbol.
/// SliceByDate returns a view sharing the same underlying arrays (no copy).
/// </summary>
public readonly record struct MarketData(
    string     Symbol,
    DateTime[] Dates,
    double[]   Open,
    double[]   High,
    double[]   Low,
    double[]   Close,
    double[]   Volume,
    double[]   HmaFast,
    double[]   HmaSlow,
    double[]   SuperTrend,
    int[]      SuperTrendDir,
    double[]   Adx,
    double[]   Atr)
{
    public int  Length  => Dates?.Length ?? 0;
    public bool IsValid => Length > 0;

    /// <summary>Date-range slice sharing the same underlying arrays — O(log n) binary search, zero allocation.</summary>
    public MarketData SliceByDate(DateTime fromInclusive, DateTime toExclusive)
    {
        if (!IsValid) return default;
        int s = Array.BinarySearch(Dates, fromInclusive); if (s < 0) s = ~s;
        int e = Array.BinarySearch(Dates, toExclusive);   if (e < 0) e = ~e;
        if (s >= e) return default;
        return new MarketData(Symbol,
            Dates[s..e], Open[s..e], High[s..e], Low[s..e], Close[s..e],
            Volume[s..e], HmaFast[s..e], HmaSlow[s..e], SuperTrend[s..e],
            SuperTrendDir[s..e], Adx[s..e], Atr[s..e]);
    }

    /// <summary>Extracts raw OHLCV into a new OhlcData (used by optimizer to rebuild with different indicator params).</summary>
    public OhlcData ToOhlcData() =>
        new(Symbol, Dates, Open, High, Low, Close, Volume);
}
