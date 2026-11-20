using Microsoft.AspNetCore.Mvc;
using Trading.Server.Services;
using Trading.Shared.Utils;

namespace Trading.Server.Controllers;

[ApiController]
[Route("api/history")]
public class HistoryController(IDatabaseQueryService dbQueryService) : ControllerBase
{
    [HttpGet("{symbol}")]
    public async Task<IActionResult> GetHistory(string symbol, [FromQuery] int limit = Constants.DefaultHistoryLimit, CancellationToken ct = default)
    {
        var records = await dbQueryService.GetHistoryAsync(symbol, limit, ct);
        return Ok(records);
    }
}
