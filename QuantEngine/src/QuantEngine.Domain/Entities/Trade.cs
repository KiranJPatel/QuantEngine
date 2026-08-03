using QuantEngine.Domain.Enums;
namespace QuantEngine.Domain.Entities;

/// <summary>Immutable record of a completed round-trip trade.</summary>
public readonly record struct Trade(
    string     Symbol,
    DateTime   EntryDate,
    DateTime   ExitDate,
    double     EntryPrice,
    double     ExitPrice,
    int        Quantity,
    double     NetPnl,
    ExitReason Reason);
