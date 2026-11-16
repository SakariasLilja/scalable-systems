using InitialAttempt.Services;
using InitialAttempt.Utils;
using Microsoft.AspNetCore.Mvc;

namespace InitialAttempt.Controllers;

[ApiController]
[Route("api/symbols")]
public class SymbolsController(ISymbolCatalogService catalogService) : ControllerBase
{
    [HttpGet("search")]
    public IActionResult Search([FromQuery] string? q, [FromQuery] int limit = Constants.DefaultSymbolSearchLimit)
    {
        var results = catalogService.SearchSymbols(q, limit);
        return Ok(results);
    }
}
