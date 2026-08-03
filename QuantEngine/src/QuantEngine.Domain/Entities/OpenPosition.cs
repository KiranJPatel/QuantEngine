namespace QuantEngine.Domain.Entities;

/// <summary>Mutable state for a position open during backtesting or live trading.</summary>
public sealed class OpenPosition
{
    public string   Symbol            { get; set; } = string.Empty;
    public int      Quantity          { get; set; }
    public double   EntryPrice        { get; set; }
    public DateTime EntryDate         { get; set; }
    public double   StopLoss          { get; set; }
    public double   TakeProfit        { get; set; }
    public double   TrailingStop      { get; set; }
    public double   HighestSinceEntry { get; set; }
}
