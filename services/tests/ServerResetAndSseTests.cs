using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Trading.Server.Controllers;
using Trading.Server.Services;
using Trading.Shared.Models;
using Trading.Shared.Utils;

namespace Trading.Tests;

public class ServerResetAndSseTests
{
    private class RecordingDatabaseQueryService(List<string> callOrder) : IDatabaseQueryService
    {
        public int LiveSymbolCount => 0;
        public DateTime? LatestSimulationTime => null;

        public int LastAdvisoriesLimit { get; private set; }
        public int StubKnownSymbols { get; set; }
        public DateTime? StubLatestTime { get; set; }
        public IReadOnlyList<AdvisoryRecord> StubAdvisories { get; set; } = [];

        public Task<IReadOnlyList<FinalizedWindowRecord>> GetHistoryAsync(string symbol, int limit, DateTime? before = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FinalizedWindowRecord>>([]);

        public Task<IReadOnlyList<AdvisoryRecord>> GetRecentAdvisoriesAsync(int limit, CancellationToken ct = default)
        {
            LastAdvisoriesLimit = limit;
            return Task.FromResult(StubAdvisories);
        }

        public Task<(int KnownSymbols, DateTime? LatestSimulationTime)> GetStatusSummaryAsync(CancellationToken ct = default) =>
            Task.FromResult((StubKnownSymbols, StubLatestTime));

        public Task<IReadOnlyList<string>> SearchSymbolsAsync(string? query, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> GetMarketsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> GetSymbolsByMarketAsync(string market, string? query, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task TruncateTradingWindowsAsync(CancellationToken ct = default)
        {
            callOrder.Add("truncate");
            return Task.CompletedTask;
        }

        public void RecordLiveSymbol(string symbolId, DateTime windowEnd)
        {
        }

        public void ClearLiveSymbols() => callOrder.Add("clear-symbols");
    }

    private class RecordingWorkerResetClient(List<string> callOrder, bool shouldSucceed = true) : IWorkerResetClient
    {
        public Task<bool> ResetWorkerAsync(CancellationToken ct = default)
        {
            callOrder.Add("reset-worker");
            return Task.FromResult(shouldSucceed);
        }
    }

    private class RecordingRedisResetPublisher(List<string> callOrder) : IRedisResetPublisher
    {
        public Task PublishResetAsync(CancellationToken ct = default)
        {
            callOrder.Add("publish-reset");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DataController_DeleteData_ResetsWorkerBeforeTruncatingDbAndClearsAllState()
    {
        var callOrder = new List<string>();
        var dbService = new RecordingDatabaseQueryService(callOrder);
        var workerClient = new RecordingWorkerResetClient(callOrder, shouldSucceed: true);
        var resetPublisher = new RecordingRedisResetPublisher(callOrder);
        var sseService = new SseService(NullLogger<SseService>.Instance);

        var controller = new DataController(dbService, workerClient, resetPublisher, sseService);
        var result = await controller.DeleteData(CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(["reset-worker", "truncate", "clear-symbols", "publish-reset"], callOrder);
    }

    [Fact]
    public async Task DataController_DeleteData_WhenWorkerResetFails_Returns502AndDoesNotTruncateOrPublish()
    {
        var callOrder = new List<string>();
        var dbService = new RecordingDatabaseQueryService(callOrder);
        var workerClient = new RecordingWorkerResetClient(callOrder, shouldSucceed: false);
        var resetPublisher = new RecordingRedisResetPublisher(callOrder);
        var sseService = new SseService(NullLogger<SseService>.Instance);

        var controller = new DataController(dbService, workerClient, resetPublisher, sseService);
        var result = await controller.DeleteData(CancellationToken.None);

        var objResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, objResult.StatusCode);
        Assert.Equal(["reset-worker"], callOrder);
    }

    [Fact]
    public async Task SseService_SetSubscriptionAndBroadcast_TracksStatesAndFiltersUpdates()
    {
        var sseService = new SseService(NullLogger<SseService>.Instance);
        var httpContext = new DefaultHttpContext();
        var bodyStream = new MemoryStream();
        httpContext.Response.Body = bodyStream;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var registerTask = sseService.RegisterClient("client_1", httpContext.Response, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (sseService.ActiveConnectionCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, sseService.ActiveConnectionCount);

        sseService.SetSubscriptionState("client_1", "SAP.ETR", "ACTIVE");
        sseService.SetSubscriptionState("client_1", "BMW.ETR", Constants.SubscriptionTracked);
        sseService.SetSubscriptionState("client_1", "ADS.ETR", "invalid_state");

        Assert.Equal(Constants.SubscriptionActive, sseService.GetClientSubscription("client_1", "SAP.ETR"));
        Assert.Equal(Constants.SubscriptionTracked, sseService.GetClientSubscription("client_1", "BMW.ETR"));
        Assert.Equal(Constants.SubscriptionHidden, sseService.GetClientSubscription("client_1", "ADS.ETR"));
        Assert.Equal(["SAP.ETR"], sseService.GetActiveSymbols().ToArray());
        Assert.Contains("SAP.ETR", sseService.GetTrackedSymbols());
        Assert.Contains("BMW.ETR", sseService.GetTrackedSymbols());

        var record = new FinalizedWindowRecord(
            "SAP.ETR",
            new DateTime(2021, 11, 8, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2021, 11, 8, 8, 5, 0, DateTimeKind.Utc),
            125.5,
            120.0,
            126.0,
            true,
            false);

        await sseService.BroadcastWindowUpdate(record);
        await Task.Delay(250);
        await sseService.BroadcastEvent("reset", new { cleared = true });

        cts.Cancel();
        await registerTask;

        string sseOutput = Encoding.UTF8.GetString(bodyStream.ToArray());
        Assert.Contains("event: connected", sseOutput);
        Assert.Contains("event: batch_update", sseOutput);
        Assert.Contains("\"window_update\"", sseOutput);
        Assert.Contains("\"advisory_alert\"", sseOutput);
        Assert.Contains("event: reset", sseOutput);
        Assert.Contains("SAP.ETR", sseOutput);
    }

    [Fact]
    public async Task SseService_BroadcastWindowUpdate_DeliversAdvisoryAlertToUnsubscribedClient()
    {
        var sseService = new SseService(NullLogger<SseService>.Instance);
        var httpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var registerTask = sseService.RegisterClient("client_unsub", httpContext.Response, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (sseService.ActiveConnectionCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await sseService.BroadcastWindowUpdate(new FinalizedWindowRecord(
            "UNSUB.ETR",
            new DateTime(2021, 11, 8, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2021, 11, 8, 8, 5, 0, DateTimeKind.Utc),
            125.5,
            120.0,
            126.0,
            true,
            false));

        await Task.Delay(250);
        cts.Cancel();
        await registerTask;

        string sseOutput = Encoding.UTF8.GetString(((MemoryStream)httpContext.Response.Body).ToArray());
        Assert.Contains("\"advisory_alert\"", sseOutput);
        Assert.Contains("UNSUB.ETR", sseOutput);
        Assert.DoesNotContain("\"window_update\"", sseOutput);
    }

    [Fact]
    public void DatabaseQueryService_MergeAndFilterLiveSymbols_MatchesPrefixAndRespectsLimitAndClear()
    {
        var dbService = new DatabaseQueryService(new ConfigurationBuilder().Build(), NullLogger<DatabaseQueryService>.Instance);

        var t1 = new DateTime(2021, 11, 8, 8, 5, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2021, 11, 8, 8, 10, 0, DateTimeKind.Utc);
        dbService.RecordLiveSymbol("SAP.ETR", t1);
        dbService.RecordLiveSymbol("BMW.ETR", t2);
        dbService.RecordLiveSymbol("SIE.ETR", t1);

        Assert.Equal(3, dbService.LiveSymbolCount);
        Assert.Equal(t2, dbService.LatestSimulationTime);

        var filtered = DatabaseQueryService.FilterSymbols(["SAP.ETR", "BMW.ETR", "SIE.ETR", "sap.etr", "SART.ETR"], "s", 2);
        Assert.Equal(["SAP.ETR", "SART.ETR"], filtered);

        dbService.ClearLiveSymbols();
        Assert.Equal(0, dbService.LiveSymbolCount);
        Assert.Null(dbService.LatestSimulationTime);
    }

    [Fact]
    public async Task SseService_ReconnectWithSameClientId_DoesNotWipeNewConnectionSubscriptions()
    {
        var sseService = new SseService(NullLogger<SseService>.Instance);
        var ctx1 = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        var ctx2 = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var reg1 = sseService.RegisterClient("tab_client", ctx1.Response, cts1.Token);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (sseService.ActiveConnectionCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        sseService.SetSubscriptionState("tab_client", "SAP.ETR", Constants.SubscriptionActive);

        var reg2 = sseService.RegisterClient("tab_client", ctx2.Response, cts2.Token);
        await Task.Delay(30);

        cts1.Cancel();
        await reg1;

        Assert.Equal(Constants.SubscriptionActive, sseService.GetClientSubscription("tab_client", "SAP.ETR"));
        Assert.Equal(1, sseService.ActiveConnectionCount);

        await sseService.BroadcastWindowUpdate(new FinalizedWindowRecord(
            "SAP.ETR",
            new DateTime(2021, 11, 8, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2021, 11, 8, 8, 5, 0, DateTimeKind.Utc),
            125.5,
            120.0,
            126.0,
            false,
            false));

        await Task.Delay(250);

        cts2.Cancel();
        await reg2;

        string sseOutput2 = Encoding.UTF8.GetString(((MemoryStream)ctx2.Response.Body).ToArray());
        Assert.Contains("event: batch_update", sseOutput2);
        Assert.Contains("\"window_update\"", sseOutput2);
    }

    public class SubscriberProxy : DispatchProxy
    {
        public int SubscribeCalls;
        public int FailCount;
        public List<string> SubscribedChannels { get; } = [];
        public List<string> UnsubscribedChannels { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISubscriber.SubscribeAsync))
            {
                int attempt = Interlocked.Increment(ref SubscribeCalls);
                if (attempt <= FailCount)
                {
                    throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated Redis startup delay");
                }
                if (args is { Length: > 0 } && args[0] is RedisChannel ch)
                {
                    lock (SubscribedChannels) SubscribedChannels.Add(ch.ToString());
                }
                return Task.CompletedTask;
            }
            if (targetMethod?.Name == nameof(ISubscriber.UnsubscribeAsync))
            {
                if (args is { Length: > 0 } && args[0] is RedisChannel ch)
                {
                    lock (UnsubscribedChannels) UnsubscribedChannels.Add(ch.ToString());
                }
                return Task.CompletedTask;
            }
            if (targetMethod?.Name == nameof(ISubscriber.UnsubscribeAllAsync))
            {
                return Task.CompletedTask;
            }
            return null;
        }
    }

    public class MultiplexerProxy : DispatchProxy
    {
        public ISubscriber Subscriber { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IConnectionMultiplexer.GetSubscriber))
            {
                return Subscriber;
            }
            return null;
        }
    }

    [Fact]
    public async Task RedisWindowSubscriber_WhenInitialSubscribeFails_RetriesUntilSubscribed()
    {
        var subProxy = DispatchProxy.Create<ISubscriber, SubscriberProxy>();
        ((SubscriberProxy)(object)subProxy).FailCount = 2;

        var muxProxy = DispatchProxy.Create<IConnectionMultiplexer, MultiplexerProxy>();
        ((MultiplexerProxy)(object)muxProxy).Subscriber = subProxy;

        var sseService = new SseService(NullLogger<SseService>.Instance);
        var dbService = new DatabaseQueryService(new ConfigurationBuilder().Build(), NullLogger<DatabaseQueryService>.Instance);

        var subscriber = new RedisWindowSubscriber(muxProxy, sseService, dbService, NullLogger<RedisWindowSubscriber>.Instance, retryDelayMs: 10);
        using var cts = new CancellationTokenSource();
        await subscriber.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref ((SubscriberProxy)(object)subProxy).SubscribeCalls) < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(Volatile.Read(ref ((SubscriberProxy)(object)subProxy).SubscribeCalls) >= 3);
        await subscriber.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RedisWindowSubscriber_DemandGatesWindowsSubscription_OnClientCountChanges()
    {
        var subProxy = DispatchProxy.Create<ISubscriber, SubscriberProxy>();
        var proxyState = (SubscriberProxy)(object)subProxy;

        var muxProxy = DispatchProxy.Create<IConnectionMultiplexer, MultiplexerProxy>();
        ((MultiplexerProxy)(object)muxProxy).Subscriber = subProxy;

        var sseService = new SseService(NullLogger<SseService>.Instance);
        var dbService = new DatabaseQueryService(new ConfigurationBuilder().Build(), NullLogger<DatabaseQueryService>.Instance);

        var subscriber = new RedisWindowSubscriber(muxProxy, sseService, dbService, NullLogger<RedisWindowSubscriber>.Instance, retryDelayMs: 10);
        using var bgCts = new CancellationTokenSource();
        await subscriber.StartAsync(bgCts.Token);

        await Task.Delay(50);
        lock (proxyState.SubscribedChannels)
        {
            Assert.Contains(Constants.RedisResetChannel, proxyState.SubscribedChannels);
            Assert.DoesNotContain(Constants.RedisWindowsChannel, proxyState.SubscribedChannels);
        }

        var ctx1 = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reg1 = sseService.RegisterClient("c1", ctx1.Response, cts1.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (proxyState.SubscribedChannels)
            {
                if (proxyState.SubscribedChannels.Contains(Constants.RedisWindowsChannel)) break;
            }
            await Task.Delay(15);
        }

        lock (proxyState.SubscribedChannels)
        {
            Assert.Contains(Constants.RedisWindowsChannel, proxyState.SubscribedChannels);
        }

        var ctx2 = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reg2 = sseService.RegisterClient("c1", ctx2.Response, cts2.Token);
        await Task.Delay(30);
        cts1.Cancel();
        await reg1;
        await Task.Delay(30);

        lock (proxyState.UnsubscribedChannels)
        {
            Assert.DoesNotContain(Constants.RedisWindowsChannel, proxyState.UnsubscribedChannels);
        }

        cts2.Cancel();
        await reg2;

        deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (proxyState.UnsubscribedChannels)
            {
                if (proxyState.UnsubscribedChannels.Contains(Constants.RedisWindowsChannel)) break;
            }
            await Task.Delay(15);
        }

        lock (proxyState.UnsubscribedChannels)
        {
            Assert.Contains(Constants.RedisWindowsChannel, proxyState.UnsubscribedChannels);
        }

        await subscriber.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HistoryController_GetAdvisories_ClampsLimitAndReturnsRecords()
    {
        var dbService = new RecordingDatabaseQueryService([])
        {
            StubAdvisories =
            [
                new AdvisoryRecord("SAP.ETR", "BUY", 125.0, 124.5, 123.0, new DateTime(2021, 11, 8, 9, 5, 0, DateTimeKind.Utc))
            ]
        };
        var controller = new HistoryController(dbService);

        var res = await controller.GetAdvisories(9999, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(res);
        var list = Assert.IsAssignableFrom<IReadOnlyList<AdvisoryRecord>>(ok.Value);
        Assert.Single(list);
        Assert.Equal(Constants.MaxAdvisoriesLimit, dbService.LastAdvisoriesLimit);
    }

    [Fact]
    public async Task DataController_GetStatus_UsesStatusSummaryAsync()
    {
        var expectedTime = new DateTime(2021, 11, 8, 9, 10, 0, DateTimeKind.Utc);
        var dbService = new RecordingDatabaseQueryService([])
        {
            StubKnownSymbols = 42,
            StubLatestTime = expectedTime
        };
        var controller = new DataController(
            dbService,
            new RecordingWorkerResetClient([]),
            new RecordingRedisResetPublisher([]),
            new SseService(NullLogger<SseService>.Instance));

        var res = await controller.GetStatus(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(res);
        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"knownSymbols\":42", json);
        Assert.Contains("2021-11-08T09:10:00Z", json);
    }

    [Fact]
    public async Task SseService_ConcurrentClientHandoff_NeverLeavesCountOutOfSync()
    {
        var sseService = new SseService(NullLogger<SseService>.Instance);
        var transitions = new List<int>();
        sseService.ActiveConnectionCountChanged += count =>
        {
            lock (transitions) transitions.Add(count);
        };

        var ctxA = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        var ctxB = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        using var ctsA = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var ctsB = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var regA = sseService.RegisterClient("client_A", ctxA.Response, ctsA.Token);
        var regB = sseService.RegisterClient("client_B", ctxB.Response, ctsB.Token);
        await Task.Delay(30);

        ctsA.Cancel();
        await regA;

        Assert.Equal(1, sseService.ActiveConnectionCount);
        lock (transitions)
        {
            Assert.NotEmpty(transitions);
            Assert.True(transitions[^1] > 0);
        }

        ctsB.Cancel();
        await regB;

        Assert.Equal(0, sseService.ActiveConnectionCount);
        lock (transitions)
        {
            Assert.Equal(0, transitions[^1]);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Fact]
    public async Task WorkerResetClient_FansOutToAllConfiguredWorkerShards()
    {
        var seen = new List<string>();
        var handler = new StubHttpMessageHandler(req =>
        {
            lock (seen) seen.Add(req.RequestUri!.ToString());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        var client = new HttpClient(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WORKER_RESET_URLS"] = "http://worker-0:8080,http://worker-1:8080,http://worker-2:8080"
            })
            .Build();

        var resetClient = new WorkerResetClient(new StubHttpClientFactory(client), config, NullLogger<WorkerResetClient>.Instance);
        var ok = await resetClient.ResetWorkerAsync();

        Assert.True(ok);
        Assert.Equal(3, seen.Count);
        Assert.Contains("http://worker-0:8080/internal/reset", seen);
        Assert.Contains("http://worker-1:8080/internal/reset", seen);
        Assert.Contains("http://worker-2:8080/internal/reset", seen);
    }

    [Fact]
    public async Task WorkerResetClient_ReturnsFalseWhenAnyShardFails()
    {
        var handler = new StubHttpMessageHandler(req =>
            req.RequestUri!.Host == "worker-1"
                ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WORKER_RESET_URLS"] = "http://worker-0:8080,http://worker-1:8080,http://worker-2:8080"
            })
            .Build();

        var resetClient = new WorkerResetClient(new StubHttpClientFactory(client), config, NullLogger<WorkerResetClient>.Instance);
        var ok = await resetClient.ResetWorkerAsync();

        Assert.False(ok);
    }

    [Fact]
    public async Task DatabaseQueryService_DiscoversDynamicMarketsAndFiltersByMarket()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TimescaleDb"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1"
            })
            .Build();
        var dbService = new DatabaseQueryService(config, NullLogger<DatabaseQueryService>.Instance);
        var now = new DateTime(2021, 11, 8, 9, 0, 0, DateTimeKind.Utc);
        dbService.RecordLiveSymbol("SAP.ETR", now);
        dbService.RecordLiveSymbol("BMW.ETR", now);
        dbService.RecordLiveSymbol("RDSA.NL", now);
        dbService.RecordLiveSymbol("NEW.XYZ", now);

        var markets = await dbService.GetMarketsAsync();
        Assert.Equal(["ETR", "NL", "XYZ"], markets);

        var etrSymbols = await dbService.GetSymbolsByMarketAsync("etr", null, 50);
        Assert.Equal(["BMW.ETR", "SAP.ETR"], etrSymbols);
    }
}
