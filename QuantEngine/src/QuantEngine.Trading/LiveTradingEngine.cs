using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Interfaces;
using QuantEngine.Domain.Options;
using QuantEngine.Domain.Utilities;
using QuantEngine.Domain.ValueObjects;
using QuantEngine.Indicators;
using QuantEngine.Indicators.Models;
using QuantEngine.Risk;
using QuantEngine.Strategy;

namespace QuantEngine.Trading;

/// <summary>
/// Orchestrates the full intraday live trading loop:
/// auth → load positions → fetch historical → build indicators →
/// generate signals → wait for open → place entries → monitor → square-off.
/// </summary>
public sealed class LiveTradingEngine
{
    private readonly IBroker             _broker;
    private readonly IMarketDataFeed     _feed;
    private readonly IOhlcProvider       _ohlc;
    private readonly IndicatorEngine     _indicators;
    private readonly LiveRiskManager     _risk;
    private readonly LiveTradingOptions  _liveOpts;
    private readonly IndicatorsOptions   _indOpts;
    private readonly StrategyOptions     _stratOpts;
    private readonly RiskOptions         _riskOpts;
    private readonly BacktestOptions     _btOpts;
    private readonly ILogger<LiveTradingEngine> _log;

    private readonly ConcurrentDictionary<string, LiveQuote> _quotes   = new();
    private readonly LivePositionManager                     _positions;
    private readonly OrderManagementSystem                   _oms;

    public LiveTradingEngine(
        IBroker                        broker,
        IMarketDataFeed                feed,
        IOhlcProvider                  ohlc,
        IndicatorEngine                indicators,
        LiveRiskManager                risk,
        Infrastructure.Audit.AuditLogger audit,
        string                         runId,
        IOptions<LiveTradingOptions>   liveOpts,
        IOptions<IndicatorsOptions>    indOpts,
        IOptions<StrategyOptions>      stratOpts,
        IOptions<RiskOptions>          riskOpts,
        IOptions<BacktestOptions>      btOpts,
        ILogger<LiveTradingEngine>     log,
        ILogger<LivePositionManager>   posLog,
        ILogger<OrderManagementSystem> omsLog)
    {
        _broker     = broker; _feed = feed; _ohlc = ohlc;
        _indicators = indicators; _risk = risk;
        _liveOpts   = liveOpts.Value; _indOpts   = indOpts.Value;
        _stratOpts  = stratOpts.Value; _riskOpts  = riskOpts.Value;
        _btOpts     = btOpts.Value; _log = log;
        _positions  = new LivePositionManager(posLog);
        _oms        = new OrderManagementSystem(broker, risk, audit, runId,
            _liveOpts.OrderTimeoutSeconds, omsLog);
    }

    public async Task RunAsync(string[] symbols, CancellationToken ct)
    {
        _log.LogInformation("═══ Live Trading | Broker: {B} ═══", _broker.BrokerType);

        if (!await _broker.AuthenticateAsync(ct).ConfigureAwait(false))
        { _log.LogCritical("Authentication failed"); return; }

        _positions.Sync(await _broker.GetPositionsAsync(ct).ConfigureAwait(false));

        // ── Historical data + indicators ─────────────────────────────────────
        var suffix = _liveOpts.YahooNseSuffix;
        var end    = MarketSchedule.NowIst().Date.AddDays(-1);
        var start  = end.AddDays(-(_liveOpts.HistoricalBarsForSignals + 100));

        using var throttle = new SemaphoreSlim(4, 4);
        var rawData = await Task.WhenAll(symbols.Select(async sym =>
        {
            await throttle.WaitAsync(ct); try
            { return await _ohlc.GetOhlcAsync(SymbolMapper.ToYahoo(sym, suffix), start, end, ct); }
            finally { throttle.Release(); }
        })).ConfigureAwait(false);

        var universe  = new Dictionary<string, MarketData>(symbols.Length, StringComparer.OrdinalIgnoreCase);
        MarketData benchmark = default;
        foreach (var raw in rawData)
        {
            if (!raw.IsValid) continue;
            var clean = SymbolMapper.FromYahoo(raw.Symbol);
            var md    = _indicators.Build(raw, _indOpts);
            universe[clean] = md;
            if (raw.Symbol.StartsWith(_indOpts.AdxThreshold.ToString(), StringComparison.OrdinalIgnoreCase))
                benchmark = md;
        }
        // Identify benchmark by matching symbol suffix-stripped
        foreach (var kvp in universe)
            if (SymbolMapper.ToYahoo(kvp.Key, suffix).Equals(
                SymbolMapper.ToYahoo("SPY", suffix), StringComparison.OrdinalIgnoreCase))
                benchmark = kvp.Value;

        if (!benchmark.IsValid) { _log.LogError("Benchmark data missing"); return; }

        // ── Signals ──────────────────────────────────────────────────────────
        var regime  = new RegimeEngine(benchmark, _indOpts);
        var scorer  = new AlphaScorer(_stratOpts, _indOpts);
        var reg     = regime.GetRegime(benchmark.Length - 1);
        var funds   = await _broker.GetFundsAsync(ct).ConfigureAwait(false);
        double equity = funds.TotalBalance;

        var signals = new List<(string Sym, SignalEvaluation Eval)>();
        foreach (var kvp in universe)
        {
            if (_positions.All.Any(p => string.Equals(p.Symbol, kvp.Key, StringComparison.OrdinalIgnoreCase)))
                continue;
            int idx = kvp.Value.Length - 1; if (idx < 0) continue;
            var eval = scorer.Evaluate(kvp.Value, idx, reg);
            if (eval.IsEntry) signals.Add((kvp.Key, eval));
        }
        signals.Sort((a, b) => b.Eval.AlphaScore.CompareTo(a.Eval.AlphaScore));
        _log.LogInformation("[Live] Regime={R} | Signals={N} | Equity={E:C}", reg, signals.Count, equity);

        // ── Start feed ───────────────────────────────────────────────────────
        _ = _feed.StartAsync(symbols, async q =>
        {
            _quotes[q.Symbol] = q;
            _positions.UpdatePrice(q.Symbol, q.LastPrice);
            await CheckTpAsync(q, ct).ConfigureAwait(false);
        }, ct);

        // ── Wait for market open ─────────────────────────────────────────────
        if (!MarketSchedule.IsMarketOpen())
        {
            var wait = MarketSchedule.TimeUntilOpen();
            _log.LogInformation("[Live] Opens in {M:F0} min", wait.TotalMinutes);
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }

        // ── Place entries ────────────────────────────────────────────────────
        int slots = _riskOpts.MaxOpenPositions - _positions.All.Count;
        foreach (var (sym, eval) in signals.Take(slots))
        {
            if (ct.IsCancellationRequested) break;
            var ltps = await _broker.GetLtpAsync([sym], ct).ConfigureAwait(false);
            double ltp = ltps.TryGetValue(sym, out double p) ? p : 0;
            if (ltp <= 0) continue;

            double rps = ltp - eval.EstStopLoss;
            if (rps <= double.Epsilon) continue;
            int qty = (int)(equity * _riskOpts.AccountRiskPerTradePct / rps);
            if (qty * ltp > _liveOpts.MaxOrderValueINR) qty = (int)(_liveOpts.MaxOrderValueINR / ltp);
            if (qty <= 0) continue;

            var req    = new OrderRequest(sym, OrderSide.Buy, OrderType.Market, qty, 0, 0, "QE_ENTRY");
            var filled = await _oms.SubmitAsync(req, ct).ConfigureAwait(false);
            if (filled.State == OrderState.Filled)
            {
                double fillPx = filled.AvgFillPrice > 0 ? filled.AvgFillPrice : ltp;
                _log.LogInformation("[Live] FILLED {Sym}×{Qty}@{Px:F2}", sym, qty, fillPx);
                var slReq = new OrderRequest(sym, OrderSide.Sell, OrderType.StopLossMarket, qty, 0, eval.EstStopLoss, "QE_SL");
                var slOrd = await _broker.PlaceOrderAsync(slReq, ct).ConfigureAwait(false);
                _positions.RegisterStopOrder(sym, slOrd.OrderId);
            }
        }

        // ── Intraday monitor ─────────────────────────────────────────────────
        while (!ct.IsCancellationRequested && MarketSchedule.IsMarketOpen())
        {
            if (_liveOpts.EnableAutoSquareOff && MarketSchedule.IsWithinSquareOffWindow())
            {
                _log.LogWarning("[Live] Square-off window — liquidating");
                await SquareOffAllAsync(ct).ConfigureAwait(false);
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(_liveOpts.PricePollingIntervalSeconds), ct)
                .ConfigureAwait(false);
        }

        await _oms.CancelAllAsync(ct).ConfigureAwait(false);
        await _feed.StopAsync(ct).ConfigureAwait(false);
        _log.LogInformation("[Live] Session ended | Daily P&L: {P:C}", _risk.DailyPnl);
    }

    private async Task CheckTpAsync(LiveQuote q, CancellationToken ct)
    {
        var pos = _positions.All.FirstOrDefault(
            p => string.Equals(p.Symbol, q.Symbol, StringComparison.OrdinalIgnoreCase));
        if (pos.Symbol is null) return;
        // Basic TP: if price rose > TakeProfitAtrMultiple * assumed ATR
        double tpEst = pos.AveragePrice * (1 + _stratOpts.TakeProfitAtrMultiple * 0.015);
        if (q.LastPrice >= tpEst)
        {
            _log.LogInformation("[Live] TP reached {Sym} @ {Px:F2}", q.Symbol, q.LastPrice);
            await ExitAsync(pos, "TP", ct).ConfigureAwait(false);
        }
    }

    private async Task ExitAsync(BrokerPosition pos, string reason, CancellationToken ct)
    {
        if (_positions.TryGetStopOrderId(pos.Symbol, out var slId))
            await _broker.CancelOrderAsync(slId, ct).ConfigureAwait(false);
        var req    = new OrderRequest(pos.Symbol, OrderSide.Sell, OrderType.Market,
            Math.Abs(pos.Quantity), 0, 0, $"QE_{reason}");
        var filled = await _oms.SubmitAsync(req, ct).ConfigureAwait(false);
        if (filled.State == OrderState.Filled)
        {
            double pnl = (filled.AvgFillPrice - pos.AveragePrice) * Math.Abs(pos.Quantity);
            await _risk.RecordRealisedPnlAsync(pnl, pos.Symbol).ConfigureAwait(false);
            _positions.RemovePosition(pos.Symbol);
            _log.LogInformation("[Live] EXIT {Reason} {Sym} P&L={P:C}", reason, pos.Symbol, pnl);
        }
    }

    private async Task SquareOffAllAsync(CancellationToken ct)
    {
        foreach (var pos in _positions.All.ToList())
            if (pos.Quantity > 0) await ExitAsync(pos, "SQOFF", ct).ConfigureAwait(false);
    }
}
