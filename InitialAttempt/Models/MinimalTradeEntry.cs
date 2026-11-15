using CsvHelper;

namespace InitialAttempt.Models;

public enum SecurityType : byte { E, I }

[CsvRecord]
public partial class MinimalTradingEntry
{
    [CsvColumn("ID")]
    public string Id { get; set; } = string.Empty;

    [CsvColumn("SecType")]
    public SecurityType SecType { get; set; }

    [CsvColumn("Last")]
    public decimal? Last { get; set; }

    [CsvColumn("Trading time")]
    [CsvFormat("HH:mm:ss.fff")]
    public TimeOnly? TradingTime { get; set; }

    [CsvColumn("Trading date")]
    [CsvFormat("dd-MM-yyyy")]
    public DateOnly? TradingDate { get; set; }
}
