namespace QuantEngine.Domain.Entities;
public sealed record BrokerPosition(
    string Symbol,
    int    Quantity,
    double AveragePrice,
    double LastPrice,
    double UnrealisedPnl,
    double RealisedPnl);
