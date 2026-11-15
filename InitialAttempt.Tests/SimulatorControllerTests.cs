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
        public bool Paused = false;
        public bool Resumed = false;
        public bool Started = false;

        public Task StartSimulation(string filePath, double speedFactor, CancellationToken ct = default)
        {
            Started = true;
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
        public string CSV_PREFIX => "debs2022-gc-trading-day-";
        public string CSV_SUFFIX => ".csv";
        public string CSV_DIRECTORY => ".";
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
