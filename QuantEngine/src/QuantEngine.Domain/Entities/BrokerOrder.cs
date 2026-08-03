using QuantEngine.Domain.Enums;
namespace QuantEngine.Domain.Entities;

public sealed class BrokerOrder
{
    public string     OrderId      { get; set; } = string.Empty;
    public string     Symbol       { get; set; } = string.Empty;
    public OrderSide  Side         { get; set; }
    public OrderType  Type         { get; set; }
    public OrderState State        { get; set; }
    public int        Quantity     { get; set; }
    public int        FilledQty    { get; set; }
    public double     Price        { get; set; }
    public double     TriggerPrice { get; set; }
    public double     AvgFillPrice { get; set; }
    public string     Reason       { get; set; } = string.Empty;
    public DateTime   PlacedAt     { get; set; } = DateTime.UtcNow;
    public DateTime   UpdatedAt    { get; set; } = DateTime.UtcNow;
}
