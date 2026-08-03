namespace QuantEngine.Domain.Options;
public sealed class UpstoxOptions
{
    public const string Section = "Brokers:Upstox";
    public string ApiKey      { get; set; } = string.Empty;
    public string ApiSecret   { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = "http://localhost:8080/upstox";
    public string AccessToken { get; set; } = string.Empty;
    public string Exchange    { get; set; } = "NSE_EQ";
    public string Product     { get; set; } = "D";
}
