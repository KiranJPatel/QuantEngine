using Microsoft.Extensions.Options;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Options;

namespace QuantEngine.Host;

/// <summary>
/// Validates all strongly-typed options on startup before any data is loaded.
/// Fails fast with precise field-level error messages.
/// </summary>
public static class StartupValidator
{
    public static void Validate(
        DataOptions         data,
        IndicatorsOptions   ind,
        StrategyOptions     strat,
        RiskOptions         risk,
        BacktestOptions     bt,
        OptimizationOptions opt,
        LiveTradingOptions  live,
        BrokersOptions      brokers,
        AppMode             mode)
    {
        // Date range
        Guard(data.Start < data.End,
            $"Data.Start ({data.Start:d}) must precede Data.End ({data.End:d})");

        // Indicator periods
        Guard(ind.HmaFast >= 2,  $"Indicators.HmaFast must be ≥ 2 (got {ind.HmaFast})");
        Guard(ind.HmaSlow >= 2,  $"Indicators.HmaSlow must be ≥ 2 (got {ind.HmaSlow})");
        Guard(ind.HmaFast < ind.HmaSlow,
            $"Indicators.HmaFast ({ind.HmaFast}) must be < HmaSlow ({ind.HmaSlow})");
        Guard(ind.SupertrendAtrPeriod >= 1,
            $"Indicators.SupertrendAtrPeriod must be ≥ 1 (got {ind.SupertrendAtrPeriod})");
        Guard(ind.SupertrendMultiplier > 0,
            $"Indicators.SupertrendMultiplier must be > 0 (got {ind.SupertrendMultiplier})");
        Guard(ind.AdxPeriod >= 1,
            $"Indicators.AdxPeriod must be ≥ 1 (got {ind.AdxPeriod})");
        Guard(ind.AdxThreshold > 0,
            $"Indicators.AdxThreshold must be > 0 (got {ind.AdxThreshold})");

        // Strategy R:R
        Guard(strat.TakeProfitAtrMultiple > 0,
            $"Strategy.TakeProfitAtrMultiple must be > 0");
        Guard(strat.StopLossAtrMultiple > 0,
            $"Strategy.StopLossAtrMultiple must be > 0");
        Guard(strat.TrailingStopAtrMultiple > 0,
            $"Strategy.TrailingStopAtrMultiple must be > 0");
        Guard(strat.TakeProfitAtrMultiple > strat.StopLossAtrMultiple,
            $"Strategy.TakeProfitAtrMultiple ({strat.TakeProfitAtrMultiple}) must exceed " +
            $"StopLossAtrMultiple ({strat.StopLossAtrMultiple}) for positive R:R");
        Guard(strat.MinimumAlphaScore is >= 0 and <= 100,
            $"Strategy.MinimumAlphaScore must be in [0,100] (got {strat.MinimumAlphaScore})");

        // Risk
        Guard(risk.AccountRiskPerTradePct is > 0 and <= 0.10,
            $"Risk.AccountRiskPerTradePct must be in (0,0.10] (got {risk.AccountRiskPerTradePct})");
        Guard(risk.MaxOpenPositions >= 1,
            $"Risk.MaxOpenPositions must be ≥ 1");
        Guard(risk.MaxPortfolioHeat is > 0 and <= 1.0,
            $"Risk.MaxPortfolioHeat must be in (0,1.0]");
        Guard(risk.RegimeHeatPenalty is > 0 and <= 1.0,
            $"Risk.RegimeHeatPenalty must be in (0,1.0]");

        // Backtest
        Guard(bt.InitialCapital > 0,
            $"Backtest.InitialCapital must be > 0 (got {bt.InitialCapital})");
        Guard(bt.CommissionPerShare >= 0,
            $"Backtest.CommissionPerShare must be ≥ 0");
        Guard(bt.SlippageAtrFrac >= 0,
            $"Backtest.SlippageAtrFrac must be ≥ 0");

        // Optimization
        Guard(opt.HmaFastRange?.Length > 0, "Optimization.HmaFastRange cannot be empty");
        Guard(opt.HmaSlowRange?.Length > 0, "Optimization.HmaSlowRange cannot be empty");
        Guard(opt.TopN >= 1,
            $"Optimization.TopN must be ≥ 1 (got {opt.TopN})");
        Guard(opt.InSampleFraction is > 0 and < 1.0,
            $"Optimization.InSampleFraction must be in (0,1) (got {opt.InSampleFraction})");

        // Live trading
        if (mode == AppMode.LiveTrade)
        {
            Guard(live.MaxDailyLossINR > 0,   "LiveTrading.MaxDailyLossINR must be > 0");
            Guard(live.MaxOrderValueINR > 0,   "LiveTrading.MaxOrderValueINR must be > 0");
            Guard(live.OrderTimeoutSeconds > 0,"LiveTrading.OrderTimeoutSeconds must be > 0");

            if (brokers.ActiveBroker == BrokerType.Zerodha)
                Guard(!string.IsNullOrWhiteSpace(brokers.Zerodha.ApiKey),
                    "Brokers.Zerodha.ApiKey is required for live trading");
            else if (brokers.ActiveBroker == BrokerType.Upstox)
                Guard(!string.IsNullOrWhiteSpace(brokers.Upstox.ApiKey),
                    "Brokers.Upstox.ApiKey is required for live trading");
            else
                throw new InvalidOperationException(
                    "Brokers.ActiveBroker must be Zerodha or Upstox for LiveTrade mode");
        }
    }

    private static void Guard(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"[Config] {message}");
    }
}
