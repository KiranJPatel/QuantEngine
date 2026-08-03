using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Backtesting.Analytics;
using QuantEngine.Backtesting.Models;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Options;
using QuantEngine.Domain.ValueObjects;
using QuantEngine.Indicators.Models;

namespace QuantEngine.Reporting;

/// <summary>Exports equity-curve and trade-log CSV files, and renders console reports.</summary>
public sealed class ReportExporter
{
    private readonly BacktestOptions _opts;
    private readonly ILogger<ReportExporter> _log;

    public ReportExporter(IOptions<BacktestOptions> opts, ILogger<ReportExporter> log)
    {
        _opts = opts.Value; _log = log;
    }

    public string ExportEquityCurve(DateTime[] dates, double[] curve, string runId)
    {
        Directory.CreateDirectory(_opts.ReportsFolder);
        string path = Path.Combine(_opts.ReportsFolder, $"equity_{runId[..8]}.csv");
        using var sw = new StreamWriter(path, false, Encoding.UTF8);
        sw.WriteLine("Date,Equity,DrawdownPct");
        double peak = curve.Length > 0 ? curve[0] : 0;
        int    len  = Math.Min(dates.Length, curve.Length);
        for (int i = 0; i < len; i++)
        {
            if (curve[i] > peak) peak = curve[i];
            double dd = peak > 0 ? (peak - curve[i]) / peak * 100.0 : 0;
            sw.WriteLine($"{dates[i]:yyyy-MM-dd},{curve[i]:F2},{dd:F3}");
        }
        _log.LogInformation("[Reports] Equity curve → {Path}", path);
        return path;
    }

    public string ExportTrades(IReadOnlyList<Trade> trades, string runId)
    {
        Directory.CreateDirectory(_opts.ReportsFolder);
        string path = Path.Combine(_opts.ReportsFolder, $"trades_{runId[..8]}.csv");
        using var sw = new StreamWriter(path, false, Encoding.UTF8);
        sw.WriteLine("Symbol,EntryDate,ExitDate,EntryPrice,ExitPrice,Quantity,NetPnl,ExitReason");
        foreach (var t in trades)
            sw.WriteLine($"{t.Symbol},{t.EntryDate:yyyy-MM-dd},{t.ExitDate:yyyy-MM-dd}," +
                $"{t.EntryPrice:F4},{t.ExitPrice:F4},{t.Quantity},{t.NetPnl:F2},{t.Reason}");
        _log.LogInformation("[Reports] Trades → {Path}", path);
        return path;
    }

    public static void PrintSummary(PerformanceMetrics m, string runId, long elapsedMs)
    {
        Console.WriteLine();
        Console.WriteLine(new string('═', 68));
        Console.WriteLine($"  BACKTEST  |  Run: {runId[..8]}");
        Console.WriteLine(new string('═', 68));
        Console.WriteLine($"  Final Equity        : {m.FinalEquity:C}");
        Console.WriteLine($"  CAGR                : {m.CAGR:P2}");
        Console.WriteLine($"  Sharpe Ratio        : {m.SharpeRatio:F3}");
        Console.WriteLine($"  Sortino Ratio       : {m.SortinoRatio:F3}");
        Console.WriteLine($"  Calmar Ratio        : {m.CalmarRatio:F3}");
        Console.WriteLine($"  Max Drawdown        : {m.MaxDrawdownPct:F2}%");
        Console.WriteLine(new string('─', 68));
        Console.WriteLine($"  Total Trades        : {m.TotalTrades}");
        Console.WriteLine($"  Win Rate            : {m.WinRate:P1}  ({m.WinningTrades}W/{m.TotalTrades - m.WinningTrades}L)");
        Console.WriteLine($"  Profit Factor       : {m.ProfitFactor:F2}");
        Console.WriteLine($"  Avg Win             : {m.AvgWin:C}");
        Console.WriteLine($"  Avg Loss            : {m.AvgLoss:C}");
        Console.WriteLine($"  Max Consec. Losses  : {m.MaxConsecutiveLosses}");
        Console.WriteLine(new string('─', 68));
        Console.WriteLine($"  Execution Time      : {elapsedMs:N0} ms");
        Console.WriteLine(new string('═', 68));
    }

    public static void PrintBenchmarkComparison(
        PerformanceMetrics strat, BenchmarkResult bench,
        string benchSym, double initialCapital)
    {
        double stratRet = strat.FinalEquity / initialCapital - 1.0;
        Console.WriteLine();
        Console.WriteLine(new string('─', 62));
        Console.WriteLine($"  STRATEGY  vs  {benchSym} Buy-and-Hold");
        Console.WriteLine(new string('─', 62));
        Console.WriteLine($"  {"Metric",-20} {"Strategy",14} {benchSym,14}");
        Console.WriteLine(new string('─', 62));
        Row("CAGR",         $"{strat.CAGR:P2}",          $"{bench.CAGR:P2}");
        Row("Sharpe",       $"{strat.SharpeRatio:F3}",   $"{bench.SharpeRatio:F3}");
        Row("Max Drawdown", $"{strat.MaxDrawdownPct:F2}%",$"{bench.MaxDrawdownPct:F2}%");
        Row("Total Return", $"{stratRet:P2}",             $"{bench.TotalReturn:P2}");
        Console.WriteLine(new string('─', 62));
        static void Row(string l, string s, string b)
            => Console.WriteLine($"  {l,-20} {s,14} {b,14}");
    }

    public static void PrintWalkForward(WalkForwardResult wf)
    {
        var (i, o) = (wf.InSampleMetrics, wf.OutOfSampleMetrics);
        Console.WriteLine();
        Console.WriteLine(new string('═', 66));
        Console.WriteLine("  WALK-FORWARD VALIDATION");
        Console.WriteLine(new string('═', 66));
        Console.WriteLine($"  Params: HmaFast={wf.BestIndicators.HmaFast} " +
            $"HmaSlow={wf.BestIndicators.HmaSlow} " +
            $"ADX={wf.BestIndicators.AdxThreshold} " +
            $"STmult={wf.BestIndicators.SupertrendMultiplier}");
        Console.WriteLine(new string('─', 66));
        Console.WriteLine($"  {"Metric",-20} {"In-Sample",14} {"Out-of-Sample",14}");
        Console.WriteLine(new string('─', 66));
        WRow("CAGR",        $"{i.CAGR:P2}",         $"{o.CAGR:P2}");
        WRow("Sharpe",      $"{i.SharpeRatio:F3}",  $"{o.SharpeRatio:F3}");
        WRow("Calmar",      $"{i.CalmarRatio:F2}",  $"{o.CalmarRatio:F2}");
        WRow("Max Drawdown",$"{i.MaxDrawdownPct:F2}%",$"{o.MaxDrawdownPct:F2}%");
        WRow("Win Rate",    $"{i.WinRate:P1}",       $"{o.WinRate:P1}");
        WRow("Trades",      $"{i.TotalTrades}",      $"{o.TotalTrades}");
        Console.WriteLine(new string('═', 66));
        double ratio = i.SharpeRatio > 0 ? o.SharpeRatio / i.SharpeRatio : 0;
        Console.WriteLine($"  OOS/IS Sharpe: {ratio:F2}  " +
            (ratio >= 0.50 ? "(acceptable)" : "(WARNING: possible overfit)"));
        Console.WriteLine(new string('═', 66));
        static void WRow(string l, string is_, string oos)
            => Console.WriteLine($"  {l,-20} {is_,14} {oos,14}");
    }

    public static void PrintOptimizationGrid(List<OptimizationResult> results)
    {
        Console.WriteLine();
        Console.WriteLine(new string('═', 104));
        Console.WriteLine($"  GRID RESULTS  |  Top {results.Count}");
        Console.WriteLine(new string('═', 104));
        Console.WriteLine($"  {"#",-3} | {"HmaF",4} | {"HmaS",4} | {"ADX",5} | {"STx",5} | " +
            $"{"Sharpe",7} | {"Sortino",7} | {"Calmar",7} | {"MaxDD%",7} | {"CAGR%",7} | {"WinR%",6} | {"Trades",6}");
        Console.WriteLine(new string('─', 104));
        for (int r = 0; r < results.Count; r++)
        {
            var (ind, _, m) = (results[r].Indicators, results[r].Strategy, results[r].Metrics);
            Console.WriteLine(
                $"  {r+1,-3} | {ind.HmaFast,4} | {ind.HmaSlow,4} | " +
                $"{ind.AdxThreshold,5:F0} | {ind.SupertrendMultiplier,5:F1} | " +
                $"{m.SharpeRatio,7:F3} | {m.SortinoRatio,7:F3} | " +
                $"{m.CalmarRatio,7:F2} | {m.MaxDrawdownPct,7:F2} | " +
                $"{m.CAGR*100,7:F2} | {m.WinRate*100,6:F1} | {m.TotalTrades,6}");
        }
        Console.WriteLine(new string('═', 104));
    }
}
