using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Interfaces;
using QuantEngine.Domain.Options;

namespace QuantEngine.Infrastructure.Feeds.Zerodha;

/// <summary>
/// Zerodha Kite Ticker WebSocket feed.
/// Parses the proprietary binary packet format (quote mode = 44 bytes per instrument).
/// Big-endian int32 for all price/quantity fields; prices divided by 100.
/// </summary>
public sealed class ZerodhaWebSocketFeed : IMarketDataFeed
{
    private readonly ZerodhaOptions  _opts;
    private readonly ILogger<ZerodhaWebSocketFeed> _log;
    private          ClientWebSocket? _ws;

    /// <summary>
    /// Instrument token → symbol reverse-map.
    /// Populate from Zerodha instruments CSV: https://api.kite.trade/instruments/NSE
    /// </summary>
    public Dictionary<uint, string> TokenMap { get; } = new();

    public ZerodhaWebSocketFeed(
        IOptions<ZerodhaOptions> opts,
        ILogger<ZerodhaWebSocketFeed> log)
    {
        _opts = opts.Value; _log = log;
    }

    public async Task StartAsync(
        IEnumerable<string> symbols,
        Func<LiveQuote, Task> onQuote,
        CancellationToken ct)
    {
        var symList = symbols.ToList();
        _log.LogInformation("[ZerodhaWS] Connecting for {N} symbols", symList.Count);

        _ws = new ClientWebSocket();
        string url = $"wss://ws.kite.trade?api_key={_opts.ApiKey}&access_token={_opts.AccessToken}";
        await _ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
        _log.LogInformation("[ZerodhaWS] Connected");

        // In production populate TokenMap from the Zerodha instruments CSV so that
        // binary packets map back to readable symbols. Using placeholder tokens here.
        var tokens = TokenMap.Keys.ToArray();
        if (tokens.Length == 0)
        {
            _log.LogWarning("[ZerodhaWS] TokenMap is empty — no binary→symbol mapping. " +
                "Download instruments CSV and call TokenMap.Add(token, symbol) at startup.");
        }

        await SendJson(new { a = "subscribe", v = tokens }, ct).ConfigureAwait(false);
        await SendJson(new { a = "mode", v = new object[] { "quote", tokens } }, ct).ConfigureAwait(false);

        var buf = new byte[65536];
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            try
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType == WebSocketMessageType.Binary && result.Count >= 2)
                    ParseBinaryFrame(buf.AsSpan(0, result.Count), onQuote, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "[ZerodhaWS] Receive error"); break; }
        }
    }

    private async Task SendJson(object obj, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        await _ws!.SendAsync(new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Binary frame format (quote mode, 44 bytes per packet):
    /// [0-1] packet count (big-endian int16)
    /// Per packet: [0-1] length, [2-5] token, [6-9] ltp/100, [28-31] open/100,
    ///             [32-35] high/100, [36-39] low/100, [40-43] close/100, [16-19] volume
    /// </summary>
    private void ParseBinaryFrame(
        ReadOnlySpan<byte> frame,
        Func<LiveQuote, Task> onQuote,
        CancellationToken ct)
    {
        if (frame.Length < 2) return;
        int count  = BinaryPrimitives.ReadInt16BigEndian(frame);
        int offset = 2;

        for (int i = 0; i < count && offset + 2 <= frame.Length; i++)
        {
            int pkLen = BinaryPrimitives.ReadInt16BigEndian(frame[offset..]);
            offset += 2;
            if (pkLen < 8 || offset + pkLen > frame.Length) break;

            var pk    = frame[offset..(offset + pkLen)];
            offset   += pkLen;
            uint token = BinaryPrimitives.ReadUInt32BigEndian(pk);
            double ltp = BinaryPrimitives.ReadInt32BigEndian(pk[4..]) / 100.0;
            double open  = pkLen >= 44 ? BinaryPrimitives.ReadInt32BigEndian(pk[28..]) / 100.0 : ltp;
            double high  = pkLen >= 44 ? BinaryPrimitives.ReadInt32BigEndian(pk[32..]) / 100.0 : ltp;
            double low   = pkLen >= 44 ? BinaryPrimitives.ReadInt32BigEndian(pk[36..]) / 100.0 : ltp;
            double close = pkLen >= 44 ? BinaryPrimitives.ReadInt32BigEndian(pk[40..]) / 100.0 : ltp;
            long   vol   = pkLen >= 20 ? BinaryPrimitives.ReadInt32BigEndian(pk[16..])          : 0;
            string sym   = TokenMap.TryGetValue(token, out var s) ? s : token.ToString();
            _ = Task.Run(() => onQuote(new LiveQuote(sym, ltp, open, high, low, close, vol, DateTime.UtcNow)), ct);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_ws?.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws is not null) { await StopAsync(CancellationToken.None).ConfigureAwait(false); _ws.Dispose(); }
    }
}
