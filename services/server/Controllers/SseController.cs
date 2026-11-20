using Microsoft.AspNetCore.Mvc;
using Trading.Server.Services;

namespace Trading.Server.Controllers;

public record SetSubscriptionRequest(string ClientId, string Symbol, string State);

[ApiController]
[Route("api")]
public class SseController(ISseService sseService) : ControllerBase
{
    [HttpGet("sse/stream")]
    public async Task GetStream([FromQuery] string? clientId, [FromQuery] string? sessionId, CancellationToken ct)
    {
        var resolvedId = !string.IsNullOrWhiteSpace(clientId)
            ? clientId
            : !string.IsNullOrWhiteSpace(sessionId)
                ? sessionId
                : Guid.NewGuid().ToString("N");

        await sseService.RegisterClient(resolvedId, Response, ct);
    }

    [HttpPost("subscription/set-state")]
    public IActionResult SetState([FromBody] SetSubscriptionRequest request)
    {
        sseService.SetSubscriptionState(request.ClientId, request.Symbol, request.State);
        return Ok(new { status = "updated", request.Symbol, request.State });
    }
}
