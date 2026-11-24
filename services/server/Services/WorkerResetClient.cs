using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Trading.Server.Services;

public interface IWorkerResetClient
{
    Task<bool> ResetWorkerAsync(CancellationToken ct = default);
}

public class WorkerResetClient(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<WorkerResetClient> logger
) : IWorkerResetClient
{
    public async Task<bool> ResetWorkerAsync(CancellationToken ct = default)
    {
        var rawUrls = configuration["WORKER_RESET_URLS"]
            ?? Environment.GetEnvironmentVariable("WORKER_RESET_URLS");

        if (string.IsNullOrWhiteSpace(rawUrls))
        {
            return await ResetSingleClientAsync(ct);
        }

        var urls = rawUrls
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => u.TrimEnd('/'))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToArray();

        if (urls.Length == 0)
        {
            return await ResetSingleClientAsync(ct);
        }

        var tasks = urls.Select(url => ResetTargetUrlAsync(url, ct)).ToArray();
        var results = await Task.WhenAll(tasks);
        return results.All(ok => ok);
    }

    private async Task<bool> ResetSingleClientAsync(CancellationToken ct)
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

    private async Task<bool> ResetTargetUrlAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("worker");
            using var response = await client.PostAsync($"{baseUrl}/internal/reset", null, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Worker reset endpoint {BaseUrl} returned status {StatusCode}", baseUrl, response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to call Worker internal reset endpoint {BaseUrl}", baseUrl);
            return false;
        }
    }
}
