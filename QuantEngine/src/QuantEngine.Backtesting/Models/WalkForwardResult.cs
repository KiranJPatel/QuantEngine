using QuantEngine.Domain.Options;
using QuantEngine.Domain.ValueObjects;
namespace QuantEngine.Backtesting.Models;
public sealed record WalkForwardResult(
    IndicatorsOptions  BestIndicators,
    StrategyOptions    BestStrategy,
    PerformanceMetrics InSampleMetrics,
    PerformanceMetrics OutOfSampleMetrics);
