namespace QuantEngine.Domain.Utilities;

/// <summary>Indian Standard Time market schedule for NSE/BSE (no external dependencies).</summary>
public static class MarketSchedule
{
    private static readonly TimeZoneInfo Ist =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");

    public static readonly TimeOnly MarketOpen      = new(9, 15, 0);
    public static readonly TimeOnly MarketClose     = new(15, 30, 0);
    public static readonly TimeOnly SquareOffWindow = new(15, 15, 0);

    public static DateTime NowIst()
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);

    public static bool IsMarketOpen(DateTime? nowIst = null)
    {
        var now = (nowIst ?? NowIst()).TimeOfDay;
        return now >= MarketOpen.ToTimeSpan() && now < MarketClose.ToTimeSpan();
    }

    public static bool IsWithinSquareOffWindow(DateTime? nowIst = null)
    {
        var now = (nowIst ?? NowIst()).TimeOfDay;
        return now >= SquareOffWindow.ToTimeSpan() && now < MarketClose.ToTimeSpan();
    }

    public static bool IsWeekday(DateTime? nowIst = null)
    {
        var dow = (nowIst ?? NowIst()).DayOfWeek;
        return dow >= DayOfWeek.Monday && dow <= DayOfWeek.Friday;
    }

    public static TimeSpan TimeUntilOpen(DateTime? nowIst = null)
    {
        var now  = nowIst ?? NowIst();
        var open = now.Date + MarketOpen.ToTimeSpan();
        if (open <= now) open = open.AddDays(1);
        return open - now;
    }

    public static TimeSpan TimeUntilClose(DateTime? nowIst = null)
    {
        var now   = nowIst ?? NowIst();
        var close = now.Date + MarketClose.ToTimeSpan();
        return close <= now ? TimeSpan.Zero : close - now;
    }
}
