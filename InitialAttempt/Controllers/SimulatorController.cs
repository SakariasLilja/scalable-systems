using InitialAttempt.Services;
using InitialAttempt.Utils;
using Microsoft.AspNetCore.Mvc;

namespace InitialAttempt.Controllers;

public record StartSimulationRequest(string? FilePath, double SpeedFactor = 1.0);
public record SetSpeedRequest(double SpeedFactor);

[ApiController]
[Route("api/simulator")]
public class SimulatorController(
    ILogger<SimulatorController> logger,
    ISimulationEngine simulationEngine,
    IConstants constants
) : ControllerBase
{
    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartSimulationRequest? request)
    {
        var filePath = request?.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
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
