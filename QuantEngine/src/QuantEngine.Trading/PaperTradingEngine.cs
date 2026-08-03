using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Options;
using QuantEngine.Domain.Utilities;
using QuantEngine.Domain.ValueObjects;
using QuantEngine.Indicators.Models;
using QuantEngine.Strategy;

namespace QuantEngine.Trading;

/// <summary>
/// Paper trading engine with persistent JSON state.
/// On each daily run:
///   1. Load paper_state.json (positions, cash, exit log)
///   2. Check exit conditions for existing positions using latest bar
///   3. Update trailing stops in-place
///   4. Score new entry candidates
///   5. Size and add positions up to risk limits
///   6. Save updated state atomically
///   7. Print comprehensive console dashboard
/// </summary>
public sealed class PaperTradingEngine
{
    private const string   StateFile       = "paper_state.json";
    private const string   StateTempFile   = "paper_state.json.tmp";
    private const int      MaxExitLogLines = 200;

    private static readonly JsonSerializerOptions JsonOpts = new()
        { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly StrategyOptions   _strat;
    private readonly RiskOptions       _risk;
    private readonly BacktestOptions   _bt;
    private readonly IndicatorsOptions _ind;
    private readonly ILogger<PaperTradingEngine> _log;

    private sealed class PaperState
    {
        public List<PaperPosition> Positions   { get; set; } = [];
        public List<string>        ExitLog     { get; set; } = [];
        public double              CashBalance { get; set; } = 1_000_000;
        public DateTime            LastUpdated { get; set; } = DateTime.MinValue;

        public void AppendExit(string line)
        {
            ExitLog.Add(line);
            if (ExitLog.Count > MaxExitLogLines)
                ExitLog.RemoveRange(0, ExitLog.Count - MaxExitLogLines);
        }
    }

    private record ExitEvent(PaperPosition Pos, double ExitPrice, ExitReason Reason, double NetPnl);

    public PaperTradingEngine(
        IOptions<StrategyOptions>   strat,
        IOptions<RiskOptions>       risk,
        IOptions<BacktestOptions>   bt,
        IOptions<IndicatorsOptions> ind,
        ILogger<PaperTradingEngine> log)
    {
        _strat = strat.Value; _risk = risk.Value;
        _bt    = bt.Value;    _ind  = ind.Value;
        _log   = log;
    }

    /// <summary>
    /// Runs one daily update cycle. Call this once per trading day (e.g. via a cron job or
    /// immediately at market open before placing real orders for validation).
    /// </summary>
    public void RunDailyUpdate(
        Dictionary<string, MarketData> universe,
        MarketData                     benchmark)
    {
        var state  = LoadState();
        var regime = new RegimeEngine(benchmark, _ind);
        var scorer = new AlphaScorer(_strat, _ind);
        var reg    = regime.GetRegime(benchmark.Length - 1);
        var today  = DateTime.UtcNow.Date;

        _log.LogInformation("[Paper] Daily update | Regime={R} | Positions={N}",
            reg, state.Positions.Count);

        // ── 1. Evaluate exits for all open positions ──────────────────────────
        var exits    = new List<ExitEvent>();
        var retained = new List<PaperPosition>(state.Positions.Count);

        foreach (var pos in state.Positions)
        {
            if (!universe.TryGetValue(pos.Symbol, out var md))
            { retained.Add(pos); continue; }

            int idx = md.Length - 1;
            if (idx < 0) { retained.Add(pos); continue; }

            double hi  = md.High[idx], lo = md.Low[idx];
            double ltp = md.Close[idx], atr = md.Atr[idx];
            bool   hasAtr = !double.IsNaN(atr) && atr > 0;

            // Update high-water mark and trailing stop
            double highest  = Math.Max(pos.HighestSinceEntry, hi);
            double newTrail = hasAtr
                ? highest - _strat.TrailingStopAtrMultiple * atr
                : pos.TrailingStop;
            double trail = Math.Max(pos.TrailingStop, newTrail);

            // Priority-ordered exit checks (same as backtester — TRADING LOGIC INVARIANT)
            bool   exitNow = false;
            double exitPx  = ltp;
            var    reason  = ExitReason.EndOfData;

            if (pos.TakeProfit > 0 && hi >= pos.TakeProfit)
                { exitNow = true; exitPx = Math.Max(ltp, pos.TakeProfit); reason = ExitReason.TakeProfit; }
            else if (pos.StopLoss > 0 && lo <= pos.StopLoss)
                { exitNow = true; exitPx = Math.Min(ltp, pos.StopLoss); reason = ExitReason.StopLoss; }
            else if (trail > 0 && lo <= trail)
                { exitNow = true; exitPx = Math.Min(ltp, trail); reason = ExitReason.TrailingStop; }
            else if (!double.IsNaN(md.HmaFast[idx]) && md.HmaFast[idx] < md.HmaSlow[idx])
                { exitNow = true; exitPx = ltp; reason = ExitReason.TrendReversal; }

            if (exitNow)
            {
                double net = (exitPx - pos.EntryPrice) * pos.Quantity
                             - pos.Quantity * _bt.CommissionPerShare * 2;
                state.CashBalance += pos.EntryPrice * pos.Quantity + net;
                var ev = new ExitEvent(pos, exitPx, reason, net);
                exits.Add(ev);
                state.AppendExit(
                    $"[{today:yyyy-MM-dd}] EXIT {pos.Symbol} @{exitPx:F2} " +
                    $"{reason} P&L={net:+#,##0.00;-#,##0.00}");
                _log.LogInformation("[Paper] Exit {Sym} {R} P&L={P:C}", pos.Symbol, reason, net);
            }
            else
            {
                retained.Add(pos with { TrailingStop = trail, HighestSinceEntry = highest });
            }
        }
        state.Positions = retained;

        // ── 2. Score new entry candidates ─────────────────────────────────────
        var candidates = new List<(string Sym, SignalEvaluation Eval, double Ltp, double Atr)>();
        foreach (var (sym, md) in universe)
        {
            if (string.Equals(sym, benchmark.Symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (state.Positions.Any(p => string.Equals(p.Symbol, sym, StringComparison.OrdinalIgnoreCase)))
                continue;
            int idx = md.Length - 1;
            if (idx < 0) continue;
            var eval = scorer.Evaluate(md, idx, reg);
            if (eval.IsEntry && !double.IsNaN(md.Atr[idx]) && md.Atr[idx] > 0)
                candidates.Add((sym, eval, md.Close[idx], md.Atr[idx]));
        }
        candidates.Sort((a, b) => b.Eval.AlphaScore.CompareTo(a.Eval.AlphaScore));

        // ── 3. Size and add new positions ─────────────────────────────────────
        var newEntries = new List<(string Sym, double Px, int Qty, SignalEvaluation Eval)>();
        double equity  = ComputeEquity(state, universe);
        int    slots   = _risk.MaxOpenPositions - state.Positions.Count;

        foreach (var (sym, eval, ltp, atr) in candidates)
        {
            if (slots <= 0) break;
            double rps = ltp - eval.EstStopLoss;
            if (rps <= double.Epsilon) continue;
            int qty = (int)(equity * _risk.AccountRiskPerTradePct / rps);
            if (qty * ltp > state.CashBalance) qty = (int)(state.CashBalance / ltp);
            if (qty <= 0) continue;

            state.Positions.Add(new PaperPosition(
                sym, qty, ltp, today, eval.EstStopLoss, eval.EstTakeProfit, 0, ltp, eval.AlphaScore));
            state.CashBalance -= qty * ltp;
            newEntries.Add((sym, ltp, qty, eval));
            slots--;
            _log.LogInformation("[Paper] Entry {Sym}×{Qty}@{Px:F2} Score={S:F1}",
                sym, qty, ltp, eval.AlphaScore);
        }

        state.LastUpdated = DateTime.UtcNow;
        SaveState(state);
        PrintDashboard(state, universe, exits, newEntries, reg, today);
    }

    private static double ComputeEquity(PaperState state, Dictionary<string, MarketData> universe)
    {
        double mtm = state.CashBalance;
        foreach (var pos in state.Positions)
            if (universe.TryGetValue(pos.Symbol, out var md) && md.Length > 0)
                mtm += pos.Quantity * md.Close[md.Length - 1];
        return mtm;
    }

    private static void PrintDashboard(
        PaperState                                                         state,
        Dictionary<string, MarketData>                                     universe,
        List<ExitEvent>                                                    exits,
        List<(string Sym, double Px, int Qty, SignalEvaluation Eval)>      newEntries,
        RegimeState                                                        reg,
        DateTime                                                           today)
    {
        double equity = ComputeEquity(state, universe);
        Console.WriteLine();
        Console.WriteLine(new string('═', 92));
        Console.WriteLine($"  PAPER TRADING  |  {today:yyyy-MM-dd}  |  Regime: {reg,-14}" +
            $"|  Equity: {equity:C}  |  Cash: {state.CashBalance:C}");
        Console.WriteLine(new string('═', 92));

        if (exits.Count > 0)
        {
            Console.WriteLine("  EXITS:");
            Console.WriteLine(new string('─', 92));
            foreach (var ev in exits)
                Console.WriteLine($"  {ev.Pos.Symbol,-10} EXIT @{ev.ExitPrice,9:F2}  " +
                    $"{ev.Reason,-16}  P&L: {ev.NetPnl,+12:N2}");
        }

        if (newEntries.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  NEW ENTRIES (execute at tomorrow's open):");
            Console.WriteLine(new string('─', 92));
            Console.WriteLine($"  {"Symbol",-10} {"Score",7} {"Est.Entry",10} {"Qty",6}" +
                $"  {"Stop",10}  {"Target",10}");
            Console.WriteLine(new string('─', 92));
            foreach (var (sym, px, qty, eval) in newEntries)
                Console.WriteLine($"  {sym,-10} {eval.AlphaScore,7:F1} {px,10:F2} {qty,6}" +
                    $"  {eval.EstStopLoss,10:F2}  {eval.EstTakeProfit,10:F2}");
        }

        if (state.Positions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  OPEN POSITIONS:");
            Console.WriteLine(new string('─', 92));
            Console.WriteLine($"  {"Symbol",-10} {"Entry",10} {"Current",10} {"Qty",6}" +
                $"  {"Unreal P&L",13}  {"Stop",10}");
            Console.WriteLine(new string('─', 92));
            foreach (var pos in state.Positions)
            {
                double cur    = universe.TryGetValue(pos.Symbol, out var md2) && md2.Length > 0
                    ? md2.Close[md2.Length - 1] : pos.EntryPrice;
                double unreal = (cur - pos.EntryPrice) * pos.Quantity;
                Console.WriteLine($"  {pos.Symbol,-10} {pos.EntryPrice,10:F2} {cur,10:F2}" +
                    $" {pos.Quantity,6}  {unreal,+11:N2}    {pos.StopLoss,10:F2}");
            }
        }

        if (exits.Count == 0 && newEntries.Count == 0 && state.Positions.Count == 0)
            Console.WriteLine("  No actionable signals in current market regime.");

        Console.WriteLine(new string('═', 92));
    }

    private PaperState LoadState()
    {
        if (!File.Exists(StateFile)) return new PaperState { CashBalance = _bt.InitialCapital };
        try
        {
            return JsonSerializer.Deserialize<PaperState>(File.ReadAllText(StateFile), JsonOpts)
                ?? new PaperState { CashBalance = _bt.InitialCapital };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Paper] Could not load state — starting fresh");
            return new PaperState { CashBalance = _bt.InitialCapital };
        }
    }

    /// <summary>Atomic write: temp file + File.Move prevents corruption on crash.</summary>
    private void SaveState(PaperState state)
    {
        try
        {
            File.WriteAllText(StateTempFile, JsonSerializer.Serialize(state, JsonOpts));
            File.Move(StateTempFile, StateFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Paper] Could not persist state");
            try { File.Delete(StateTempFile); } catch { /* best-effort cleanup */ }
        }
    }
}
