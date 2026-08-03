using QuantEngine.Domain.Entities;
using QuantEngine.Domain.ValueObjects;
namespace QuantEngine.Domain.Interfaces;
public interface IBacktestRepository
{
    Task SaveRunAsync(string runId, string configJson, IReadOnlyList<Trade> trades,
        PerformanceMetrics metrics, DateTime start, DateTime end, CancellationToken ct);
}
