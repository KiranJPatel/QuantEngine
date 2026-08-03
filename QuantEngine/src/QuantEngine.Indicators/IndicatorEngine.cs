using Microsoft.Extensions.Logging;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Options;
using QuantEngine.Indicators.Models;
using Skender.Stock.Indicators;

namespace QuantEngine.Indicators;

/// <summary>
/// Builds all technical indicators from raw OHLC data using Skender.Stock.Indicators.
/// TRADING LOGIC INVARIANT: indicator calculations must remain mathematically identical
/// across all refactoring — do not change Skender API calls or result extraction.
/// </summary>
public sealed class IndicatorEngine
{
    private readonly ILogger<IndicatorEngine> _log;

    public IndicatorEngine(ILogger<IndicatorEngine> log) => _log = log;

    /// <summary>
    /// Builds a complete <see cref="MarketData"/> from raw OHLC using the supplied indicator config.
    /// Uses direct foreach-into-pre-allocated-array fills (avoids SelectIterator heap allocs).
    /// </summary>
    public MarketData Build(OhlcData raw, IndicatorsOptions cfg)
    {
        if (!raw.IsValid)
        {
            _log.LogWarning("IndicatorEngine.Build called with empty/invalid data for {Symbol}", raw.Symbol);
            return default;
        }

        int n = raw.Length;
        var quotes = new List<Quote>(n);
        for (int i = 0; i < n; i++)
            quotes.Add(new Quote
            {
                Date   = raw.Dates[i],
                Open   = (decimal)raw.Open[i],
                High   = (decimal)raw.High[i],
                Low    = (decimal)raw.Low[i],
                Close  = (decimal)raw.Close[i],
                Volume = (decimal)raw.Volume[i]
            });

        // ── Direct foreach fills — eliminates 4x SelectIterator allocations per call ──
        var hmaFast  = new double[n];
        var hmaSlow  = new double[n];
        var adx      = new double[n];
        var atr      = new double[n];
        var stLine   = new double[n];
        var stDir    = new int[n];

        int j = 0;
        foreach (var r in quotes.GetHma(cfg.HmaFast))
            hmaFast[j++] = r.Hma.HasValue ? (double)r.Hma.Value : double.NaN;

        j = 0;
        foreach (var r in quotes.GetHma(cfg.HmaSlow))
            hmaSlow[j++] = r.Hma.HasValue ? (double)r.Hma.Value : double.NaN;

        j = 0;
        foreach (var r in quotes.GetAdx(cfg.AdxPeriod))
            adx[j++] = r.Adx.HasValue ? (double)r.Adx.Value : double.NaN;

        j = 0;
        foreach (var r in quotes.GetAtr(cfg.SupertrendAtrPeriod))
            atr[j++] = r.Atr.HasValue ? (double)r.Atr.Value : double.NaN;

        j = 0;
        foreach (var r in quotes.GetSuperTrend(cfg.SupertrendAtrPeriod, cfg.SupertrendMultiplier))
        {
            if (r.SuperTrend.HasValue)
            {
                stLine[j] = (double)r.SuperTrend.Value;
                stDir[j]  = raw.Close[j] > stLine[j] ? 1 : -1;
            }
            else { stLine[j] = double.NaN; stDir[j] = 0; }
            j++;
        }

        _log.LogDebug("Built indicators for {Symbol}: {N} bars", raw.Symbol, n);

        return new MarketData(raw.Symbol, raw.Dates, raw.Open, raw.High, raw.Low,
            raw.Close, raw.Volume, hmaFast, hmaSlow, stLine, stDir, adx, atr);
    }
}
