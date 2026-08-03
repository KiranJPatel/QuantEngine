using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Utilities;

namespace QuantEngine.Trading;

/// <summary>Thread-safe in-memory live position cache; updated on every price tick.</summary>
public sealed class LivePositionManager
{
    private readonly ConcurrentDictionary<string, BrokerPosition> _positions = new();
    private readonly ConcurrentDictionary<string, string>         _stopOrders = new();
    private readonly ILogger<LivePositionManager> _log;

    public LivePositionManager(ILogger<LivePositionManager> log) => _log = log;

    public void Sync(IReadOnlyList<BrokerPosition> brokerPositions)
    {
        _positions.Clear();
        foreach (var p in brokerPositions) _positions[p.Symbol] = p;
        _log.LogDebug("[Positions] Synced {N} positions", brokerPositions.Count);
    }

    public void UpdatePrice(string symbol, double ltp)
    {
        if (_positions.TryGetValue(symbol, out var p))
            _positions[symbol] = p with
            {
                LastPrice     = ltp,
                UnrealisedPnl = (ltp - p.AveragePrice) * p.Quantity
            };
    }

    public void RegisterStopOrder(string sym, string orderId) => _stopOrders[sym] = orderId;

    public bool TryGetStopOrderId(string sym, out string orderId) =>
        _stopOrders.TryGetValue(sym, out orderId!);

    public void RemovePosition(string sym)
    {
        _positions.TryRemove(sym, out _);
        _stopOrders.TryRemove(sym, out _);
    }

    public IReadOnlyList<BrokerPosition> All =>
        _positions.Values.Where(p => p.Quantity != 0).ToList();

    public double TotalUnrealisedPnl => _positions.Values.Sum(p => p.UnrealisedPnl);

    public void PrintDashboard()
    {
        var now = MarketSchedule.NowIst();
        var all = All;
        Console.Clear();
        Console.WriteLine(new string('═', 90));
        Console.WriteLine($"  LIVE POSITIONS  |  {now:HH:mm:ss} IST  |  " +
            $"Unrealised: {TotalUnrealisedPnl:+C;-C}");
        Console.WriteLine(new string('═', 90));
        if (all.Count == 0) { Console.WriteLine("  No open positions."); }
        else
        {
            Console.WriteLine($"  {"Symbol",-12} {"Qty",6} {"Avg",10} {"LTP",10} {"P&L",12}");
            Console.WriteLine(new string('─', 56));
            foreach (var p in all)
                Console.WriteLine($"  {p.Symbol,-12} {p.Quantity,6} " +
                    $"{p.AveragePrice,10:F2} {p.LastPrice,10:F2} {p.UnrealisedPnl,+12:N2}");
        }
        Console.WriteLine(new string('═', 90));
    }
}
