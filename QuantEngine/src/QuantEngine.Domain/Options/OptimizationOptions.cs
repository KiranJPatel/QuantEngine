using System.ComponentModel.DataAnnotations;
namespace QuantEngine.Domain.Options;
public sealed class OptimizationOptions
{
    public const string Section = "Optimization";
    [MinLength(1)] public int[]    HmaFastRange              { get; set; } = [10, 15, 20];
    [MinLength(1)] public int[]    HmaSlowRange              { get; set; } = [40, 50, 60];
    [MinLength(1)] public double[] AdxThresholdRange         { get; set; } = [18.0, 20.0, 25.0];
    [MinLength(1)] public double[] SupertrendMultiplierRange  { get; set; } = [2.5, 3.0, 3.5];
    public int    Parallelism        { get; set; } = -1;  // -1 = ProcessorCount
    [Range(1, 100)] public int TopN { get; set; } = 10;
    public bool   EnableWalkForward   { get; set; } = false;
    [Range(0.1, 0.9)] public double InSampleFraction { get; set; } = 0.70;
}
