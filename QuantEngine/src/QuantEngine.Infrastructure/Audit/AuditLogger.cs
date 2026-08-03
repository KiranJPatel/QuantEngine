using System.Text;
using QuantEngine.Domain.Entities;

namespace QuantEngine.Infrastructure.Audit;

public sealed class AuditLogger : IDisposable
{
    private readonly StreamWriter  _sw;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AuditLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        bool exists = File.Exists(path);
        _sw = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
        if (!exists)
            _sw.WriteLine("Timestamp,RunId,Broker,Event,Symbol,Side,Type," +
                          "Qty,Price,TriggerPrice,OrderId,State,Reason,Detail");
    }

    public async Task LogOrderAsync(
        string runId, string broker, string ev, BrokerOrder o, string detail = "")
    {
        string line = $"{DateTime.UtcNow:O},{runId},{broker},{ev},{o.Symbol},{o.Side}," +
            $"{o.Type},{o.Quantity},{o.Price:F2},{o.TriggerPrice:F2}," +
            $"{o.OrderId},{o.State},{Esc(o.Reason)},{Esc(detail)}";
        await _lock.WaitAsync().ConfigureAwait(false);
        try { await _sw.WriteLineAsync(line).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    public async Task LogEventAsync(string runId, string broker, string ev, string detail)
    {
        string line = $"{DateTime.UtcNow:O},{runId},{broker},{ev},,,,,,,,,{Esc(detail)}";
        await _lock.WaitAsync().ConfigureAwait(false);
        try { await _sw.WriteLineAsync(line).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    private static string Esc(string s) =>
        s.Contains(',') ? $"\"{s.Replace("\"", "'")}\"" : s;

    public void Dispose() { _sw.Dispose(); _lock.Dispose(); }
}
