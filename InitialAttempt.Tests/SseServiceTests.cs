using InitialAttempt.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InitialAttempt.Tests;

public class SseServiceTests
{
    [Fact]
    public void SetSubscriptionState_UpdatesClientSymbolState()
    {
        var service = new SseService(NullLogger<SseService>.Instance);
        string clientId = "client-1";
        string symbol = "RDSA.NL";

        service.SetSubscriptionState(clientId, symbol, "tracked");
        Assert.Equal("tracked", service.GetClientSubscription(clientId, symbol));

        service.SetSubscriptionState(clientId, symbol, "active");
        Assert.Equal("active", service.GetClientSubscription(clientId, symbol));

        service.SetSubscriptionState(clientId, symbol, "hidden");
        Assert.Equal("hidden", service.GetClientSubscription(clientId, symbol));
    }

    [Fact]
    public void GetActiveSymbols_ReturnsOnlyActiveSymbols()
    {
        var service = new SseService(NullLogger<SseService>.Instance);
        service.SetSubscriptionState("client-1", "RDSA.NL", "active");
        service.SetSubscriptionState("client-1", "A0HN5C.ETR", "tracked");
        service.SetSubscriptionState("client-2", "CBOT.FR", "hidden");

        var activeSymbols = service.GetActiveSymbols();
        Assert.Contains("RDSA.NL", activeSymbols);
        Assert.DoesNotContain("A0HN5C.ETR", activeSymbols);
        Assert.DoesNotContain("CBOT.FR", activeSymbols);
    }

    [Fact]
    public void SetSubscriptionState_RejectsInvalidState_DefaultsToHidden()
    {
        var service = new SseService(NullLogger<SseService>.Instance);
        service.SetSubscriptionState("client-1", "RDSA.NL", "invalid_state");
        Assert.Equal("hidden", service.GetClientSubscription("client-1", "RDSA.NL"));
    }
}
