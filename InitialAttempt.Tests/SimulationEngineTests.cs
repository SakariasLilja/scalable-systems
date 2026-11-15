using InitialAttempt.Services;
using Xunit;

namespace InitialAttempt.Tests;

public class SimulationEngineTests
{
    [Theory]
    [InlineData(1000, 1.0, 1000)]
    [InlineData(1000, 2.0, 500)]
    [InlineData(1000, 10.0, 100)]
    [InlineData(1000, 0.0, 0)]
    public void calculateDelay_ComputesCorrectScaledDelay(long deltaMs, double speedFactor, long expectedDelayMs)
    {
        var delay = SimulationEngine.calculateDelay(deltaMs, speedFactor);
        Assert.Equal(expectedDelayMs, delay);
    }
}
