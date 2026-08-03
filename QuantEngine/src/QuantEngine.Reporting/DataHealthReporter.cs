using QuantEngine.Indicators.Models;

namespace QuantEngine.Reporting;

public record DataHealthEntry(
    string   Symbol,
    int      Bars,
    DateTime FirstDate,
    DateTime LastDate,
    bool     HasData);

public static class DataHealthReporter
{
    public static IReadOnlyList<DataHealthEntry> Build(
        IEnumerable<string> symbols, Dictionary<string, MarketData> universe) =>
        symbols.Select(sym =>
        {
            if (!universe.TryGetValue(sym, out var md) || !md.IsValid)
                return new DataHealthEntry(sym, 0, DateTime.MinValue, DateTime.MinValue, false);
            return new DataHealthEntry(sym, md.Length, md.Dates[0], md.Dates[^1], true);
        }).ToList();

    public static void Print(IReadOnlyList<DataHealthEntry> entries)
    {
        int ok = entries.Count(e => e.HasData), fail = entries.Count - ok;
        Console.WriteLine();
        Console.WriteLine($"  DATA HEALTH  {ok} OK / {fail} failed");
        Console.WriteLine(new string('─', 72));
        foreach (var e in entries)
        {
            if (!e.HasData)
                Console.WriteLine($"  ✗  {e.Symbol,-10}  *** NO DATA ***");
            else
                Console.WriteLine($"  ✓  {e.Symbol,-10}  {e.Bars,5} bars  " +
                    $"{e.FirstDate:yyyy-MM-dd} → {e.LastDate:yyyy-MM-dd}");
        }
        Console.WriteLine(new string('─', 72));
    }
}
