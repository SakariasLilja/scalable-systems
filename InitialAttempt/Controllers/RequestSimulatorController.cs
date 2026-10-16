using System.Diagnostics;
using InitialAttempt.Services;
using Microsoft.AspNetCore.Mvc;

namespace InitialAttempt.Controllers;

[ApiController]
[Route("[controller]")]
public class RequestSimulatorController(
    ILogger<RequestSimulatorController> logger,
    IReadFileService readFileService
) : ControllerBase
{
    private Queue<string> queue = new();

    [HttpGet(Name = "GetFirstEntry")]
    public async Task<IEnumerable<string>> GetFirstEntry()
    {
        StreamReader? reader = null;
        string? firstLine = null;
        Stopwatch? stopwatch = null;
        try
        {
            reader = readFileService.OpenStreamReader("13-11-21");
            
            stopwatch = Stopwatch.StartNew();
            await readFileService.SkipComments(reader);
            stopwatch.Stop();
            logger.LogInformation("Time taken to skip comments: {ElapsedMilliseconds} ms", stopwatch.ElapsedMilliseconds);
            stopwatch = null;
            
            firstLine = await reader.ReadLineAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred while opening stream reader");
        }
        finally
        {
            // Ensure the reader is closed in case of an exception
            reader?.Close();
            stopwatch?.Stop();
        }

        return [firstLine ?? "No data found"];
    }
}