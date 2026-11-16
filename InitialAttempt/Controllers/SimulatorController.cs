using InitialAttempt.Services;
using InitialAttempt.Utils;
using Microsoft.AspNetCore.Mvc;

namespace InitialAttempt.Controllers;

public record TradingDayInfo(string TradingDay, string FileName, long FileSizeBytes);
public record StartSimulationRequest(string? FilePath = null, double SpeedFactor = 1.0, string? TradingDay = null);
public record SetSpeedRequest(double SpeedFactor);

[ApiController]
[Route("api/simulator")]
public class SimulatorController(
    ILogger<SimulatorController> logger,
    ISimulationEngine simulationEngine,
    IConstants constants
) : ControllerBase
{
    [HttpGet("days")]
    public IActionResult GetDays()
    {
        if (!Directory.Exists(constants.CsvDirectory))
        {
            return Ok(Array.Empty<TradingDayInfo>());
        }

        var dirInfo = new DirectoryInfo(constants.CsvDirectory);
        var searchPattern = $"{constants.CsvPrefix}*{constants.CsvSuffix}";
        var files = dirInfo.GetFiles(searchPattern);

        var days = files.Select(f =>
        {
            var day = f.Name
                .Replace(constants.CsvPrefix, "")
                .Replace(constants.CsvSuffix, "");
            return new TradingDayInfo(day, f.Name, f.Length);
        })
        .OrderBy(d => d.TradingDay)
        .ToList();

        return Ok(days);
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartSimulationRequest? request)
    {
        string filePath;
        if (!string.IsNullOrWhiteSpace(request?.TradingDay))
        {
            filePath = Path.Combine(constants.CsvDirectory, $"{constants.CsvPrefix}{request.TradingDay}{constants.CsvSuffix}");
            if (!System.IO.File.Exists(filePath))
            {
                return BadRequest($"Trading day file not found: {filePath}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(request?.FilePath))
        {
            filePath = request.FilePath;
        }
        else
        {
            filePath = Path.Combine(constants.CsvDirectory, $"{constants.CsvPrefix}{Constants.DefaultTradingDate}{constants.CsvSuffix}");
        }

        var speed = request?.SpeedFactor ?? Constants.DefaultSimulationSpeed;
        logger.LogInformation("Simulation starting with path {FilePath} and speed {Speed}", filePath, speed);
        await simulationEngine.StartSimulation(filePath, speed);
        return Accepted(simulationEngine.GetStatus());
    }

    [HttpPost("pause")]
    public IActionResult Pause()
    {
        simulationEngine.PauseSimulation();
        return Ok(simulationEngine.GetStatus());
    }

    [HttpPost("resume")]
    public IActionResult Resume()
    {
        simulationEngine.ResumeSimulation();
        return Ok(simulationEngine.GetStatus());
    }

    [HttpPost("speed")]
    public IActionResult SetSpeed([FromBody] SetSpeedRequest request)
    {
        if (request.SpeedFactor <= 0)
        {
            return BadRequest("Speed factor must be greater than zero.");
        }

        simulationEngine.SetSpeedFactor(request.SpeedFactor);
        return Ok();
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(simulationEngine.GetStatus());
    }
}
