using System.Collections.Concurrent;
using System.Text.Json;
using InitialAttempt.Models;
using Microsoft.AspNetCore.Http;

namespace InitialAttempt.Services;

public class SseService(ILogger<SseService> logger) : ISseService
{
    private class ClientConnection
    {
        public required string ClientId { get; init; }
        public required HttpResponse Response { get; init; }
        public required CancellationToken CancellationToken { get; init; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, ClientConnection> connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> subscriptions = new();

    public async Task RegisterClient(string clientId, HttpResponse response, CancellationToken ct)
    {
        response.Headers.Append("Content-Type", "text/event-stream");
        response.Headers.Append("Cache-Control", "no-cache");
        response.Headers.Append("Connection", "keep-alive");

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
            connections.TryRemove(clientId, out _);
            logger.LogInformation("SSE Client {ClientId} disconnected. Active connections: {Count}", clientId, connections.Count);
        }
    }

    public void SetSubscriptionState(string clientId, string symbol, string state)
    {
        var subs = subscriptions.GetOrAdd(clientId, _ => new ConcurrentDictionary<string, string>());
        subs[symbol] = state.ToLowerInvariant();
    }

    public string GetClientSubscription(string clientId, string symbol)
    {
        if (subscriptions.TryGetValue(clientId, out var subs) &&
            subs.TryGetValue(symbol, out var state))
        {
            return state;
        }

        return "hidden";
    }

    public IEnumerable<string> GetActiveSymbols()
    {
        return subscriptions.Values
            .SelectMany(s => s)
            .Where(s => s.Value == "active")
            .Select(s => s.Key)
            .Distinct();
    }

    public IEnumerable<string> GetTrackedSymbols()
    {
        return subscriptions.Values
            .SelectMany(s => s)
            .Where(s => s.Value is "tracked" or "active")
            .Select(s => s.Key)
            .Distinct();
    }

    public async Task BroadcastEvent(string eventType, object data)
    {
        var tasks = connections.Values.Select(conn => SendSseEvent(conn, eventType, data));
        await Task.WhenAll(tasks);
    }

    public async Task BroadcastAdvisory(FinalizedWindowRecord record)
    {
        var alertType = record.BuySignal ? "BUY" : record.SellSignal ? "SELL" : "NONE";
        if (alertType == "NONE") return;

        var payload = new
        {
            symbol = record.SymbolId,
            type = alertType,
            closePrice = record.LastPrice,
            ema38 = record.Ema38,
            ema100 = record.Ema100,
            time = record.WindowEnd
        };

        foreach (var (clientId, conn) in connections)
        {
            if (subscriptions.TryGetValue(clientId, out var subs) &&
                subs.TryGetValue(record.SymbolId, out var sub) &&
                sub is "tracked" or "active")
            {
                await SendSseEvent(conn, "advisory_alert", payload);
            }
        }
    }

    private async Task SendSseEvent(ClientConnection connection, string eventType, object data)
    {
        if (connection.CancellationToken.IsCancellationRequested) return;

        await connection.Lock.WaitAsync(connection.CancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(data);
            var message = $"event: {eventType}\ndata: {json}\n\n";
            await connection.Response.WriteAsync(message, connection.CancellationToken);
            await connection.Response.Body.FlushAsync(connection.CancellationToken);
        }
        catch
        {
            connections.TryRemove(connection.ClientId, out _);
        }
        finally
        {
            connection.Lock.Release();
        }
    }
}
