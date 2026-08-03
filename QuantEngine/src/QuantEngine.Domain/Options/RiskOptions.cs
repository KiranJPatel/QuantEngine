using System.ComponentModel.DataAnnotations;
namespace QuantEngine.Domain.Options;
public sealed class RiskOptions
{
    public const string Section = "Risk";
    [Range(0.001, 0.10)] public double AccountRiskPerTradePct { get; set; } = 0.01;
    [Range(1, 100)]      public int    MaxOpenPositions       { get; set; } = 10;
    [Range(0.01, 1.0)]   public double MaxPortfolioHeat       { get; set; } = 0.80;
    [Range(0.01, 1.0)]   public double RegimeHeatPenalty      { get; set; } = 0.50;
}
