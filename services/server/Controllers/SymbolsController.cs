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

    [HttpGet("markets")]
    public async Task<IActionResult> GetMarkets(CancellationToken ct = default)
    {
        var markets = await dbQueryService.GetMarketsAsync(ct);
        return Ok(markets);
    }

    [HttpGet("by-market/{market}")]
    public async Task<IActionResult> GetByMarket(string market, [FromQuery] string? q, [FromQuery] int limit = Constants.DefaultMarketSymbolsLimit, CancellationToken ct = default)
    {
        var results = await dbQueryService.GetSymbolsByMarketAsync(market, q, limit, ct);
        return Ok(results);
    }
}
