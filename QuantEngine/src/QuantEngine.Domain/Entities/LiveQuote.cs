namespace QuantEngine.Domain.Entities;
public sealed record LiveQuote(
    string   Symbol,
    double   LastPrice,
    double   Open,
    double   High,
    double   Low,
    double   Close,
    long     Volume,
    DateTime Timestamp);
