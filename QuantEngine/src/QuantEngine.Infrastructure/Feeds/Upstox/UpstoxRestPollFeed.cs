using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Interfaces;
using QuantEngine.Domain.Options;

namespace QuantEngine.Infrastructure.Feeds.Upstox;

/// <summary>
/// Upstox REST polling feed (reliable fallback for Upstox which uses protobuf WebSocket).
/// To enable the full WebSocket: add Google.Protobuf package and implement using the
/// Upstox market-data-feed .proto schema at https://github.com/upstox/upstox-python.
/// </summary>
public sealed class UpstoxRestPollFeed : IMarketDataFeed
{
    private readonly IBroker   _broker;
    private readonly int       _intervalSec;
    private readonly ILogger<UpstoxRestPollFeed> _log;

    public UpstoxRestPollFeed(IBroker broker,
        IOptions<LiveTradingOptions> opts,
        ILogger<UpstoxRestPollFeed> log)
    {
        _broker      = broker;
        _intervalSec = Math.Max(1, opts.Value.PricePollingIntervalSeconds);
        _log         = log;
    }

    public async Task StartAsync(
        IEnumerable<string> symbols,
        Func<LiveQuote, Task> onQuote,
        CancellationToken ct)
    {
        var symList = symbols.ToList();
        _log.LogInformation("[UpstoxPoll] Polling {N} symbols every {S}s",
            symList.Count, _intervalSec);

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var ltps = await _broker.GetLtpAsync(symList, ct).ConfigureAwait(false);
                    var now  = DateTime.UtcNow;
                    foreach (var (sym, ltp) in ltps)
                        await onQuote(new LiveQuote(sym, ltp, ltp, ltp, ltp, ltp, 0, now))
                            .ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "[UpstoxPoll] Poll error");
                }
                await Task.Delay(TimeSpan.FromSeconds(_intervalSec), ct).ConfigureAwait(false);
            }
        }, ct);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
