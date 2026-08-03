using QuantEngine.Domain.Enums;
namespace QuantEngine.Domain.ValueObjects;
public sealed record OrderRequest(
    string    Symbol,
    OrderSide Side,
    OrderType Type,
    int       Quantity,
    double    Price,
    double    TriggerPrice,
    string    Tag = "QUANT");
