namespace QuantEngine.Domain.ValueObjects;

/// <summary>Complete set of risk-adjusted performance metrics from a backtest or live run.</summary>
public readonly record struct PerformanceMetrics(
    double   FinalEquity,
    double   CAGR,
    double   SharpeRatio,
    double   SortinoRatio,
    double   CalmarRatio,
    double   MaxDrawdownPct,
    double   WinRate,
    double   ProfitFactor,
    double   AvgWin,
    double   AvgLoss,
    int      MaxConsecutiveLosses,
    int      TotalTrades,
    int      WinningTrades,
    double[] EquityCurve);
