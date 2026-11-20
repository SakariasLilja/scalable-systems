using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Trading.Shared.Models;
using Trading.Shared.Utils;

namespace Trading.Server.Services;

public interface ISseService
{
    Task RegisterClient(string clientId, HttpResponse response, CancellationToken ct);
    void SetSubscriptionState(string clientId, string symbol, string state);
    string GetClientSubscription(string clientId, string symbol);
    IEnumerable<string> GetActiveSymbols();
    IEnumerable<string> GetTrackedSymbols();
    Task BroadcastEvent(string eventType, object data);
    Task BroadcastWindowUpdate(FinalizedWindowRecord record);
    int ActiveConnectionCount { get; }
}

public class SseService(ILogger<SseService> logger) : ISseService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private class ClientConnection
    {
        public required string ClientId { get; init; }
        public required HttpResponse Response { get; init; }
        public required CancellationToken CancellationToken { get; init; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, ClientConnection> connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> subscriptions = new();

    public int ActiveConnectionCount => connections.Count;

    public async Task RegisterClient(string clientId, HttpResponse response, CancellationToken ct)
    {
        response.Headers.Append("Content-Type", "text/event-stream");
        response.Headers.Append("Cache-Control", "no-cache");
        response.Headers.Append("Connection", "keep-alive");
        response.Headers.Append("X-Accel-Buffering", "no");

        var connection = new ClientConnection
        {
            ClientId = clientId,
            Response = response,
            CancellationToken = ct
        };

        connections[clientId] = connection;
        logger.LogInformation("SSE Client {ClientId} connected. Active connections: {Count}", clientId, connections.Count);

        try
        {
            await SendSseEvent(connection, "connected", new { clientId, time = DateTime.UtcNow });
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            connections.TryRemove(new KeyValuePair<string, ClientConnection>(clientId, connection));
            subscriptions.TryRemove(clientId, out _);
            logger.LogInformation("SSE Client {ClientId} disconnected. Active connections: {Count}", clientId, connections.Count);
        }
    }

    public void SetSubscriptionState(string clientId, string symbol, string state)
    {
        var normalized = state?.ToLowerInvariant() ?? Constants.SubscriptionHidden;
        if (normalized != Constants.SubscriptionHidden &&
            normalized != Constants.SubscriptionTracked &&
            normalized != Constants.SubscriptionActive)
        {
            normalized = Constants.SubscriptionHidden;
        }

        var subs = subscriptions.GetOrAdd(clientId, _ => new ConcurrentDictionary<string, string>());
        subs[symbol] = normalized;
    }

    public string GetClientSubscription(string clientId, string symbol)
    {
        if (subscriptions.TryGetValue(clientId, out var subs) &&
            subs.TryGetValue(symbol, out var state))
        {
            return state;
        }

        return Constants.SubscriptionHidden;
    }

    public IEnumerable<string> GetActiveSymbols()
    {
        return subscriptions.Values
            .SelectMany(s => s)
            .Where(s => s.Value == Constants.SubscriptionActive)
            .Select(s => s.Key)
            .Distinct();
    }

    public IEnumerable<string> GetTrackedSymbols()
    {
        return subscriptions.Values
            .SelectMany(s => s)
            .Where(s => s.Value == Constants.SubscriptionTracked || s.Value == Constants.SubscriptionActive)
            .Select(s => s.Key)
            .Distinct();
    }

    public async Task BroadcastEvent(string eventType, object data)
    {
        var tasks = connections.Values.Select(conn => SendSseEvent(conn, eventType, data));
        await Task.WhenAll(tasks);
    }

    public async Task BroadcastWindowUpdate(FinalizedWindowRecord record)
    {
        var alertType = record.BuySignal ? "BUY" : record.SellSignal ? "SELL" : "NONE";

        var activePayload = new
        {
            symbol = record.SymbolId,
            closePrice = record.LastPrice,
            ema38 = record.Ema38,
            ema100 = record.Ema100,
            time = record.WindowEnd,
            buySignal = record.BuySignal,
            sellSignal = record.SellSignal
        };

        var trackedPayload = new
        {
            symbol = record.SymbolId,
            price = record.LastPrice,
            time = record.WindowEnd
        };

        var alertPayload = alertType != "NONE" ? new
        {
            symbol = record.SymbolId,
            type = alertType,
            closePrice = record.LastPrice,
            ema38 = record.Ema38,
            ema100 = record.Ema100,
            time = record.WindowEnd
        } : null;

        var tasks = new List<Task>();

        foreach (var (clientId, conn) in connections)
        {
            if (subscriptions.TryGetValue(clientId, out var subs) &&
                subs.TryGetValue(record.SymbolId, out var subState))
            {
                if (subState == Constants.SubscriptionActive)
                {
                    tasks.Add(SendSseEvent(conn, "active_update", activePayload));
                }
                else if (subState == Constants.SubscriptionTracked)
                {
                    tasks.Add(SendSseEvent(conn, "tracked_update", trackedPayload));
                }

                if (alertPayload != null && (subState == Constants.SubscriptionTracked || subState == Constants.SubscriptionActive))
                {
                    tasks.Add(SendSseEvent(conn, "advisory_alert", alertPayload));
                }
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    private async Task SendSseEvent(ClientConnection connection, string eventType, object data)
    {
        if (connection.CancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await connection.Lock.WaitAsync(connection.CancellationToken);
            try
            {
                var json = JsonSerializer.Serialize(data, JsonOptions);
                var message = $"event: {eventType}\ndata: {json}\n\n";
                await connection.Response.WriteAsync(message, connection.CancellationToken);
                await connection.Response.Body.FlushAsync(connection.CancellationToken);
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch
        {
            connections.TryRemove(new KeyValuePair<string, ClientConnection>(connection.ClientId, connection));
        }
    }
}
