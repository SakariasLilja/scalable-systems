using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Trading.Worker.Services;

namespace Trading.Worker.Controllers;

[ApiController]
public class IngestController(
    ITickIngestionService ingestionService,
    IAsyncWarningLogger warningLogger
) : ControllerBase
{
    [HttpPost("/api/ingest")]
    public async Task<IActionResult> PostTick(CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            warningLogger.EnqueueWarning($"Malformed JSON tick payload: {ex.Message}");
            return BadRequest();
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (RawTickParser.TryParseTradeTick(element, warningLogger, out var entry) && entry is not null)
                    {
                        await ingestionService.EnqueueAsync(entry, ct);
                    }
                }
            }
            else if (RawTickParser.TryParseTradeTick(doc.RootElement, warningLogger, out var entry) && entry is not null)
            {
                await ingestionService.EnqueueAsync(entry, ct);
            }
        }

        return Accepted();
    }

    [HttpPost("/internal/reset")]
    public IActionResult Reset()
    {
        ingestionService.Reset();
        return Ok(new { reset = true });
    }

    [HttpGet("/health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", ingestedCount = ingestionService.IngestedCount });
    }
}
