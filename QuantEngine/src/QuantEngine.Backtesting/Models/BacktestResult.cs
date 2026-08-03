using QuantEngine.Domain.Entities;
using QuantEngine.Domain.ValueObjects;
namespace QuantEngine.Backtesting.Models;
public sealed class BacktestResult
{
    public required string              RunId   { get; init; }
    public required IReadOnlyList<Trade> Trades  { get; init; }
    public required PerformanceMetrics  Metrics { get; init; }
}
