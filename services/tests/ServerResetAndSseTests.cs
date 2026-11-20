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

        public Task<IReadOnlyList<FinalizedWindowRecord>> GetHistoryAsync(string symbol, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FinalizedWindowRecord>>([]);

        public Task<IReadOnlyList<string>> SearchSymbolsAsync(string? query, int limit, CancellationToken ct = default) =>
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
        await sseService.BroadcastEvent("reset", new { cleared = true });

        cts.Cancel();
        await registerTask;

        string sseOutput = Encoding.UTF8.GetString(bodyStream.ToArray());
        Assert.Contains("event: connected", sseOutput);
        Assert.Contains("event: active_update", sseOutput);
        Assert.Contains("event: advisory_alert", sseOutput);
        Assert.Contains("event: reset", sseOutput);
        Assert.Contains("SAP.ETR", sseOutput);
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

        cts2.Cancel();
        await reg2;

        string sseOutput2 = Encoding.UTF8.GetString(((MemoryStream)ctx2.Response.Body).ToArray());
        Assert.Contains("event: active_update", sseOutput2);
    }

    public class SubscriberProxy : DispatchProxy
    {
        public int SubscribeCalls;
        public int FailCount;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISubscriber.SubscribeAsync))
            {
                int attempt = Interlocked.Increment(ref SubscribeCalls);
                if (attempt <= FailCount)
                {
                    throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated Redis startup delay");
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
        while (Volatile.Read(ref ((SubscriberProxy)(object)subProxy).SubscribeCalls) < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }

        Assert.True(Volatile.Read(ref ((SubscriberProxy)(object)subProxy).SubscribeCalls) >= 4);
        await subscriber.StopAsync(CancellationToken.None);
    }
}
