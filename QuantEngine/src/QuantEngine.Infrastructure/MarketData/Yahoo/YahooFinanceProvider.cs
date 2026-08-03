using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Interfaces;
using QuantEngine.Domain.Options;
using QuantEngine.Infrastructure.MarketData.Cache;
using QuantEngine.Infrastructure.Utilities;

namespace QuantEngine.Infrastructure.MarketData.Yahoo;

/// <summary>
/// Yahoo Finance v8 OHLC provider with Cookie/Crumb session authentication,
/// retry via <see cref="RetryHelper"/> with exponential back-off, and atomic disk cache.
/// </summary>
public sealed class YahooFinanceProvider : IOhlcProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // Yahoo Finance JSON response models — explicit [JsonPropertyName] is required
    // because System.Text.Json defaults to case-sensitive deserialization.
    private record YahooRoot(
        [property: JsonPropertyName("chart")] YahooChart? Chart);
    private record YahooChart(
        [property: JsonPropertyName("result")] YahooResult[]? Result,
        [property: JsonPropertyName("error")]  object?        Error);
    private record YahooResult(
        [property: JsonPropertyName("timestamp")]  long[]?          Timestamp,
        [property: JsonPropertyName("indicators")] YahooIndicators? Indicators);
    private record YahooIndicators(
        [property: JsonPropertyName("quote")]    YahooQuote[]?    Quote,
        [property: JsonPropertyName("adjclose")] YahooAdjclose[]? Adjclose);
    private record YahooQuote(
        [property: JsonPropertyName("open")]   double?[]? Open,
        [property: JsonPropertyName("high")]   double?[]? High,
        [property: JsonPropertyName("low")]    double?[]? Low,
        [property: JsonPropertyName("close")]  double?[]? Close,
        [property: JsonPropertyName("volume")] double?[]? Volume);
    private record YahooAdjclose(
        [property: JsonPropertyName("adjclose")] double?[]? Values);

    private readonly HttpClient      _http;
    private readonly OhlcDiskCache   _cache;
    private readonly ILogger<YahooFinanceProvider> _log;
    private          string?         _crumb;
    private readonly SemaphoreSlim   _crumbLock = new(1, 1);

    public YahooFinanceProvider(
        IOptions<DataOptions>        opts,
        OhlcDiskCache                cache,
        ILogger<YahooFinanceProvider> log)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _log   = log   ?? throw new ArgumentNullException(nameof(log));
        var handler = new HttpClientHandler
        {
            CookieContainer        = new System.Net.CookieContainer(),
            UseCookies             = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept",
            "application/json,text/html,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    }

    public async Task<OhlcData> GetOhlcAsync(
        string symbol, DateTime start, DateTime end, CancellationToken ct = default)
    {
        long p1 = ((DateTimeOffset)DateTime.SpecifyKind(start, DateTimeKind.Utc)).ToUnixTimeSeconds();
        long p2 = ((DateTimeOffset)DateTime.SpecifyKind(end,   DateTimeKind.Utc)).ToUnixTimeSeconds();

        var cached = _cache.TryLoad(symbol, p1, p2);
        if (cached.HasValue && cached.Value.IsValid)
        {
            _log.LogDebug("[Yahoo] Cache hit {Symbol} ({N} bars)", symbol, cached.Value.Length);
            return cached.Value;
        }

        string crumbParam = string.Empty;
        try
        {
            var crumb = await AcquireCrumbAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(crumb))
                crumbParam = $"&crumb={Uri.EscapeDataString(crumb)}";
        }
        catch (Exception ex) { _log.LogWarning(ex, "[Yahoo] Crumb failed for {Sym}", symbol); }

        string url =
            $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}" +
            $"?period1={p1}&period2={p2}&interval=1d&events=history{crumbParam}";

        return await RetryHelper.ExecuteAsync(
            async (attempt, ct2) =>
            {
                using var resp = await _http.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, ct2).ConfigureAwait(false);

                // On 401: rotate crumb and let RetryHelper retry
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    await _crumbLock.WaitAsync(ct2).ConfigureAwait(false);
                    _crumb = null;
                    _crumbLock.Release();
                    throw new HttpRequestException("401 Unauthorized — rotating crumb")
                        { Data = { ["status"] = 401 } };
                }
                resp.EnsureSuccessStatusCode();

                var raw    = await resp.Content.ReadAsStringAsync(ct2).ConfigureAwait(false);
                var result = ParseResponse(raw, symbol);
                if (result.IsValid)
                {
                    _cache.Save(result, p1, p2);
                    _log.LogInformation("[Yahoo] {Symbol}: {N} bars", symbol, result.Length);
                }
                else _log.LogWarning("[Yahoo] {Symbol}: no valid bars parsed", symbol);
                return result;
            },
            maxAttempts:   4,
            baseDelay:     TimeSpan.FromSeconds(2),
            isTransient:   ex => ex is HttpRequestException httpEx &&
                                 (httpEx.StatusCode == HttpStatusCode.TooManyRequests ||
                                  httpEx.StatusCode == null ||   // network error
                                  httpEx.Data.Contains("status")), // crumb rotation
            log:           _log,
            operationName: $"Yahoo/{symbol}",
            ct:            ct)
        .ConfigureAwait(false);
    }

    private OhlcData ParseResponse(string json, string symbol)
    {
        try
        {
            var root  = JsonSerializer.Deserialize<YahooRoot>(json, JsonOpts);
            var res   = root?.Chart?.Result?.FirstOrDefault();
            var quote = res?.Indicators?.Quote?.FirstOrDefault();
            if (res?.Timestamp is null || quote is null) return OhlcData.Empty(symbol);

            var ts     = res.Timestamp;
            var adjArr = res.Indicators?.Adjclose?.FirstOrDefault()?.Values;
            int total  = ts.Length;

            // Two-pass: count valid rows first, size arrays exactly
            int valid = 0;
            for (int i = 0; i < total; i++)
                if ((quote.Open?[i] ?? 0) > 0 && (quote.High?[i] ?? 0) > 0 &&
                    (quote.Low?[i]  ?? 0) > 0 && ((adjArr?[i] ?? quote.Close?[i]) ?? 0) > 0)
                    valid++;

            if (valid == 0) return OhlcData.Empty(symbol);

            var dates = new DateTime[valid]; var open  = new double[valid];
            var high  = new double[valid];   var low   = new double[valid];
            var close = new double[valid];   var vol   = new double[valid];
            int k = 0;

            for (int i = 0; i < total; i++)
            {
                double o = quote.Open?[i]  ?? 0, h = quote.High?[i] ?? 0,
                       l = quote.Low?[i]   ?? 0,
                       c = (adjArr?[i] ?? quote.Close?[i]) ?? 0;
                if (o <= 0 || h <= 0 || l <= 0 || c <= 0) continue;
                dates[k] = DateTimeOffset.FromUnixTimeSeconds(ts[i]).UtcDateTime;
                open[k] = o; high[k] = h; low[k] = l; close[k] = c;
                vol[k]  = quote.Volume?[i] ?? 0;
                k++;
            }
            return new OhlcData(symbol, dates, open, high, low, close, vol);
        }
        catch (JsonException ex)
        {
            _log.LogError(ex, "[Yahoo] JSON parse error for {Symbol}", symbol);
            return OhlcData.Empty(symbol);
        }
    }

    private async Task<string?> AcquireCrumbAsync(CancellationToken ct)
    {
        if (_crumb is not null) return _crumb;
        await _crumbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_crumb is not null) return _crumb;
            try { await _http.GetAsync("https://fc.yahoo.com", ct).ConfigureAwait(false); }
            catch { /* seed cookies — non-fatal */ }
            using var r = await _http
                .GetAsync("https://query1.finance.yahoo.com/v1/test/getcrumb", ct)
                .ConfigureAwait(false);
            if (r.IsSuccessStatusCode)
            {
                _crumb = (await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
                _log.LogDebug("[Yahoo] Crumb acquired");
            }
            return _crumb;
        }
        finally { _crumbLock.Release(); }
    }

    public void Dispose() { _http.Dispose(); _crumbLock.Dispose(); }
}
