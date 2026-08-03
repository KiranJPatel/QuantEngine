using System.ComponentModel.DataAnnotations;
namespace QuantEngine.Domain.Options;
public sealed class StrategyOptions
{
    public const string Section = "Strategy";
    [Range(0.1, 50)] public double TakeProfitAtrMultiple   { get; set; } = 3.0;
    [Range(0.1, 50)] public double StopLossAtrMultiple     { get; set; } = 1.5;
    [Range(0.1, 50)] public double TrailingStopAtrMultiple { get; set; } = 2.0;
    [Range(0, 100)]  public double MinimumAlphaScore       { get; set; } = 40.0;
}
