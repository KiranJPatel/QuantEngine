using QuantEngine.Domain.Entities;
namespace QuantEngine.Domain.Interfaces;
public interface IMarketDataFeed : IAsyncDisposable
{
    Task StartAsync(IEnumerable<string> symbols,
        Func<LiveQuote, Task> onQuote, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
