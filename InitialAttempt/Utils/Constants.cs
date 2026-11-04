namespace InitialAttempt.Utils;

public interface IConstants
{
    public string CSV_PREFIX { get; }
    public string CSV_SUFFIX { get; }
    public string CSV_DIRECTORY { get; }
}

public class Constants(
    ILogger logger,
    IConfiguration configuration) : IConstants
{
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