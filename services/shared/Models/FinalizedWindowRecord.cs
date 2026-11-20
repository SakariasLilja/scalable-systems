namespace Trading.Shared.Models;

public record FinalizedWindowRecord(
    string SymbolId,
    DateTime WindowStart,
    DateTime WindowEnd,
    double Ema38,
    double Ema100,
    double LastPrice,
    bool BuySignal,
    bool SellSignal
);
