using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Trading.Shared.Models;
using Trading.Worker.Controllers;
using Trading.Worker.Services;
using Xunit;

namespace Trading.Tests;

public class IngestControllerTests
{
    private sealed class FakeIngestionService : ITickIngestionService
    {
        public List<MinimalTradingEntry> Enqueued { get; } = [];
        public long IngestedCount => Enqueued.Count;

        public ValueTask EnqueueAsync(MinimalTradingEntry entry, CancellationToken ct = default)
        {
            Enqueued.Add(entry);
            return ValueTask.CompletedTask;
        }

        public void Reset() => Enqueued.Clear();
    }

    private sealed class NoopWarningLogger : IAsyncWarningLogger
    {
        public long DroppedCount => 0;
        public void EnqueueWarning(string message) { }
    }

    private static IngestController CreateController(FakeIngestionService ingestion, string jsonBody)
    {
        var controller = new IngestController(ingestion, new NoopWarningLogger())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Request =
                    {
                        Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody)),
                        ContentType = "application/json"
                    }
                }
            }
        };
        return controller;
    }

    [Fact]
    public async Task PostTick_AcceptsSingleObject()
    {
        var ingestion = new FakeIngestionService();
        var body = """{"ID":"RDSA.NL","SecType":"E","Last":"20.5","Trading time":"09:00:01","Trading date":"08-11-21"}""";
        var controller = CreateController(ingestion, body);

        var result = await controller.PostTick(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Single(ingestion.Enqueued);
        Assert.Equal("RDSA.NL", ingestion.Enqueued[0].Id);
    }

    [Fact]
    public async Task PostTick_AcceptsJsonArrayBatch()
    {
        var ingestion = new FakeIngestionService();
        var body = """[{"ID":"RDSA.NL","SecType":"E","Last":"20.5","Trading time":"09:00:01","Trading date":"08-11-21"},{"ID":"A0HN5C.ETR","SecType":"E","Last":"30.1","Trading time":"09:00:02","Trading date":"08-11-21"}]""";
        var controller = CreateController(ingestion, body);

        var result = await controller.PostTick(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal(2, ingestion.Enqueued.Count);
    }

    [Fact]
    public async Task PostTick_AcceptsEmptyArray()
    {
        var ingestion = new FakeIngestionService();
        var controller = CreateController(ingestion, "[]");

        var result = await controller.PostTick(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Empty(ingestion.Enqueued);
    }

    [Fact]
    public async Task PostTick_RejectsMalformedJson()
    {
        var ingestion = new FakeIngestionService();
        var controller = CreateController(ingestion, "{bad json");

        var result = await controller.PostTick(CancellationToken.None);

        Assert.IsType<BadRequestResult>(result);
        Assert.Empty(ingestion.Enqueued);
    }
}
