using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Domain.Options;
using QuantEngine.Domain.Utilities;
using QuantEngine.Domain.ValueObjects;

namespace QuantEngine.Risk;

/// <summary>
/// Pre-trade and intraday risk controls for live trading.
/// Tracks daily realised P&amp;L and halts trading when the daily loss limit is breached.
/// </summary>
public sealed class LiveRiskManager
{
    private readonly LiveTradingOptions _opts;
    private readonly ILogger<LiveRiskManager> _log;
    private          double              _dailyPnl;
    private          bool                _halted;
    private readonly SemaphoreSlim       _pnlLock = new(1, 1);

    public bool   IsHalted => _halted;
    public double DailyPnl => _dailyPnl;

    public LiveRiskManager(IOptions<LiveTradingOptions> opts, ILogger<LiveRiskManager> log)
    {
        _opts = opts.Value;
        _log  = log;
    }

    public void ResetDailyCounters()
    {
        _dailyPnl = 0;
        _halted   = false;
        _log.LogInformation("[Risk] Daily counters reset");
    }

    public async Task RecordRealisedPnlAsync(double pnl, string symbol)
    {
        await _pnlLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _dailyPnl += pnl;
            if (_dailyPnl <= -_opts.MaxDailyLossINR && !_halted)
            {
                _halted = true;
                _log.LogCritical("[Risk] DAILY LOSS LIMIT: {Loss:C} — halted",
                    Math.Abs(_dailyPnl));
            }
        }
        finally { _pnlLock.Release(); }
    }

    /// <summary>Returns null if the order passes all pre-trade checks; otherwise a rejection reason.</summary>
    public string? CheckOrderRisk(OrderRequest req)
    {
        if (_halted)                      return "Daily loss limit hit";
        if (req.Quantity <= 0)            return "Quantity must be > 0";
        if (!MarketSchedule.IsWeekday())  return "Market closed (weekend)";
        if (!MarketSchedule.IsMarketOpen()) return "Market not open";
        double est = req.Quantity * (req.Price > 0 ? req.Price : 1000.0);
        if (est > _opts.MaxOrderValueINR)
            return $"Order value {est:C} exceeds max {_opts.MaxOrderValueINR:C}";
        return null;
    }
}
