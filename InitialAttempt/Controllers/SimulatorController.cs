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
    public async Task<IActionResult> start([FromBody] StartSimulationRequest? request)
    {
        var filePath = request?.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            filePath = Path.Combine(constants.CSV_DIRECTORY, $"{constants.CSV_PREFIX}13-11-21{constants.CSV_SUFFIX}");
        }

        var speed = request?.SpeedFactor ?? 1.0;
        logger.LogInformation("Simulation starting with path {FilePath} and speed {Speed}", filePath, speed);
        await simulationEngine.startSimulation(filePath, speed);
        return Accepted(simulationEngine.getStatus());
    }

    [HttpPost("pause")]
    public IActionResult pause()
    {
        simulationEngine.pauseSimulation();
        return Ok(simulationEngine.getStatus());
    }

    [HttpPost("resume")]
    public IActionResult resume()
    {
        simulationEngine.resumeSimulation();
        return Ok(simulationEngine.getStatus());
    }

    [HttpPost("speed")]
    public IActionResult setSpeed([FromBody] SetSpeedRequest request)
    {
        simulationEngine.setSpeedFactor(request.SpeedFactor);
        return Ok();
    }

    [HttpGet("status")]
    public IActionResult getStatus()
    {
        return Ok(simulationEngine.getStatus());
    }
}
