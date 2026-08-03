using System.ComponentModel.DataAnnotations;
namespace QuantEngine.Domain.Options;
public sealed class LiveTradingOptions
{
    public const string Section = "LiveTrading";
    [Range(1, double.MaxValue)]  public double MaxDailyLossINR             { get; set; } = 50_000;
    [Range(1, double.MaxValue)]  public double MaxOrderValueINR            { get; set; } = 500_000;
    [Range(1, 500)]              public int    MinBarsWarmup               { get; set; } = 60;
    [Range(0, 60)]               public int    SquareOffMinutesBeforeClose { get; set; } = 15;
    public bool   EnableAutoSquareOff        { get; set; } = true;
    [Range(1, 300)]              public int    PricePollingIntervalSeconds { get; set; } = 5;
    [Range(5, 600)]              public int    OrderTimeoutSeconds         { get; set; } = 60;
    public bool   UseWebSocketFeed            { get; set; } = true;
    [Range(10, 2000)]            public int    HistoricalBarsForSignals    { get; set; } = 300;
    [Required]                   public string YahooNseSuffix              { get; set; } = ".NS";
}
