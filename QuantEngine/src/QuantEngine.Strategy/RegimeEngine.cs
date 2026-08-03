using System.Runtime.CompilerServices;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Options;
using QuantEngine.Indicators.Models;

namespace QuantEngine.Strategy;

/// <summary>
/// Determines the macro market regime using the benchmark's HMA cross and ADX strength.
/// TRADING LOGIC INVARIANT: GetRegime must remain mathematically identical.
/// adxThreshold is sourced from IndicatorsOptions so regime and scorer use the same value.
/// </summary>
public sealed class RegimeEngine
{
    private readonly MarketData _benchmark;
    private readonly double     _adxThreshold;

    public RegimeEngine(MarketData benchmark, IndicatorsOptions opts)
    {
        _benchmark    = benchmark;
        _adxThreshold = opts.AdxThreshold;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RegimeState GetRegime(int idx)
    {
        if ((uint)idx >= (uint)_benchmark.Length) return RegimeState.Neutral;
        if (double.IsNaN(_benchmark.HmaFast[idx]) || double.IsNaN(_benchmark.Adx[idx]))
            return RegimeState.Neutral;

        bool bull     = _benchmark.HmaFast[idx] > _benchmark.HmaSlow[idx];
        bool trending = _benchmark.Adx[idx] > _adxThreshold;

        if ( bull && trending) return RegimeState.BullTrending;
        if (!bull && trending) return RegimeState.BearTrending;
        return RegimeState.Neutral;
    }
}
