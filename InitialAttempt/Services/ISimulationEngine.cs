using System.Threading.Channels;
using InitialAttempt.Models;

namespace InitialAttempt.Services;

public enum SimulationState
{
    Stopped,
    Running,
    Paused
}

public record SimulationStatus(
    SimulationState State,
    double SpeedFactor,
    long ProcessedCount,
    DateTime? CurrentSimulationTime,
    TimeSpan ElapsedWallTime,
    string? LastError = null
);

public interface ISimulationEngine
{
    Task StartSimulation(string filePath, double speedFactor, CancellationToken ct = default);
    void PauseSimulation();
    void ResumeSimulation();
    void SetSpeedFactor(double speedFactor);
    SimulationStatus GetStatus();
    ChannelReader<MinimalTradingEntry> GetEventChannel();
}
