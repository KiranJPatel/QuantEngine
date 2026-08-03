namespace QuantEngine.Domain.Entities;
public sealed record PaperPosition(
    string   Symbol,
    int      Quantity,
    double   EntryPrice,
    DateTime EntryDate,
    double   StopLoss,
    double   TakeProfit,
    double   TrailingStop,
    double   HighestSinceEntry,
    double   AlphaScore);
