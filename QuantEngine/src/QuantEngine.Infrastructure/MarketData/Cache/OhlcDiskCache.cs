using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Options;

namespace QuantEngine.Infrastructure.MarketData.Cache;

/// <summary>
/// Atomic JSON disk cache for OHLC data.
/// Writes to a .tmp file then renames (atomic on same filesystem).
/// </summary>
public sealed class OhlcDiskCache
{
    private sealed class CacheEnvelope
    {
        public string     Symbol  { get; set; } = string.Empty;
        public DateTime[] Dates   { get; set; } = [];
        public double[]   Open    { get; set; } = [];
        public double[]   High    { get; set; } = [];
        public double[]   Low     { get; set; } = [];
        public double[]   Close   { get; set; } = [];
        public double[]   Volume  { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Opts =
        new() { PropertyNameCaseInsensitive = true };

    private readonly string _folder;
    private readonly int    _maxAgeDays;
    private readonly ILogger<OhlcDiskCache> _log;

    public OhlcDiskCache(IOptions<DataOptions> opts, ILogger<OhlcDiskCache> log)
    {
        _folder     = opts.Value.CacheFolder;
        _maxAgeDays = opts.Value.CacheMaxAgeDays;
        _log        = log;
        Directory.CreateDirectory(_folder);
    }

    private string CachePath(string symbol, long p1, long p2) =>
        Path.Combine(_folder, $"{symbol}_{p1}_{p2}.json");

    public OhlcData? TryLoad(string symbol, long period1, long period2)
    {
        var path = CachePath(symbol, period1, period2);
        if (!File.Exists(path)) return null;
        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalDays > _maxAgeDays) return null;
        try
        {
            var env = JsonSerializer.Deserialize<CacheEnvelope>(File.ReadAllText(path), Opts);
            if (env is null || env.Close.Length == 0) return null;
            return new OhlcData(env.Symbol, env.Dates, env.Open, env.High, env.Low, env.Close, env.Volume);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Cache] Corrupt cache for {Symbol} — will re-fetch", symbol);
            return null;
        }
    }

    public void Save(OhlcData d, long period1, long period2)
    {
        string final = CachePath(d.Symbol, period1, period2);
        string temp  = final + ".tmp";
        try
        {
            var env = new CacheEnvelope
            {
                Symbol = d.Symbol, Dates = d.Dates, Open = d.Open,
                High = d.High, Low = d.Low, Close = d.Close, Volume = d.Volume
            };
            File.WriteAllText(temp, JsonSerializer.Serialize(env));
            File.Move(temp, final, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Cache] Could not persist cache for {Symbol}", d.Symbol);
            try { File.Delete(temp); } catch { /* best-effort */ }
        }
    }
}
