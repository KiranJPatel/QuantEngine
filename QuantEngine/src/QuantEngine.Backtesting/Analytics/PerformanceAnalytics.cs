using QuantEngine.Domain.Entities;
using QuantEngine.Domain.ValueObjects;
using QuantEngine.Indicators.Models;

namespace QuantEngine.Backtesting.Analytics;

/// <summary>
/// Computes the full suite of risk-adjusted performance metrics.
/// TRADING LOGIC INVARIANT: all formulas (Sharpe, Sortino, Calmar, CAGR, WinRate,
/// ProfitFactor, MaxDrawdown) must remain mathematically identical.
/// </summary>
public static class PerformanceAnalytics
{
    public const double TradingDaysPerYear = 252.0;

    public static PerformanceMetrics Compute(
        IReadOnlyList<Trade> trades,
        double[]             equityCurve,
        double               initialCapital,
        DateTime             start,
        DateTime             end)
    {
        double finalEquity = equityCurve.Length > 0 ? equityCurve[^1] : initialCapital;
        double years       = Math.Max((end - start).TotalDays / 365.25, 0.001);
        double cagr        = initialCapital > 0
            ? Math.Pow(finalEquity / initialCapital, 1.0 / years) - 1.0 : 0;

        var returns = new double[Math.Max(equityCurve.Length - 1, 0)];
        for (int i = 1; i < equityCurve.Length; i++)
            returns[i - 1] = equityCurve[i - 1] > 0
                ? (equityCurve[i] - equityCurve[i - 1]) / equityCurve[i - 1] : 0;

        double maxDD  = CalcMaxDrawdown(equityCurve);
        double calmar = maxDD > 0 ? cagr / (maxDD / 100.0) : 0;

        int    total = trades.Count, winners = 0;
        double gWin  = 0, gLoss = 0, consLoss = 0, maxCons = 0;
        foreach (var t in trades)
        {
            if (t.NetPnl > 0) { winners++; gWin  += t.NetPnl;           consLoss = 0; }
            else               {            gLoss += Math.Abs(t.NetPnl); consLoss++;
                if (consLoss > maxCons) maxCons = consLoss; }
        }

        double losers  = total - winners;
        return new PerformanceMetrics(
            FinalEquity:          finalEquity,
            CAGR:                 cagr,
            SharpeRatio:          CalcSharpe(returns),
            SortinoRatio:         CalcSortino(returns),
            CalmarRatio:          calmar,
            MaxDrawdownPct:       maxDD,
            WinRate:              total > 0 ? (double)winners / total : 0,
            ProfitFactor:         gLoss > 0 ? gWin / gLoss : double.PositiveInfinity,
            AvgWin:               winners > 0  ? gWin  / winners : 0,
            AvgLoss:              losers  > 0  ? gLoss / losers  : 0,
            MaxConsecutiveLosses: (int)maxCons,
            TotalTrades:          total,
            WinningTrades:        winners,
            EquityCurve:          equityCurve);
    }

    public static double CalcSharpe(double[] returns)
    {
        if (returns.Length < 2) return 0;
        double sum = 0, sq = 0;
        foreach (double r in returns) { sum += r; sq += r * r; }
        double avg = sum / returns.Length;
        double std = Math.Sqrt(Math.Max(0, sq / returns.Length - avg * avg));
        return std > 1e-12 ? avg / std * Math.Sqrt(TradingDaysPerYear) : 0;
    }

    public static double CalcSortino(double[] returns)
    {
        if (returns.Length < 2) return 0;
        double sum = 0, dsq = 0;
        foreach (double r in returns) { sum += r; if (r < 0) dsq += r * r; }
        double dstd = Math.Sqrt(dsq / returns.Length);
        return dstd > 1e-12 ? (sum / returns.Length) / dstd * Math.Sqrt(TradingDaysPerYear) : 0;
    }

    public static double CalcMaxDrawdown(double[] curve)
    {
        if (curve.Length == 0) return 0;
        double peak = curve[0], maxDD = 0;
        foreach (double v in curve)
        {
            if (v > peak) peak = v;
            double dd = peak > 0 ? (peak - v) / peak * 100.0 : 0;
            if (dd > maxDD) maxDD = dd;
        }
        return maxDD;
    }

    public static BenchmarkResult ComputeBenchmark(MarketData bench, double initialCapital)
    {
        if (!bench.IsValid || bench.Length < 2) return new BenchmarkResult(0, 0, 0, 0);
        double sp = bench.Close[0], ep = bench.Close[^1];
        double totalReturn = ep / sp - 1.0;
        double years = Math.Max((bench.Dates[^1] - bench.Dates[0]).TotalDays / 365.25, 0.001);
        double cagr  = Math.Pow(ep / sp, 1.0 / years) - 1.0;
        var curve    = new double[bench.Length];
        var returns  = new double[bench.Length - 1];
        for (int i = 0; i < bench.Length; i++) curve[i] = initialCapital * (bench.Close[i] / sp);
        for (int i = 1; i < bench.Length; i++)
            returns[i - 1] = bench.Close[i - 1] > 0
                ? (bench.Close[i] - bench.Close[i - 1]) / bench.Close[i - 1] : 0;
        return new BenchmarkResult(cagr, CalcSharpe(returns), CalcMaxDrawdown(curve), totalReturn);
    }
}
