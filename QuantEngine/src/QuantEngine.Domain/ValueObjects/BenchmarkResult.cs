namespace QuantEngine.Domain.ValueObjects;
public readonly record struct BenchmarkResult(
    double CAGR,
    double SharpeRatio,
    double MaxDrawdownPct,
    double TotalReturn);
