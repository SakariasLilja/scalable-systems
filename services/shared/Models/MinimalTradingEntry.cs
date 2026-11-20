namespace Trading.Shared.Models;

public class MinimalTradingEntry
{
    public string Id { get; set; } = string.Empty;
    public decimal? Last { get; set; }
    public TimeOnly? TradingTime { get; set; }
    public DateOnly? TradingDate { get; set; }
    public DateOnly? Date { get; set; }
    public TimeOnly? Time { get; set; }

    public DateOnly? EffectiveDate => TradingDate ?? Date;
    public TimeOnly? EffectiveTime => TradingTime ?? Time;
}
