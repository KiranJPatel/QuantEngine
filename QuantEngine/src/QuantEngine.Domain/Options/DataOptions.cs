using System.ComponentModel.DataAnnotations;
namespace QuantEngine.Domain.Options;
public sealed class DataOptions
{
    public const string Section = "Data";
    [Required] public string   Provider         { get; set; } = "Yahoo";
    [Required] public string   CsvFolder        { get; set; } = "./Data";
    [Required] public string   BenchmarkSymbol  { get; set; } = "SPY";
    [Required] public string   UniverseFilePath { get; set; } = "universe.json";
    public DateTime Start            { get; set; } = new(2020, 1, 1);
    public DateTime End              { get; set; } = new(2024, 12, 31);
    [Required] public string   CacheFolder      { get; set; } = ".quant_cache";
    [Range(0, 365)] public int CacheMaxAgeDays  { get; set; } = 1;
}
