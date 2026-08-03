using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Interfaces;
using QuantEngine.Domain.Options;

namespace QuantEngine.Infrastructure.MarketData.Csv;

/// <summary>Zero-dependency CSV OHLC provider (Date,Open,High,Low,Close,AdjClose,Volume).</summary>
public sealed class CsvOhlcProvider : IOhlcProvider
{
    private readonly string _folder;
    private readonly ILogger<CsvOhlcProvider> _log;

    public CsvOhlcProvider(IOptions<DataOptions> opts, ILogger<CsvOhlcProvider> log)
    {
        _folder = opts.Value.CsvFolder;
        _log    = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task<OhlcData> GetOhlcAsync(
        string symbol, DateTime start, DateTime end, CancellationToken ct = default)
    {
        var path = Path.Combine(_folder, $"{symbol}.csv");
        if (!File.Exists(path))
        {
            _log.LogWarning("[CSV] {Path} not found", path);
            return Task.FromResult(OhlcData.Empty(symbol));
        }
        try
        {
            using var sr = new StreamReader(path);
            sr.ReadLine(); // skip header
            var dates = new List<DateTime>(4096); var open = new List<double>(4096);
            var high  = new List<double>(4096);   var low  = new List<double>(4096);
            var close = new List<double>(4096);   var vol  = new List<double>(4096);

            while (sr.ReadLine() is { } line)
            {
                var s = line.AsSpan();
                Span<Range> r = stackalloc Range[8];
                int count = s.Split(r, ',', StringSplitOptions.TrimEntries);
                if (count < 6) continue;
                if (!DateTime.TryParse(s[r[0]], out var date)) continue;
                if (date < start || date > end) continue;
                if (!TryPos(s[r[1]], out double o)) continue;
                if (!TryPos(s[r[2]], out double h)) continue;
                if (!TryPos(s[r[3]], out double l)) continue;
                if (!TryPos(s[r[5]], out double c) && !TryPos(s[r[4]], out c)) continue;
                double v = count >= 7 && TryPos(s[r[6]], out double vv) ? vv : 0;
                dates.Add(date); open.Add(o); high.Add(h); low.Add(l); close.Add(c); vol.Add(v);
            }
            return Task.FromResult(dates.Count == 0
                ? OhlcData.Empty(symbol)
                : new OhlcData(symbol, [..dates], [..open], [..high], [..low], [..close], [..vol]));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CSV] Parse error for {Symbol}", symbol);
            return Task.FromResult(OhlcData.Empty(symbol));
        }
    }

    private static bool TryPos(ReadOnlySpan<char> s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0;
}
