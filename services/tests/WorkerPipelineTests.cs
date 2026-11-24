using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Trading.Shared.Models;
using Trading.Shared.Services;
using Trading.Worker.Controllers;
using Trading.Worker.Services;

namespace Trading.Tests;

public class WorkerPipelineTests
{
    private class FakeWarningLogger : IAsyncWarningLogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public long DroppedCount => 0;
        public void EnqueueWarning(string message) => Warnings.Enqueue(message);
    }

    private class FakeIngestionService : ITickIngestionService
    {
        public ConcurrentQueue<MinimalTradingEntry> Enqueued { get; } = new();
        public bool ResetCalled { get; private set; }
        public long IngestedCount => Enqueued.Count;

        public ValueTask EnqueueAsync(MinimalTradingEntry tick, CancellationToken ct = default)
        {
            Enqueued.Enqueue(tick);
            return ValueTask.CompletedTask;
        }

        public Task<int> FlushAsync(CancellationToken ct = default) => Task.FromResult(0);

        public void Reset() => ResetCalled = true;
    }

    private class FakeDbBatchWriter : IDbBatchWriter
    {
        public ConcurrentQueue<FinalizedWindowRecord> Enqueued { get; } = new();
        public bool ClearBufferCalled { get; private set; }

        public void Enqueue(FinalizedWindowRecord record) => Enqueued.Enqueue(record);
        public void ClearBuffer() => ClearBufferCalled = true;
    }

    private class FakeWindowPublisher : IWindowPublisher
    {
        public ConcurrentQueue<FinalizedWindowRecord> Published { get; } = new();

        public Task PublishWindowAsync(FinalizedWindowRecord record, CancellationToken ct = default)
        {
            Published.Enqueue(record);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task IngestController_PostMalformedJson_ReturnsBadRequestAndLogsWarningWithoutThrowing()
    {
        var warningLogger = new FakeWarningLogger();
        var ingestionService = new FakeIngestionService();
        var controller = new IngestController(ingestionService, warningLogger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request =
                    {
                        Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"ID\": \"SAP.ETR\", \"Last\": "))
                    }
                }
            }
        };

        var result = await controller.PostTick(CancellationToken.None);

        Assert.IsType<BadRequestResult>(result);
        Assert.NotEmpty(warningLogger.Warnings);
        Assert.Empty(ingestionService.Enqueued);
    }

    [Fact]
    public async Task IngestController_PostQuoteRow_ReturnsAcceptedWithoutEnqueuing()
    {
        var warningLogger = new FakeWarningLogger();
        var ingestionService = new FakeIngestionService();
        var controller = new IngestController(ingestionService, warningLogger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request =
                    {
                        Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"ID\":\"SAP.ETR\",\"Last\":\"\"}"))
                    }
                }
            }
        };

        var result = await controller.PostTick(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Empty(ingestionService.Enqueued);
        Assert.Empty(warningLogger.Warnings);
    }

    [Fact]
    public async Task IngestController_PostValidTrade_EnqueuesTickAndReturnsAccepted()
    {
        var warningLogger = new FakeWarningLogger();
        var ingestionService = new FakeIngestionService();
        var payload = """{"ID":"SAP.ETR","Last":"123.45","Trading date":"08-11-2021","Trading time":"09:05:10.000"}""";
        var controller = new IngestController(ingestionService, warningLogger)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request =
                    {
                        Body = new MemoryStream(Encoding.UTF8.GetBytes(payload))
                    }
                }
            }
        };

        var result = await controller.PostTick(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Single(ingestionService.Enqueued);
        Assert.True(ingestionService.Enqueued.TryDequeue(out var tick));
        Assert.Equal("SAP.ETR", tick.Id);
        Assert.Equal(123.45m, tick.Last);
    }

    [Fact]
    public async Task TickIngestionService_ProcessAndReset_PublishesWindowEnqueuesDbAndResetsEmaState()
    {
        var processor = new WindowProcessor();
        var dbWriter = new FakeDbBatchWriter();
        var publisher = new FakeWindowPublisher();
        var service = new TickIngestionService(processor, dbWriter, publisher, NullLogger<TickIngestionService>.Instance);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        try
        {
            var date = new DateOnly(2021, 11, 8);
            await service.EnqueueAsync(new MinimalTradingEntry { Id = "S1", Last = 100m, TradingDate = date, TradingTime = new TimeOnly(10, 0, 1) });
            await service.EnqueueAsync(new MinimalTradingEntry { Id = "S1", Last = 110m, TradingDate = date, TradingTime = new TimeOnly(10, 5, 1) });

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while ((dbWriter.Enqueued.IsEmpty || publisher.Published.IsEmpty) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(15);
            }

            Assert.Single(dbWriter.Enqueued);
            Assert.Single(publisher.Published);
            Assert.True(publisher.Published.TryDequeue(out var firstWin));
            Assert.Equal(100.0, firstWin.LastPrice, 6);
            Assert.Equal(EmaCalculator.CalculateShortEma(100.0, 0.0), firstWin.Ema38, 6);

            service.Reset();
            Assert.True(dbWriter.ClearBufferCalled);
            Assert.Equal(0, service.IngestedCount);

            await service.EnqueueAsync(new MinimalTradingEntry { Id = "S1", Last = 50m, TradingDate = date, TradingTime = new TimeOnly(10, 0, 1) });
            await service.EnqueueAsync(new MinimalTradingEntry { Id = "S1", Last = 60m, TradingDate = date, TradingTime = new TimeOnly(10, 5, 1) });

            deadline = DateTime.UtcNow.AddSeconds(2);
            while (publisher.Published.IsEmpty && DateTime.UtcNow < deadline)
            {
                await Task.Delay(15);
            }

            Assert.True(publisher.Published.TryDequeue(out var resetWin));
            Assert.Equal(50.0, resetWin.LastPrice, 6);
            Assert.Equal(EmaCalculator.CalculateShortEma(50.0, 0.0), resetWin.Ema38, 6);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private class SlowPublishTracker : IWindowPublisher
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ObservedToken { get; private set; }

        public async Task PublishWindowAsync(FinalizedWindowRecord record, CancellationToken ct = default)
        {
            ObservedToken = ct;
            Entered.TrySetResult();
            await Task.Delay(5000, ct);
        }
    }

    [Fact]
    public async Task TickIngestionService_ResetDuringSlowPublish_CancelsInFlightPublish()
    {
        var processor = new WindowProcessor();
        var dbWriter = new FakeDbBatchWriter();
        var publisher = new SlowPublishTracker();
        var service = new TickIngestionService(processor, dbWriter, publisher, NullLogger<TickIngestionService>.Instance);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        try
        {
            var date = new DateOnly(2021, 11, 8);
            await service.EnqueueAsync(new MinimalTradingEntry { Id = "S1", Last = 100m, TradingDate = date, TradingTime = new TimeOnly(10, 0, 1) });
            await service.EnqueueAsync(new MinimalTradingEntry { Id = "S1", Last = 110m, TradingDate = date, TradingTime = new TimeOnly(10, 5, 1) });

            await publisher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            service.Reset();

            Assert.True(publisher.ObservedToken.IsCancellationRequested);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
