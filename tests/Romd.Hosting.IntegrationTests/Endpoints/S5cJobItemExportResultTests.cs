using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Queries.ExportJobItems;
using Romd.Application.Common.Systems;
using Romd.Domain.Jobs;
using Romd.Host.Endpoints;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class S5cJobItemExportResultTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task ExecuteAsync_CompleteResponse_FlushesBeforeCompletingSnapshot(string format)
    {
        var item = Item();
        var response = new ResponseFeature();
        var body = new RecordingStream(response);
        var snapshot = new RecordingSnapshot(() =>
        {
            body.FlushCount.ShouldBeGreaterThan(0);
            var output = Encoding.UTF8.GetString(body.ToArray());
            if (format == "json")
                output.ShouldEndWith("]");
            else
                output.ShouldEndWith("\n");
        });
        var context = Context(response, body);
        var export = Export(snapshot, new JobItemPageResult([item], false));

        await new JobItemExportResult(item.JobId, format, export, new SystemKeys(new Dictionary<int, string>())).ExecuteAsync(context);

        snapshot.Completed.ShouldBeTrue();
        snapshot.DisposeCount.ShouldBe(1);
        context.Response.Headers.ContentDisposition.ToString().ShouldContain($"import-{item.JobId:N}.{format}");
        var bytes = body.ToArray();
        if (format == "json")
        {
            using var json = JsonDocument.Parse(bytes);
            json.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
            var row = json.RootElement.EnumerateArray().ShouldHaveSingleItem();
            row.GetProperty("sizeBytes").ValueKind.ShouldBe(JsonValueKind.String);
            row.GetProperty("sizeBytes").GetString().ShouldBe("9007199254740993");
            row.GetProperty("fileName").GetString().ShouldBe(item.FileName);
            row.TryGetProperty("SizeBytes", out _).ShouldBeFalse();
        }
        else
        {
            Encoding.UTF8.GetString(bytes).ShouldBe(JobItemExportResult.CsvHeader + "\n" +
                "\"folder/\"\"game\"\",one\n.bin\",rom,ingested,9007199254740993,,\"One, title; Two\"\"\",12,true,\"line1\r\nline2\"\n");
        }
    }

    [Fact]
    public async Task ExecuteAsync_Json_PropagatesTransportBackpressureBeforeReadingLaterPages()
    {
        var item = Item() with { FileName = new string('x', 900) + ".bin" };
        var position = new JobItemPosition(item.CreatedAt, item.Id);
        var repository = Substitute.For<IJobItemRepository>();
        int laterReads = 0;
        repository.GetByJobAsync(item.JobId, null, JobItemExport.BatchSize, Arg.Any<CancellationToken>(), Arg.Any<JobItemPosition>())
            .Returns(_ =>
            {
                laterReads++;
                return new JobItemPageResult(Enumerable.Repeat(item, JobItemExport.BatchSize).ToList(), laterReads < 2,
                    laterReads < 2 ? position : null);
            });
        var snapshot = new RecordingSnapshot();
        var response = new ResponseFeature();
        var body = new RecordingStream(response);
        var transport = new ControlledPipeWriter(PipeWriter.Create(body, new StreamPipeWriterOptions(leaveOpen: true)), holdFirst: true);
        var context = Context(response, body);
        context.Features.Set<IHttpResponseBodyFeature>(new WriterBodyFeature(body, transport));
        var export = new JobItemExport(repository, snapshot, item.JobId, new JobItemPageResult([item], true, position));

        var writeTask = new JobItemExportResult(item.JobId, "json", export, new SystemKeys(new Dictionary<int, string>())).ExecuteAsync(context);
        await transport.FirstFlushReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int readsAtFirstTransportFlush = laterReads;
        bool completedWhileTransportBlocked = snapshot.Completed;
        transport.Resume.TrySetResult();
        await writeTask;

        readsAtFirstTransportFlush.ShouldBe(0);
        completedWhileTransportBlocked.ShouldBeFalse();
        transport.FlushCount.ShouldBeGreaterThan(2);
        transport.MaxPendingBytes.ShouldBeLessThan(64 * 1024);
        laterReads.ShouldBe(2);
        snapshot.Completed.ShouldBeTrue();
        snapshot.DisposeCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_JsonTransportCanceledOrCompleted_AbortsWithoutCompletingSnapshot(bool canceled)
    {
        var snapshot = new RecordingSnapshot();
        var response = new ResponseFeature();
        var body = new RecordingStream(response);
        var transport = new ControlledPipeWriter(PipeWriter.Create(body, new StreamPipeWriterOptions(leaveOpen: true)),
            terminalResult: new FlushResult(isCanceled: canceled, isCompleted: !canceled));
        var context = Context(response, body);
        context.Features.Set<IHttpResponseBodyFeature>(new WriterBodyFeature(body, transport));

        var failure = await Record.ExceptionAsync(() => new JobItemExportResult(Guid.NewGuid(), "json",
            Export(snapshot, new JobItemPageResult([Item()], false)), new SystemKeys(new Dictionary<int, string>())).ExecuteAsync(context));

        failure.ShouldNotBeNull();
        ((LifetimeFeature)context.Features.Get<IHttpRequestLifetimeFeature>()!).Aborted.ShouldBeTrue();
        snapshot.Completed.ShouldBeFalse();
        snapshot.DisposeCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task ExecuteAsync_EmptyExport_WritesCompleteEmptyRepresentation(string format)
    {
        var snapshot = new RecordingSnapshot();
        var response = new ResponseFeature();
        var body = new RecordingStream(response);
        var context = Context(response, body);

        await new JobItemExportResult(Guid.NewGuid(), format, Export(snapshot, new JobItemPageResult([], false)),
            new SystemKeys(new Dictionary<int, string>())).ExecuteAsync(context);

        Encoding.UTF8.GetString(body.ToArray()).ShouldBe(format == "json" ? "[]" : JobItemExportResult.CsvHeader + "\n");
        snapshot.Completed.ShouldBeTrue();
        snapshot.DisposeCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task ExecuteAsync_LaterReadFails_AbortsStartedResponseAndDisposesWithoutCompletion(string format)
    {
        var item = Item();
        var repository = Substitute.For<IJobItemRepository>();
        var position = new JobItemPosition(item.CreatedAt, item.Id);
        repository.GetByJobAsync(item.JobId, null, JobItemExport.BatchSize, Arg.Any<CancellationToken>(), position)
            .Returns<Task<JobItemPageResult>>(_ => throw new IOException("private database failure"));
        var snapshot = new RecordingSnapshot();
        var response = new ResponseFeature();
        var body = new RecordingStream(response);
        var context = Context(response, body);
        var lifetime = (LifetimeFeature)context.Features.Get<IHttpRequestLifetimeFeature>()!;
        var export = new JobItemExport(repository, snapshot, item.JobId, new JobItemPageResult([item], true, position));

        await Should.ThrowAsync<IOException>(() => new JobItemExportResult(item.JobId, format, export, new SystemKeys(new Dictionary<int, string>())).ExecuteAsync(context));

        response.HasStarted.ShouldBeTrue();
        lifetime.Aborted.ShouldBeTrue();
        snapshot.Completed.ShouldBeFalse();
        snapshot.DisposeCount.ShouldBe(1);
        Encoding.UTF8.GetString(body.ToArray()).ShouldNotContain("private database failure");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task ExecuteAsync_WriteFails_AbortsAndDisposesWithoutCompletion(string format)
    {
        var snapshot = new RecordingSnapshot();
        var response = new ResponseFeature();
        var body = new RecordingStream(response, failAfterFirstWrite: true);
        var context = Context(response, body);
        var export = Export(snapshot, new JobItemPageResult([Item(), Item()], false));

        await Should.ThrowAsync<IOException>(() => new JobItemExportResult(Guid.NewGuid(), format, export, new SystemKeys(new Dictionary<int, string>())).ExecuteAsync(context));

        ((LifetimeFeature)context.Features.Get<IHttpRequestLifetimeFeature>()!).Aborted.ShouldBeTrue();
        snapshot.Completed.ShouldBeFalse();
        snapshot.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_RequestCanceled_StopsBeforeHeadersAndDisposesSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        var snapshot = new RecordingSnapshot();
        var response = new ResponseFeature();
        var body = new RecordingStream(response);
        var context = Context(response, body);
        context.RequestAborted = cancellation.Token;
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => new JobItemExportResult(Guid.NewGuid(), "json",
            Export(snapshot, new JobItemPageResult([Item()], false)), new SystemKeys(new Dictionary<int, string>())).ExecuteAsync(context));

        response.HasStarted.ShouldBeFalse();
        body.Length.ShouldBe(0);
        snapshot.Completed.ShouldBeFalse();
        snapshot.DisposeCount.ShouldBe(1);
        context.Response.Headers.ContentDisposition.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_LaterReadFails_ClientCannotReceiveSuccessfulTruncatedBody()
    {
        var item = Item();
        var repository = Substitute.For<IJobItemRepository>();
        var position = new JobItemPosition(item.CreatedAt, item.Id);
        repository.GetByJobAsync(item.JobId, null, JobItemExport.BatchSize, Arg.Any<CancellationToken>(), position)
            .Returns<Task<JobItemPageResult>>(_ => throw new IOException("Injected later-page failure."));
        var snapshot = new RecordingSnapshot { DisposeFailure = new IOException("Secondary snapshot cleanup failure") };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRomdHttpContract();
        await using var app = builder.Build();
        app.UseRomdExceptionHandling();
        app.MapGet("/api/export", () => new JobItemExportResult(item.JobId, "json",
            new JobItemExport(repository, snapshot, item.JobId, new JobItemPageResult([item], true, position)),
            new SystemKeys(new Dictionary<int, string>())));
        await app.StartAsync();
        using var client = app.GetTestClient();

        var failure = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.GetAsync("/api/export", HttpCompletionOption.ResponseHeadersRead);
            await response.Content.ReadAsStringAsync();
        });

        failure.ShouldNotBeNull();
        snapshot.Completed.ShouldBeFalse();
        snapshot.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_FirstItemCannotResolve_ReturnsSanitizedProblemBeforeExportHeaders()
    {
        var snapshot = new RecordingSnapshot();
        var item = Item() with { PlatformId = 123 };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRomdHttpContract();
        await using var app = builder.Build();
        app.UseRomdExceptionHandling();
        app.MapGet("/api/export", () => new JobItemExportResult(item.JobId, "json",
            Export(snapshot, new JobItemPageResult([item], false)), new SystemKeys(new Dictionary<int, string>())));
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/export");

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Content.Headers.ContentDisposition.ShouldBeNull();
        var problem = await response.Content.ReadAsStringAsync();
        problem.ShouldNotContain("Unregistered system identity");
        snapshot.Completed.ShouldBeFalse();
        snapshot.DisposeCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("json", "read", false)]
    [InlineData("json", "read", true)]
    [InlineData("csv", "read", false)]
    [InlineData("csv", "read", true)]
    [InlineData("json", "complete", false)]
    [InlineData("json", "complete", true)]
    [InlineData("csv", "complete", false)]
    [InlineData("csv", "complete", true)]
    public async Task Execute_PrimaryAndSnapshotCleanupFault_AbortsAndPreservesOriginalFailure(string format, string phase, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException("Original export cancellation", cancellation.Token)
            : new IOException("Original export failure");
        var snapshot = new RecordingSnapshot
        {
            CompleteFailure = phase == "complete" ? primary : null,
            DisposeFailure = new IOException("Secondary snapshot disposal failure")
        };
        var item = Item();
        var position = new JobItemPosition(item.CreatedAt, item.Id);
        var repository = Substitute.For<IJobItemRepository>();
        repository.GetByJobAsync(item.JobId, null, JobItemExport.BatchSize, Arg.Any<CancellationToken>(), position)
            .Returns(Task.FromException<JobItemPageResult>(primary));
        var response = new ResponseFeature();
        var body = new RecordingStream(response);
        var context = Context(response, body);
        var export = new JobItemExport(repository, snapshot, item.JobId,
            new([item], phase == "read", phase == "read" ? position : null));

        var observed = await Record.ExceptionAsync(() => new JobItemExportResult(item.JobId, format, export,
            new SystemKeys(new Dictionary<int, string>())).ExecuteAsync(context));

        observed.ShouldBeSameAs(primary);
        if (cancel)
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.ReadSnapshot.CleanupFailure"].ShouldBeSameAs(snapshot.DisposeFailure);
        response.HasStarted.ShouldBeTrue();
        ((LifetimeFeature)context.Features.Get<IHttpRequestLifetimeFeature>()!).Aborted.ShouldBeTrue();
        snapshot.Completed.ShouldBeFalse();
        snapshot.DisposeCount.ShouldBe(1);
        Encoding.UTF8.GetString(body.ToArray()).ShouldNotContain(primary.Message);
        Encoding.UTF8.GetString(body.ToArray()).ShouldNotContain(snapshot.DisposeFailure.Message);
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("json", true)]
    [InlineData("csv", false)]
    [InlineData("csv", true)]
    public async Task Execute_WriteAndSnapshotCleanupFault_PreservesWriteCauseAndAborts(string format, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel ? new OperationCanceledException("Original transport cancellation", cancellation.Token)
            : new IOException("Original transport write failure");
        var snapshot = new RecordingSnapshot { DisposeFailure = new IOException("Secondary snapshot disposal failure") };
        var response = new ResponseFeature();
        var body = new RecordingStream(response, failAfterFirstWrite: true) { WriteFailure = primary };
        var context = Context(response, body);
        var export = Export(snapshot, new([Item(), Item()], false));

        var observed = await Record.ExceptionAsync(() => new JobItemExportResult(Guid.NewGuid(), format, export,
            new SystemKeys(new Dictionary<int, string>())).ExecuteAsync(context));

        observed.ShouldBeSameAs(primary);
        if (cancel)
            ((OperationCanceledException)observed!).CancellationToken.ShouldBe(cancellation.Token);
        primary.Data["Romd.ReadSnapshot.CleanupFailure"].ShouldBeSameAs(snapshot.DisposeFailure);
        ((LifetimeFeature)context.Features.Get<IHttpRequestLifetimeFeature>()!).Aborted.ShouldBeTrue();
        snapshot.Completed.ShouldBeFalse();
        snapshot.DisposeCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("json", true)]
    [InlineData("csv", false)]
    [InlineData("csv", true)]
    public async Task Execute_AcknowledgedCompleteAndCleanupFault_ReturnsFullResponseWithoutAborting(string format, bool cleanupCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        var response = new ResponseFeature();
        var body = new RecordingStream(response);
        var snapshot = new RecordingSnapshot(() =>
        {
            body.FlushCount.ShouldBeGreaterThan(0);
            Encoding.UTF8.GetString(body.ToArray()).ShouldEndWith(format == "json" ? "]" : "\n");
        })
        {
            DisposeFailure = cleanupCancellation ? new OperationCanceledException("Secondary cleanup cancellation", cancellation.Token)
                : new IOException("Secondary snapshot disposal failure")
        };
        var context = Context(response, body);

        await new JobItemExportResult(Guid.NewGuid(), format, Export(snapshot, new([Item()], false)),
            new SystemKeys(new Dictionary<int, string>())).ExecuteAsync(context);

        snapshot.Completed.ShouldBeTrue();
        snapshot.DisposeCount.ShouldBe(1);
        ((LifetimeFeature)context.Features.Get<IHttpRequestLifetimeFeature>()!).Aborted.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(200);
        if (format == "json")
        {
            using var json = JsonDocument.Parse(body.ToArray());
            json.RootElement.EnumerateArray().ShouldHaveSingleItem();
        }
        else
            Encoding.UTF8.GetString(body.ToArray()).ShouldStartWith(JobItemExportResult.CsvHeader + "\n");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task Execute_AcknowledgedCompletionAndCleanupFault_MappedResponseRemainsComplete(string format)
    {
        var snapshot = new RecordingSnapshot { DisposeFailure = new IOException("Secondary acknowledged snapshot cleanup") };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRomdHttpContract();
        await using var app = builder.Build();
        app.UseRomdExceptionHandling();
        app.MapGet("/api/export", () => new JobItemExportResult(Guid.NewGuid(), format,
            Export(snapshot, new([Item()], false)), new SystemKeys(new Dictionary<int, string>())));
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/export");

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        if (format == "json")
        {
            using var json = JsonDocument.Parse(body);
            json.RootElement.EnumerateArray().ShouldHaveSingleItem();
        }
        else
            body.ShouldStartWith(JobItemExportResult.CsvHeader + "\n");
        body.ShouldNotContain(snapshot.DisposeFailure.Message);
        snapshot.Completed.ShouldBeTrue();
        snapshot.DisposeCount.ShouldBe(1);
    }

    private static JobItemExport Export(RecordingSnapshot snapshot, JobItemPageResult page) =>
        new(Substitute.For<IJobItemRepository>(), snapshot, page.Items.FirstOrDefault()?.JobId ?? Guid.NewGuid(), page);

    private static JobItemView Item() => new()
    {
        Id = Guid.NewGuid(),
        JobId = Guid.NewGuid(),
        Kind = JobItemKind.Rom,
        FileName = "folder/\"game\",one\n.bin",
        SizeBytes = 9_007_199_254_740_993,
        Outcome = JobItemOutcome.Ingested,
        MatchedTitles = [new JobItemTitle(1, "One, title"), new JobItemTitle(2, "Two\"")],
        GameCount = 12,
        ArchiveOnly = true,
        Error = "line1\r\nline2",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static DefaultHttpContext Context(ResponseFeature response, RecordingStream body)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(response);
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        context.Features.Set<IHttpRequestLifetimeFeature>(new LifetimeFeature());
        context.RequestServices = new ServiceCollection().BuildServiceProvider();
        return context;
    }

    private sealed class RecordingSnapshot(Action? onComplete = null) : IReadSnapshotTransaction
    {
        public bool Completed { get; private set; }
        public int DisposeCount { get; private set; }
        public Exception? CompleteFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            onComplete?.Invoke();
            if (CompleteFailure is not null)
                throw CompleteFailure;
            Completed = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeFailure);
        }
    }

    private sealed class LifetimeFeature : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        public bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }

    private sealed class ResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; set; }
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    private sealed class WriterBodyFeature(Stream stream, PipeWriter writer) : IHttpResponseBodyFeature
    {
        public Stream Stream { get; } = stream;
        public PipeWriter Writer { get; } = writer;
        public void DisableBuffering() { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteAsync() => Task.CompletedTask;
    }

    private sealed class ControlledPipeWriter(PipeWriter inner, bool holdFirst = false, FlushResult? terminalResult = null) : PipeWriter
    {
        private int _pendingBytes;
        public int FlushCount { get; private set; }
        public int MaxPendingBytes { get; private set; }
        public TaskCompletionSource FirstFlushReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Advance(int bytes)
        {
            _pendingBytes += bytes;
            MaxPendingBytes = Math.Max(MaxPendingBytes, _pendingBytes);
            inner.Advance(bytes);
        }
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushCount++;
            if (FlushCount == 1)
            {
                FirstFlushReached.TrySetResult();
                if (holdFirst)
                    await Resume.Task.WaitAsync(cancellationToken);
            }
            var result = await inner.FlushAsync(cancellationToken);
            _pendingBytes = 0;
            return terminalResult ?? result;
        }
    }

    private sealed class RecordingStream(ResponseFeature response, bool failAfterFirstWrite = false) : MemoryStream
    {
        private int _writeCount;
        public int FlushCount { get; private set; }
        public Exception? WriteFailure { get; init; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            response.HasStarted = true;
            if (failAfterFirstWrite && ++_writeCount > 1)
                throw WriteFailure ?? new IOException("Injected response write failure.");
            return base.WriteAsync(buffer, cancellationToken);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return base.FlushAsync(cancellationToken);
        }
    }
}
