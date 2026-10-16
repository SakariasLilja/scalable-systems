namespace InitialAttempt.Services;

public interface IReadFileService
{
    public StreamReader OpenStreamReader(string fileName);
    public Task SkipComments(StreamReader reader);
    public Task AppendToTradingEventQueue(StreamReader reader, Queue<string> tradingEventQueue);
}

public class ReadFileService(
    ILogger<ReadFileService> logger,
    IConfiguration configuration) : IReadFileService
{
    private string CSV_PREFIX => configuration.GetValue<string>("CSV_PREFIX")
        ?? LogEnvErrorWithFallback("CSV_PREFIX", "debs2022-gc-trading-day-");
    
    private string CSV_SUFFIX => configuration.GetValue<string>("CSV_SUFFIX")
        ?? LogEnvErrorWithFallback("CSV_SUFFIX", ".csv");

    private string CSV_DIRECTORY => configuration.GetValue<string>("CSV_DIRECTORY")
        ?? LogEnvErrorWithFallback("CSV_DIRECTORY", ".");

    public StreamReader OpenStreamReader(string fileName)
    {
        var filestream = new FileStream(GetFilePath(fileName), FileMode.Open, FileAccess.Read);
        return new StreamReader(filestream);
    }

    public async Task SkipComments(StreamReader reader)
    {
        while (reader.Peek() == '#')
        {
            await reader.ReadLineAsync();
        }
    }

    public async Task AppendToTradingEventQueue(StreamReader reader, Queue<string> tradingEventQueue)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            tradingEventQueue.Enqueue(line);
        }
    }

    private string GetFilePath(string fileName)
    {
        return Path.Combine(CSV_DIRECTORY, $"{CSV_PREFIX}{fileName}{CSV_SUFFIX}");
    }

    private string LogEnvErrorWithFallback(string environmentVariable, string fallback)
    {
        logger.LogWarning("An error occurred while reading the environment variable '{EnvironmentVariable}'. Using fallback value: {Fallback}", environmentVariable, fallback);
        return fallback;
    }
}
