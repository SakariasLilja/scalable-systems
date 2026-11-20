using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Trading.Server.Services;

namespace Trading.Server.Controllers;

[ApiController]
public class DataController(
    IDatabaseQueryService dbQueryService,
    IWorkerResetClient workerResetClient,
    IRedisResetPublisher redisResetPublisher,
    ISseService sseService
) : ControllerBase
{
    [HttpDelete("/api/data")]
    public async Task<IActionResult> DeleteData(CancellationToken ct)
    {
        bool workerReset = await workerResetClient.ResetWorkerAsync(ct);
        if (!workerReset)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                cleared = false,
                error = "Failed to reset Worker state"
            });
        }

        await dbQueryService.TruncateTradingWindowsAsync(ct);
        dbQueryService.ClearLiveSymbols();
        await redisResetPublisher.PublishResetAsync(ct);
        return Ok(new { cleared = true, time = DateTime.UtcNow });
    }

    [HttpGet("/api/status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            connectedClients = sseService.ActiveConnectionCount,
            knownSymbols = dbQueryService.LiveSymbolCount,
            latestSimulationTime = dbQueryService.LatestSimulationTime
        });
    }

    [HttpGet("/health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", connectedClients = sseService.ActiveConnectionCount });
    }
}
