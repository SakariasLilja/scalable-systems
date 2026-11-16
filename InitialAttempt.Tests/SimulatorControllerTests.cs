using System.Threading.Channels;
using InitialAttempt.Controllers;
using InitialAttempt.Models;
using InitialAttempt.Services;
using InitialAttempt.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InitialAttempt.Tests;

public class SimulatorControllerTests
{
    private class FakeSimulationEngine : ISimulationEngine
    {
        public double LastSetSpeed = 1.0;
        public string? LastStartedPath = null;
        public bool Paused = false;
        public bool Resumed = false;
        public bool Started = false;

        public Task StartSimulation(string filePath, double speedFactor, CancellationToken ct = default)
        {
            Started = true;
            LastStartedPath = filePath;
            LastSetSpeed = speedFactor;
            return Task.CompletedTask;
        }

        public void PauseSimulation() => Paused = true;
        public void ResumeSimulation() => Resumed = true;
        public void SetSpeedFactor(double speedFactor) => LastSetSpeed = speedFactor;

        public SimulationStatus GetStatus() =>
            new(SimulationState.Running, LastSetSpeed, 100, DateTime.UtcNow, TimeSpan.FromSeconds(2));

        public ChannelReader<MinimalTradingEntry> GetEventChannel() =>
            Channel.CreateUnbounded<MinimalTradingEntry>().Reader;
    }

    private class FakeConstants : IConstants
    {
        public string CsvPrefix { get; set; } = "debs2022-gc-trading-day-";
        public string CsvSuffix { get; set; } = ".csv";
        public string CsvDirectory { get; set; } = ".";
    }

    [Fact]
    public void GetDays_ReturnsDiscoveredTradingDays()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "debs2022-gc-trading-day-08-11-21.csv"), "Id,SecType\n");
            File.WriteAllText(Path.Combine(tempDir, "debs2022-gc-trading-day-13-11-21.csv"), "Id,SecType\n");
            File.WriteAllText(Path.Combine(tempDir, "other-file.txt"), "text\n");

            var constants = new FakeConstants { CsvDirectory = tempDir };
            var fakeEngine = new FakeSimulationEngine();
            var controller = new SimulatorController(NullLogger<SimulatorController>.Instance, fakeEngine, constants);

            var result = controller.GetDays();
            var okResult = Assert.IsType<OkObjectResult>(result);
            var days = Assert.IsAssignableFrom<IEnumerable<TradingDayInfo>>(okResult.Value).ToList();

            Assert.Equal(2, days.Count);
            Assert.Contains(days, d => d.TradingDay == "08-11-21");
            Assert.Contains(days, d => d.TradingDay == "13-11-21");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Start_WithSpecificTradingDay_UsesTradingDayPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var targetFile = Path.Combine(tempDir, "debs2022-gc-trading-day-08-11-21.csv");
            File.WriteAllText(targetFile, "Id,SecType\n");

            var constants = new FakeConstants { CsvDirectory = tempDir };
            var fakeEngine = new FakeSimulationEngine();
            var controller = new SimulatorController(NullLogger<SimulatorController>.Instance, fakeEngine, constants);

            var request = new StartSimulationRequest(null, 1.0, "08-11-21");
            var result = await controller.Start(request);

            Assert.IsType<AcceptedResult>(result);
            Assert.Equal(targetFile, fakeEngine.LastStartedPath);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Start_WithInvalidTradingDay_ReturnsBadRequest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var constants = new FakeConstants { CsvDirectory = tempDir };
            var fakeEngine = new FakeSimulationEngine();
            var controller = new SimulatorController(NullLogger<SimulatorController>.Instance, fakeEngine, constants);

            var request = new StartSimulationRequest(null, 1.0, "99-99-99");
            var result = await controller.Start(request);

            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains("not found", badRequest.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetStatus_ReturnsOkWithStatus()
    {
        var fakeEngine = new FakeSimulationEngine();
        var controller = new SimulatorController(NullLogger<SimulatorController>.Instance, fakeEngine, new FakeConstants());

        var result = controller.GetStatus();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var status = Assert.IsType<SimulationStatus>(okResult.Value);
        Assert.Equal(SimulationState.Running, status.State);
    }

    [Fact]
    public void SetSpeed_CallsEngineSetSpeedFactor()
    {
        var fakeEngine = new FakeSimulationEngine();
        var controller = new SimulatorController(NullLogger<SimulatorController>.Instance, fakeEngine, new FakeConstants());

        var result = controller.SetSpeed(new SetSpeedRequest(5.0));

        Assert.Equal(5.0, fakeEngine.LastSetSpeed);
        Assert.IsType<OkResult>(result);
    }
}
