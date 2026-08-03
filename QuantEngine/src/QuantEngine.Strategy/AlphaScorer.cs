using System.Runtime.CompilerServices;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Options;
using QuantEngine.Domain.ValueObjects;
using QuantEngine.Indicators.Models;

namespace QuantEngine.Strategy;

/// <summary>
/// Evaluates a single symbol at a single bar and produces an entry signal with alpha score.
/// TRADING LOGIC INVARIANT: Evaluate must remain mathematically identical.
/// Score formula: Clamp(ADX*1.5 + Clamp(momentumGap, 0, 40), 0, 100)
/// where momentumGap = ((HmaFast - HmaSlow) / HmaSlow) * 1000
/// </summary>
public sealed class AlphaScorer
{
    private readonly StrategyOptions   _strategy;
    private readonly IndicatorsOptions _indicators;

    public AlphaScorer(StrategyOptions strategy, IndicatorsOptions indicators)
    {
        _strategy   = strategy;
        _indicators = indicators;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SignalEvaluation Evaluate(in MarketData data, int idx, RegimeState regime)
    {
        // Warmup guard — any NaN or un-warmed Supertrend → no signal
        if (double.IsNaN(data.HmaFast[idx]) || double.IsNaN(data.HmaSlow[idx]) ||
            double.IsNaN(data.Adx[idx])     || double.IsNaN(data.Atr[idx]) ||
            data.SuperTrendDir[idx] == 0)
            return default;

        // ATR guard — prevents degenerate stop = entry and ∞ qty sizing
        if (data.Atr[idx] <= double.Epsilon) return default;

        // Only long in Bull/Neutral — never in confirmed Bear
        if (regime == RegimeState.BearTrending) return default;

        bool aligned  = data.HmaFast[idx] > data.HmaSlow[idx] && data.SuperTrendDir[idx] == 1;
        bool strength = data.Adx[idx] > _indicators.AdxThreshold;
        if (!aligned || !strength) return default;

        double momentumGap = data.HmaSlow[idx] > 0
            ? ((data.HmaFast[idx] - data.HmaSlow[idx]) / data.HmaSlow[idx]) * 1000.0
            : 0;
        double score = Math.Clamp(
            data.Adx[idx] * 1.5 + Math.Clamp(momentumGap, 0, 40), 0, 100);

        if (score < _strategy.MinimumAlphaScore) return default;

        double px  = data.Close[idx];
        double atr = data.Atr[idx];

        return new SignalEvaluation(
            IsEntry:       true,
            AlphaScore:    score,
            EstStopLoss:   px - _strategy.StopLossAtrMultiple   * atr,
            EstTakeProfit: px + _strategy.TakeProfitAtrMultiple * atr);
    }
}
