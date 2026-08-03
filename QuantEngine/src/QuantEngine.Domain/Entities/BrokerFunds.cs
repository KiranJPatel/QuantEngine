namespace QuantEngine.Domain.Entities;
public sealed record BrokerFunds(
    double AvailableBalance,
    double UsedMargin,
    double TotalBalance);
