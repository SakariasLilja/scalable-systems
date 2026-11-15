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
            filePath = Path.Combine(constants.CSV_DIRECTORY, $"{constants.CSV_PREFIX}13-11-21{constants.CSV_SUFFIX}");
        }

        var speed = request?.SpeedFactor ?? 1.0;
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
        simulationEngine.SetSpeedFactor(request.SpeedFactor);
        return Ok();
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(simulationEngine.GetStatus());
    }
}
