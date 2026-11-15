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
    TimeSpan ElapsedWallTime
);

public interface ISimulationEngine
{
    Task startSimulation(string filePath, double speedFactor, CancellationToken ct = default);
    void pauseSimulation();
    void resumeSimulation();
    void setSpeedFactor(double speedFactor);
    SimulationStatus getStatus();
    ChannelReader<MinimalTradingEntry> getEventChannel();
}
