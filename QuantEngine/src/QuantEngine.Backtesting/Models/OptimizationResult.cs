using QuantEngine.Domain.Options;
using QuantEngine.Domain.ValueObjects;
namespace QuantEngine.Backtesting.Models;
public sealed record OptimizationResult(
    IndicatorsOptions  Indicators,
    StrategyOptions    Strategy,
    PerformanceMetrics Metrics);
