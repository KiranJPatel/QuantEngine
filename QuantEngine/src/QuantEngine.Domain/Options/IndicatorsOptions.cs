using System.ComponentModel.DataAnnotations;
namespace QuantEngine.Domain.Options;
public sealed class IndicatorsOptions
{
    public const string Section = "Indicators";
    [Range(2, 500)]  public int    HmaFast              { get; set; } = 20;
    [Range(2, 500)]  public int    HmaSlow              { get; set; } = 50;
    [Range(1, 200)]  public int    SupertrendAtrPeriod  { get; set; } = 10;
    [Range(0.1, 20)] public double SupertrendMultiplier { get; set; } = 3.0;
    [Range(1, 200)]  public int    AdxPeriod            { get; set; } = 14;
    [Range(1, 100)]  public double AdxThreshold         { get; set; } = 20.0;
}
