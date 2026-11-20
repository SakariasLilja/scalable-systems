using Microsoft.Extensions.Logging;

namespace Trading.Server.Services;

public interface IWorkerResetClient
{
    Task<bool> ResetWorkerAsync(CancellationToken ct = default);
}

public class WorkerResetClient(
    IHttpClientFactory httpClientFactory,
    ILogger<WorkerResetClient> logger
) : IWorkerResetClient
{
    public async Task<bool> ResetWorkerAsync(CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient("worker");
            using var response = await client.PostAsync("/internal/reset", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Worker reset endpoint returned status {StatusCode}", response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to call Worker internal reset endpoint");
            return false;
        }
    }
}
