namespace InitialAttempt.Models;

// Keeping SecType as an enum since the dataset defines it strictly as (E)quity or (I)ndex
public enum SecurityType : byte { E, I }

public readonly record struct MinimalTradingEntry(
    string Id,
    SecurityType SecType,
    decimal? Last,
    TimeOnly? TradingTime,
    DateOnly? TradingDate
);
