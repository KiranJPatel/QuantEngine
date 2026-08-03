namespace QuantEngine.Domain.Options;
public sealed class ZerodhaOptions
{
    public const string Section = "Brokers:Zerodha";
    public string ApiKey      { get; set; } = string.Empty;
    public string ApiSecret   { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string UserId      { get; set; } = string.Empty;
    public string Exchange    { get; set; } = "NSE";
    public string Product     { get; set; } = "CNC";
}
