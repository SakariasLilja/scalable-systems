using Microsoft.AspNetCore.Mvc;
using Trading.Server.Services;
using Trading.Shared.Utils;

namespace Trading.Server.Controllers;

[ApiController]
[Route("api/symbols")]
public class SymbolsController(IDatabaseQueryService dbQueryService) : ControllerBase
{
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int limit = Constants.DefaultSymbolSearchLimit, CancellationToken ct = default)
    {
        var results = await dbQueryService.SearchSymbolsAsync(q, limit, ct);
        return Ok(results);
    }
}
