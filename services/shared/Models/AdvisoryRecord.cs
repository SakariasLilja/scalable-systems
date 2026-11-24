namespace Trading.Shared.Models;

public record AdvisoryRecord(
    string Symbol,
    string Type,
    double ClosePrice,
    double Ema38,
    double Ema100,
    DateTime Time
);
