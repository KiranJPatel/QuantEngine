using System.ComponentModel.DataAnnotations;
namespace QuantEngine.Domain.Options;
public sealed class BacktestOptions
{
    public const string Section = "Backtest";
    [Range(1000, double.MaxValue)] public double InitialCapital     { get; set; } = 1_000_000;
    [Range(0, 100)]                public double CommissionPerShare { get; set; } = 0.005;
    [Range(0, 1.0)]                public double SlippageAtrFrac    { get; set; } = 0.05;
    [Required]                     public string ReportsFolder      { get; set; } = "reports";
}
