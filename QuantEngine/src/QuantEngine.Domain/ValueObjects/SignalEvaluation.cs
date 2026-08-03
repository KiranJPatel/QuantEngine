namespace QuantEngine.Domain.ValueObjects;
/// <summary>Result of the alpha scorer for a single symbol at a given bar.</summary>
public readonly record struct SignalEvaluation(
    bool   IsEntry,
    double AlphaScore,
    double EstStopLoss,
    double EstTakeProfit);
