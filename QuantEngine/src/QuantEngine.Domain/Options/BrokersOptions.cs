using QuantEngine.Domain.Enums;
namespace QuantEngine.Domain.Options;
public sealed class BrokersOptions
{
    public const string Section = "Brokers";
    public BrokerType   ActiveBroker { get; set; } = BrokerType.Paper;
    public ZerodhaOptions Zerodha    { get; set; } = new();
    public UpstoxOptions  Upstox     { get; set; } = new();
}
