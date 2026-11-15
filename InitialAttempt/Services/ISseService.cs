using InitialAttempt.Models;
using Microsoft.AspNetCore.Http;

namespace InitialAttempt.Services;

public interface ISseService
{
    Task RegisterClient(string clientId, HttpResponse response, CancellationToken ct);
    void SetSubscriptionState(string clientId, string symbol, string state);
    string GetClientSubscription(string clientId, string symbol);
    IEnumerable<string> GetActiveSymbols();
    IEnumerable<string> GetTrackedSymbols();
    Task BroadcastEvent(string eventType, object data);
    Task BroadcastAdvisory(FinalizedWindowRecord record);
}
