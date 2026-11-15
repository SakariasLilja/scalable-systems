using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InitialAttempt.Utils;

public interface IConstants
{
    public string CSV_PREFIX { get; }
    public string CSV_SUFFIX { get; }
    public string CSV_DIRECTORY { get; }
}

public class Constants(
    ILogger<Constants> logger,
    IConfiguration configuration) : IConstants
{
    public const int WindowSizeMs = 300000;
    public const int WindowDurationMinutes = 5;
    public const int WindowStartHour = 0;

    public const int EmaShortPeriod = 38;
    public const int EmaLongPeriod = 100;
    public const double EmaShortAlpha = 2.0 / (1.0 + 38.0);
    public const double EmaLongAlpha = 2.0 / (1.0 + 100.0);

    public const double DefaultSimulationSpeed = 1.0;
    public const int TrackedPushIntervalMs = 10000;
    public const int ActivePushIntervalMs = 2000;

    public const int DbBatchFlushIntervalMs = 5000;
    public const int DbBatchMaxCapacity = 1000;
    public const int DbBatchMaxBufferSize = 5000;
    public const int SimulationChannelCapacity = 100000;

    public string CSV_PREFIX => configuration.GetValue<string>("CSV_PREFIX")
        ?? LogEnvErrorWithFallback("CSV_PREFIX", "debs2022-gc-trading-day-");
    
    public string CSV_SUFFIX => configuration.GetValue<string>("CSV_SUFFIX")
        ?? LogEnvErrorWithFallback("CSV_SUFFIX", ".csv");

    public string CSV_DIRECTORY => configuration.GetValue<string>("CSV_DIRECTORY")
        ?? LogEnvErrorWithFallback("CSV_DIRECTORY", ".");

    private string LogEnvErrorWithFallback(string environmentVariable, string fallback)
    {
        logger.LogWarning("An error occurred while reading the environment variable '{EnvironmentVariable}'. Using fallback value: {Fallback}", environmentVariable, fallback);
        return fallback;
    }
}
