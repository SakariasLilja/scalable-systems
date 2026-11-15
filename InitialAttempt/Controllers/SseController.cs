using InitialAttempt.Services;
using Microsoft.AspNetCore.Mvc;

namespace InitialAttempt.Controllers;

public record SetSubscriptionRequest(string ClientId, string Symbol, string State);

[ApiController]
[Route("api")]
public class SseController(ISseService sseService) : ControllerBase
{
    [HttpGet("sse/stream")]
    public async Task GetStream([FromQuery] string? clientId, CancellationToken ct)
    {
        var id = string.IsNullOrWhiteSpace(clientId) ? Guid.NewGuid().ToString("N") : clientId;
        await sseService.RegisterClient(id, Response, ct);
    }

    [HttpPost("subscription/set-state")]
    public IActionResult SetState([FromBody] SetSubscriptionRequest request)
    {
        sseService.SetSubscriptionState(request.ClientId, request.Symbol, request.State);
        return Ok(new { status = "updated", request.Symbol, request.State });
    }
}
