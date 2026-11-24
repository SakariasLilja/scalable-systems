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
    event Action<int>? ActiveConnectionCountChanged;
}

public class SseService(ILogger<SseService> logger, int batchIntervalMs = Constants.SseBatchIntervalMs) : ISseService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record QueuedSseEvent(string EventType, object Payload);

    private class ClientConnection
    {
        public required string ClientId { get; init; }
        public required HttpResponse Response { get; init; }
        public required CancellationToken CancellationToken { get; init; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public ConcurrentQueue<QueuedSseEvent> PendingEvents { get; } = new();
    }

    private readonly int effectiveBatchIntervalMs = Math.Max(10, batchIntervalMs);
    private readonly ConcurrentDictionary<string, ClientConnection> connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> subscriptions = new();
    private readonly object connectionStateLock = new();

    public int ActiveConnectionCount => connections.Count;
    public event Action<int>? ActiveConnectionCountChanged;

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

        bool becameActive;
        int currentCount;
        lock (connectionStateLock)
        {
            bool wasEmpty = connections.IsEmpty;
            connections[clientId] = connection;
            currentCount = connections.Count;
            becameActive = wasEmpty && currentCount > 0;
        }

        if (becameActive)
        {
            ActiveConnectionCountChanged?.Invoke(currentCount);
        }

        logger.LogInformation("SSE Client {ClientId} connected. Active connections: {Count}", clientId, currentCount);

        try
        {
            await SendSseEvent(connection, "connected", new { clientId, time = DateTime.UtcNow });
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(effectiveBatchIntervalMs));
            while (await timer.WaitForNextTickAsync(ct))
            {
                await FlushPendingBatchAsync(connection);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            bool becameEmpty = false;
            lock (connectionStateLock)
            {
                if (connections.TryGetValue(clientId, out var current) && ReferenceEquals(current, connection))
                {
                    connections.TryRemove(clientId, out _);
                    subscriptions.TryRemove(clientId, out _);
                    becameEmpty = connections.IsEmpty;
                }
            }

            if (becameEmpty)
            {
                ActiveConnectionCountChanged?.Invoke(0);
            }

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
        if (string.Equals(eventType, "reset", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var conn in connections.Values)
            {
                while (conn.PendingEvents.TryDequeue(out _)) { }
            }
        }

        var tasks = connections.Values.Select(conn => SendSseEvent(conn, eventType, data));
        await Task.WhenAll(tasks);
    }

    public Task BroadcastWindowUpdate(FinalizedWindowRecord record)
    {
        var alertType = record.BuySignal ? "BUY" : record.SellSignal ? "SELL" : "NONE";

        var windowPayload = new
        {
            symbol = record.SymbolId,
            closePrice = record.LastPrice,
            ema38 = record.Ema38,
            ema100 = record.Ema100,
            time = record.WindowEnd,
            buySignal = record.BuySignal,
            sellSignal = record.SellSignal
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

        foreach (var (clientId, conn) in connections)
        {
            if (alertPayload != null)
            {
                conn.PendingEvents.Enqueue(new QueuedSseEvent("advisory_alert", alertPayload));
            }

            if (subscriptions.TryGetValue(clientId, out var subs) &&
                subs.TryGetValue(record.SymbolId, out var subState) &&
                (subState == Constants.SubscriptionTracked || subState == Constants.SubscriptionActive))
            {
                conn.PendingEvents.Enqueue(new QueuedSseEvent("window_update", windowPayload));
            }
        }

        return Task.CompletedTask;
    }

    private async Task FlushPendingBatchAsync(ClientConnection connection)
    {
        if (connection.PendingEvents.IsEmpty)
        {
            return;
        }

        var batch = new List<QueuedSseEvent>();
        while (connection.PendingEvents.TryDequeue(out var item))
        {
            batch.Add(item);
        }

        if (batch.Count > 0)
        {
            await SendSseEvent(connection, "batch_update", batch);
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
            bool becameEmpty = false;
            lock (connectionStateLock)
            {
                if (connections.TryGetValue(connection.ClientId, out var current) && ReferenceEquals(current, connection))
                {
                    connections.TryRemove(connection.ClientId, out _);
                    subscriptions.TryRemove(connection.ClientId, out _);
                    becameEmpty = connections.IsEmpty;
                }
            }

            if (becameEmpty)
            {
                ActiveConnectionCountChanged?.Invoke(0);
            }
        }
    }
}
