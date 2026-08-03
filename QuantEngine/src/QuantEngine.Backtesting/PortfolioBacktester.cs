using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Backtesting.Analytics;
using QuantEngine.Backtesting.Models;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Options;
using QuantEngine.Indicators.Models;
using QuantEngine.Strategy;

namespace QuantEngine.Backtesting;

/// <summary>
/// Cross-sectional portfolio backtester.
/// TRADING LOGIC INVARIANT: the entire simulation loop — exits, entries, MTM, exposure
/// tracking, trailing stop updates — must remain mathematically identical to v4.0.
/// Only structural/observability changes are permitted.
/// </summary>
public sealed class PortfolioBacktester
{
    private readonly IndicatorsOptions _indOpts;
    private readonly StrategyOptions   _stratOpts;
    private readonly RiskOptions       _riskOpts;
    private readonly BacktestOptions   _btOpts;
    private readonly DataOptions       _dataOpts;
    private readonly ILogger<PortfolioBacktester> _log;

    // ── Static comparer avoids delegate allocation on every hot-path sort call ──
    private static readonly IComparer<Candidate> ByScoreDesc =
        Comparer<Candidate>.Create(static (a, b) =>
            b.Eval.AlphaScore.CompareTo(a.Eval.AlphaScore));

    private readonly struct Candidate
    {
        public readonly string   Symbol;
        public readonly QuantEngine.Domain.ValueObjects.SignalEvaluation Eval;
        public readonly double   NextOpen;
        public readonly double   Atr;
        public Candidate(string s,
            in QuantEngine.Domain.ValueObjects.SignalEvaluation e,
            double no, double a)
        { Symbol = s; Eval = e; NextOpen = no; Atr = a; }
    }

    public PortfolioBacktester(
        IOptions<IndicatorsOptions> indOpts,
        IOptions<StrategyOptions>   stratOpts,
        IOptions<RiskOptions>       riskOpts,
        IOptions<BacktestOptions>   btOpts,
        IOptions<DataOptions>       dataOpts,
        ILogger<PortfolioBacktester> log)
    {
        _indOpts   = indOpts.Value;
        _stratOpts = stratOpts.Value;
        _riskOpts  = riskOpts.Value;
        _btOpts    = btOpts.Value;
        _dataOpts  = dataOpts.Value;
        _log       = log;
    }

    public BacktestResult RunCrossSectional(
        Dictionary<string, MarketData> universe,
        MarketData benchmark,
        string? runId = null)
    {
        runId ??= Guid.NewGuid().ToString();
        var regime = new RegimeEngine(benchmark, _indOpts);
        var scorer = new AlphaScorer(_stratOpts, _indOpts);
        var dates  = benchmark.Dates;
        int n      = dates.Length;

        _log.LogInformation(
            "[Backtest:{RunId}] Starting — {N} bars | {Sym} | Capital: {Cap:C}",
            runId[..8], n, benchmark.Symbol, _btOpts.InitialCapital);

        if (n < 3)
        {
            _log.LogWarning("[Backtest:{RunId}] Benchmark too short ({N} bars)", runId[..8], n);
            return new BacktestResult { RunId = runId, Trades = [],
                Metrics = PerformanceAnalytics.Compute([], [], _btOpts.InitialCapital,
                    _dataOpts.Start, _dataOpts.End) };
        }

        double equity = _btOpts.InitialCapital;
        double cash   = equity;
        var    curve  = new double[n];
        curve[0]      = equity;
        double peakEquity        = equity;
        double totalExposureCost = 0;   // O(1) incremental heat tracking

        var openPositions = new Dictionary<string, OpenPosition>(
            _riskOpts.MaxOpenPositions * 2, StringComparer.OrdinalIgnoreCase);
        var trades = new List<Trade>(2048);

        // ── Pre-compute O(1) date-alignment maps ──────────────────────────────────
        var symbols  = universe.Keys.ToArray();
        var alignMap = new Dictionary<string, int[]>(
            symbols.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var sym in symbols)
        {
            var ad  = universe[sym].Dates;
            var map = new int[n];
            for (int i = 0; i < n; i++)
                map[i] = Array.BinarySearch(ad, dates[i]);
            alignMap[sym] = map;
        }

        // ── Pre-allocated reusable hot-path buffers — zero heap allocs inside loop ─
        var candidates    = new List<Candidate>(universe.Count);
        var closedThisBar = new List<string>(_riskOpts.MaxOpenPositions + 4);

        // ════════════════════════════════════════════════════════════════════════════
        //  MAIN SIMULATION LOOP  —  TRADING LOGIC INVARIANT
        // ════════════════════════════════════════════════════════════════════════════
        for (int i = 1; i < n - 1; i++)
        {
            var reg      = regime.GetRegime(i);
            var today    = dates[i];
            var tomorrow = dates[i + 1];

            // ── EXIT PROCESSING ──────────────────────────────────────────────────
            closedThisBar.Clear();
            foreach (var pos in openPositions.Values)
            {
                if (!universe.TryGetValue(pos.Symbol, out var md)) continue;
                int idx = alignMap[pos.Symbol][i];
                if (idx < 0) continue;

                double hi  = md.High[idx], lo = md.Low[idx];
                double op  = md.Open[idx], atr = md.Atr[idx];

                if (hi > pos.HighestSinceEntry) pos.HighestSinceEntry = hi;
                if (!double.IsNaN(atr) && atr > 0)
                {
                    double nt = pos.HighestSinceEntry - _stratOpts.TrailingStopAtrMultiple * atr;
                    if (nt > pos.TrailingStop) pos.TrailingStop = nt;
                }

                bool   exitNow = false;
                double exitPx  = op;
                var    reason  = ExitReason.EndOfData;

                if (pos.TakeProfit > 0 && hi >= pos.TakeProfit)
                    { exitNow = true; exitPx = Math.Max(op, pos.TakeProfit); reason = ExitReason.TakeProfit; }
                else if (pos.StopLoss > 0 && lo <= pos.StopLoss)
                    { exitNow = true; exitPx = Math.Min(op, pos.StopLoss); reason = ExitReason.StopLoss; }
                else if (pos.TrailingStop > 0 && lo <= pos.TrailingStop)
                    { exitNow = true; exitPx = Math.Min(op, pos.TrailingStop); reason = ExitReason.TrailingStop; }
                else if (!double.IsNaN(md.HmaFast[idx]) && md.HmaFast[idx] < md.HmaSlow[idx])
                    { exitNow = true; exitPx = op; reason = ExitReason.TrendReversal; }

                if (!exitNow) continue;

                if (!double.IsNaN(atr) && atr > 0)
                    exitPx -= atr * _btOpts.SlippageAtrFrac;

                double gross = (exitPx - pos.EntryPrice) * pos.Quantity;
                double comms = pos.Quantity * _btOpts.CommissionPerShare * 2;
                double net   = gross - comms;

                equity            += net;
                cash              += pos.EntryPrice * pos.Quantity + net;
                totalExposureCost -= pos.EntryPrice * pos.Quantity;
                trades.Add(new Trade(pos.Symbol, pos.EntryDate, today,
                    pos.EntryPrice, exitPx, pos.Quantity, net, reason));
                closedThisBar.Add(pos.Symbol);

                _log.LogDebug(
                    "[Backtest:{RunId}] EXIT {Sym} {Reason} @ {Px:F2} Net={Net:F2}",
                    runId[..8], pos.Symbol, reason, exitPx, net);
            }

            var closedSpan = CollectionsMarshal.AsSpan(closedThisBar);
            for (int k = 0; k < closedSpan.Length; k++)
                openPositions.Remove(closedSpan[k]);

            // ── ENTRY RANKING ────────────────────────────────────────────────────
            double heatCap = _riskOpts.MaxPortfolioHeat *
                (reg == RegimeState.BullTrending ? 1.0 : _riskOpts.RegimeHeatPenalty);
            double heat    = equity > 0 ? totalExposureCost / equity : 0;

            if (heat < heatCap && openPositions.Count < _riskOpts.MaxOpenPositions)
            {
                candidates.Clear();
                for (int s = 0; s < symbols.Length; s++)
                {
                    var sym = symbols[s];
                    if (openPositions.ContainsKey(sym) ||
                        string.Equals(sym, benchmark.Symbol, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int idx = alignMap[sym][i];
                    if (idx < 0 || idx >= universe[sym].Length - 1) continue;
                    var assetData = universe[sym];
                    var eval = scorer.Evaluate(assetData, idx, reg);
                    if (eval.IsEntry)
                        candidates.Add(new Candidate(sym, eval,
                            assetData.Open[idx + 1], assetData.Atr[idx]));
                }

                if (candidates.Count > 1) candidates.Sort(ByScoreDesc);

                var candSpan = CollectionsMarshal.AsSpan(candidates);
                for (int c = 0; c < candSpan.Length; c++)
                {
                    if (heat >= heatCap || openPositions.Count >= _riskOpts.MaxOpenPositions) break;
                    ref readonly var cand = ref candSpan[c];
                    if (cand.Atr <= double.Epsilon) continue;

                    double rps = cand.NextOpen - cand.Eval.EstStopLoss;
                    if (rps <= 0) continue;
                    int qty = (int)(equity * _riskOpts.AccountRiskPerTradePct / rps);
                    if (qty * cand.NextOpen > cash) qty = (int)(cash / cand.NextOpen);
                    if (qty <= 0) continue;

                    double entryPx = cand.NextOpen + cand.Atr * _btOpts.SlippageAtrFrac;
                    openPositions[cand.Symbol] = new OpenPosition
                    {
                        Symbol = cand.Symbol, Quantity = qty, EntryPrice = entryPx,
                        EntryDate = tomorrow, StopLoss = cand.Eval.EstStopLoss,
                        TakeProfit = cand.Eval.EstTakeProfit,
                        HighestSinceEntry = entryPx, TrailingStop = 0
                    };
                    cash              -= qty * cand.NextOpen;
                    totalExposureCost += entryPx * qty;
                    heat               = equity > 0 ? totalExposureCost / equity : 0;

                    _log.LogDebug(
                        "[Backtest:{RunId}] ENTRY {Sym} {Qty}@{Px:F2} Score={Score:F1}",
                        runId[..8], cand.Symbol, qty, entryPx, cand.Eval.AlphaScore);
                }
            }

            // ── MARK-TO-MARKET ───────────────────────────────────────────────────
            double mtm = cash;
            foreach (var pos in openPositions.Values)
            {
                if (!universe.TryGetValue(pos.Symbol, out var md)) continue;
                int idx = alignMap[pos.Symbol][i];
                if (idx >= 0) mtm += pos.Quantity * md.Close[idx];
            }
            curve[i] = mtm;
            if (mtm > peakEquity) peakEquity = mtm;
        }

        // ── FORCE LIQUIDATE AT EOD ───────────────────────────────────────────────
        int      eodIdx  = n - 1;
        DateTime eodDate = dates[eodIdx];
        foreach (var pos in openPositions.Values)
        {
            if (!universe.TryGetValue(pos.Symbol, out var md)) continue;
            int idx = alignMap[pos.Symbol][eodIdx];
            if (idx < 0) continue;
            double px  = md.Close[idx];
            double net = (px - pos.EntryPrice) * pos.Quantity
                         - pos.Quantity * _btOpts.CommissionPerShare * 2;
            equity += net;
            trades.Add(new Trade(pos.Symbol, pos.EntryDate, eodDate,
                pos.EntryPrice, px, pos.Quantity, net, ExitReason.EndOfData));
        }
        curve[eodIdx] = equity;

        var metrics = PerformanceAnalytics.Compute(
            trades, curve, _btOpts.InitialCapital, _dataOpts.Start, _dataOpts.End);

        _log.LogInformation(
            "[Backtest:{RunId}] Done — Trades:{T} Sharpe:{S:F2} Sortino:{So:F2} " +
            "Calmar:{Ca:F2} MaxDD:{D:F1}% CAGR:{G:P1}",
            runId[..8], trades.Count, metrics.SharpeRatio, metrics.SortinoRatio,
            metrics.CalmarRatio, metrics.MaxDrawdownPct, metrics.CAGR);

        return new BacktestResult { RunId = runId, Trades = trades, Metrics = metrics };
    }
}
