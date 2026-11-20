using System.Globalization;
using System.Text.Json;
using Trading.Shared.Models;

namespace Trading.Worker.Services;

public static class RawTickParser
{
    private static readonly string[] DateFormats = ["dd-MM-yyyy", "yyyy-MM-dd"];
    private static readonly string[] TimeFormats = ["HH:mm:ss.fff", "HH:mm:ss", "H:mm:ss.fff", "H:mm:ss", "HH:mm:ss.FFFFFFF"];

    public static bool TryParseTradeTick(JsonElement root, IAsyncWarningLogger? warningLogger, out MinimalTradingEntry? entry)
    {
        entry = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            warningLogger?.EnqueueWarning($"Expected JSON object tick payload, got {root.ValueKind}");
            return false;
        }

        JsonElement idElement = default;
        JsonElement lastElement = default;
        JsonElement dateElement = default;
        JsonElement timeElement = default;
        JsonElement tradingDateElement = default;
        JsonElement tradingTimeElement = default;

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.Equals("ID", StringComparison.OrdinalIgnoreCase))
            {
                idElement = prop.Value;
            }
            else if (prop.Name.Equals("Last", StringComparison.OrdinalIgnoreCase))
            {
                lastElement = prop.Value;
            }
            else if (prop.Name.Equals("Date", StringComparison.OrdinalIgnoreCase))
            {
                dateElement = prop.Value;
            }
            else if (prop.Name.Equals("Time", StringComparison.OrdinalIgnoreCase))
            {
                timeElement = prop.Value;
            }
            else if (prop.Name.Equals("Trading date", StringComparison.OrdinalIgnoreCase))
            {
                tradingDateElement = prop.Value;
            }
            else if (prop.Name.Equals("Trading time", StringComparison.OrdinalIgnoreCase))
            {
                tradingTimeElement = prop.Value;
            }
        }

        if (!TryParseLast(lastElement, warningLogger, out decimal lastPrice))
        {
            return false;
        }

        if (!TryParseId(idElement, warningLogger, out string id))
        {
            return false;
        }

        var date = TryParseDate(dateElement, "Date", warningLogger);
        var time = TryParseTime(timeElement, "Time", warningLogger);
        var tradingDate = TryParseDate(tradingDateElement, "Trading date", warningLogger);
        var tradingTime = TryParseTime(tradingTimeElement, "Trading time", warningLogger);

        entry = new MinimalTradingEntry
        {
            Id = id,
            Last = lastPrice,
            Date = date,
            Time = time,
            TradingDate = tradingDate,
            TradingTime = tradingTime
        };
        return true;
    }

    private static bool TryParseId(JsonElement element, IAsyncWarningLogger? warningLogger, out string id)
    {
        id = string.Empty;
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
        {
            warningLogger?.EnqueueWarning("Missing or null ID field in trade tick");
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            warningLogger?.EnqueueWarning($"Unexpected JSON type {element.ValueKind} for ID field");
            return false;
        }

        id = (element.GetString() ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(id))
        {
            warningLogger?.EnqueueWarning("Empty ID field in trade tick");
            return false;
        }

        return true;
    }

    private static bool TryParseLast(JsonElement element, IAsyncWarningLogger? warningLogger, out decimal value)
    {
        value = 0m;
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetDecimal(out value))
            {
                return true;
            }

            warningLogger?.EnqueueWarning("Numeric Last value outside decimal range");
            return false;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "::")
            {
                return false;
            }

            if (decimal.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            warningLogger?.EnqueueWarning($"Invalid Last decimal string: '{raw}'");
            return false;
        }

        warningLogger?.EnqueueWarning($"Unexpected JSON type {element.ValueKind} for Last field");
        return false;
    }

    private static DateOnly? TryParseDate(JsonElement element, string fieldName, IAsyncWarningLogger? warningLogger)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            warningLogger?.EnqueueWarning($"Unexpected JSON type {element.ValueKind} for {fieldName}");
            return null;
        }

        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed == "::")
        {
            return null;
        }

        if (DateOnly.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        warningLogger?.EnqueueWarning($"Unparseable {fieldName} value: '{raw}'");
        return null;
    }

    private static TimeOnly? TryParseTime(JsonElement element, string fieldName, IAsyncWarningLogger? warningLogger)
    {
        if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            warningLogger?.EnqueueWarning($"Unexpected JSON type {element.ValueKind} for {fieldName}");
            return null;
        }

        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed == "::")
        {
            return null;
        }

        if (TimeOnly.TryParseExact(trimmed, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        warningLogger?.EnqueueWarning($"Unparseable {fieldName} value: '{raw}'");
        return null;
    }
}
