using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Npgsql;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Commands.AcceptUpload;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class MultipartUploadAcceptanceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 5, 1, 2, 3, TimeSpan.FromHours(3)).AddTicks(1234567);
    private static readonly byte[] Bytes = [1, 2, 3];
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _root = Directory.CreateTempSubdirectory("romd-multipart-owner-").FullName;

    [Theory]
    [InlineData(UploadKind.Generic)]
    [InlineData(UploadKind.Dat)]
    [InlineData(UploadKind.Rom)]
    public async Task Handle_ActualOwnerCopiesBeforeBeginAndAtomicallyCommitsJobDispatchUnderWorkerLease(UploadKind kind)
    {
        await using var provider = Services("success", kind).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<ObservedWork>();
        var input = new Input();

        var result = await Handler(scope).HandleAsync(new(kind, input));

        await AssertAcceptedAsync(result.Value);
        owner.Flushed.ShouldBeTrue();
        owner.DisposedUnderLease.ShouldBeTrue();
        (await File.ReadAllBytesAsync(InputPath(result.Value))).ShouldBe(Bytes);
        input.Streams.Count.ShouldBe(kind == UploadKind.Generic ? 1 : 2);
        input.Streams.ShouldAllBe(stream => !stream.CanRead);
        using var worker = JobWorkspace.TryAcquireExecutionLock(_root, result.Value).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("cancel")]
    [InlineData("final-sql-fault")]
    public async Task Handle_FailureAfterActualNonemptySqlRollsBackRowsAndRetainsCompletedInput(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        await using var provider = Services(mode, cancellation: cancellation).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<ObservedWork>();

        if (mode == "cancel")
            (await Should.ThrowAsync<OperationCanceledException>(() => Handler(scope).HandleAsync(
                new(UploadKind.Generic, new Input()), cancellation.Token))).CancellationToken.ShouldBe(cancellation.Token);
        else if (mode == "final-sql-fault")
            (await Should.ThrowAsync<DbUpdateException>(() => Handler(scope).HandleAsync(new(UploadKind.Generic, new Input()))))
                .InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        else
            (await Should.ThrowAsync<IOException>(() => Handler(scope).HandleAsync(new(UploadKind.Generic, new Input())))).ShouldBeSameAs(owner.Fault);

        owner.Flushed.ShouldBeTrue();
        owner.DisposedUnderLease.ShouldBeTrue();
        scope.ServiceProvider.GetRequiredService<RomdDbContext>().ChangeTracker.Entries().ShouldBeEmpty();
        await AssertEmptyAsync();
        var id = owner.JobId.ShouldNotBeNull();
        (await File.ReadAllBytesAsync(InputPath(id))).ShouldBe(Bytes);
        using var collector = JobWorkspace.TryAcquireExecutionLock(_root, id).ShouldNotBeNull();
    }

    [Fact]
    public async Task Handle_ActualCommitThenLostAcknowledgementPreservesExceptionAndAcceptedInput()
    {
        await using var provider = Services("ack-fault").BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var owner = scope.ServiceProvider.GetRequiredService<ObservedWork>();

        (await Should.ThrowAsync<IOException>(() => Handler(scope).HandleAsync(new(UploadKind.Generic, new Input())))).ShouldBeSameAs(owner.Fault);

        var id = owner.JobId.ShouldNotBeNull();
        await AssertAcceptedAsync(id);
        (await File.ReadAllBytesAsync(InputPath(id))).ShouldBe(Bytes);
        owner.DisposedUnderLease.ShouldBeTrue();
    }

    [Theory]
    [InlineData("cleanup-fault")]
    [InlineData("cancel-after-commit")]
    public async Task Handle_AcknowledgedActualCommitSurvivesLaterCleanupOrCancellation(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        await using var provider = Services(mode, cancellation: cancellation).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await Handler(scope).HandleAsync(new(UploadKind.Generic, new Input()), cancellation.Token);

        await AssertAcceptedAsync(result.Value);
        (await File.ReadAllBytesAsync(InputPath(result.Value))).ShouldBe(Bytes);
        scope.ServiceProvider.GetRequiredService<ObservedWork>().DisposedUnderLease.ShouldBeTrue();
        using var worker = JobWorkspace.TryAcquireExecutionLock(_root, result.Value).ShouldNotBeNull();
    }

    [Fact]
    public async Task Handle_DurableReplayDoesNotReadNewInputOrOverwriteAcceptedWorkerContents()
    {
        await using var provider = Services("success").BuildServiceProvider();
        var request = Guid.NewGuid();
        var batch = Guid.NewGuid();
        await using (var first = provider.CreateAsyncScope())
            (await Handler(first).HandleAsync(new(UploadKind.Generic, new Input(), RequestId: request, BatchId: batch))).Value.ShouldBe(request);
        File.Delete(InputPath(request));
        string extracted = Path.Combine(Path.GetDirectoryName(InputPath(request))!, "worker.rom");
        await File.WriteAllTextAsync(extracted, "accepted worker bytes");
        await using var replay = provider.CreateAsyncScope();
        var source = new Input(failOnRead: true);

        var result = await Handler(replay).HandleAsync(new(UploadKind.Generic, source, RequestId: request, BatchId: batch));

        result.Value.ShouldBe(request);
        (await File.ReadAllTextAsync(extracted)).ShouldBe("accepted worker bytes");
        File.Exists(InputPath(request)).ShouldBeFalse();
        replay.ServiceProvider.GetRequiredService<ObservedWork>().Flushed.ShouldBeFalse();
        await AssertAcceptedAsync(request);
    }

    [Fact]
    public async Task Handle_ConcurrentSameIdentityRechecksAfterPhysicalLeaseAndPublishesOnlyOneJob()
    {
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        var services = Services("success");
        services.AddScoped<IJobRepository>(provider =>
        {
            var inner = new JobRepository(provider.GetRequiredService<RomdDbContext>(), new ManualTimeProvider(Now));
            var reader = Substitute.For<IJobRepository>();
            int reads = 0;
            reader.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                var found = await inner.GetByIdAsync(call.Arg<Guid>(), call.Arg<CancellationToken>());
                if (++reads == 1)
                {
                    if (Interlocked.Increment(ref arrivals) == 2)
                        bothRead.TrySetResult();
                    await bothRead.Task;
                }
                return found;
            });
            return reader;
        });
        await using var provider = services.BuildServiceProvider();
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var id = Guid.NewGuid();
        var batch = Guid.NewGuid();
        var one = Handler(first).HandleAsync(new(UploadKind.Generic, new Input(), RequestId: id, BatchId: batch));
        var two = Handler(second).HandleAsync(new(UploadKind.Generic, new Input(), RequestId: id, BatchId: batch));
        ErrorOr<Guid>[] results;
        try
        {
            await bothRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            results = await Task.WhenAll(one, two);
        }
        finally
        {
            bothRead.TrySetResult();
            await Task.WhenAll(one, two);
        }

        results.ShouldAllBe(result => !result.IsError && result.Value == id);
        await AssertAcceptedAsync(id);
        (await File.ReadAllBytesAsync(InputPath(id))).ShouldBe(Bytes);
        new[] { first, second }.Count(scope => scope.ServiceProvider.GetRequiredService<ObservedWork>().Flushed).ShouldBe(1);
    }

    private ServiceCollection Services(string mode, UploadKind kind = UploadKind.Generic, CancellationTokenSource? cancellation = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRomdTestingPersistence(_database.ConnectionString);
        services.AddRomdAdminApplicationHandlers();
        services.AddSingleton<TimeProvider>(new ManualTimeProvider(Now));
        services.AddSingleton(Substitute.For<ICurrentUser>());
        services.AddSingleton(Substitute.For<ITrackedTitleReader>());
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        services.AddSingleton(options);
        var classifier = Substitute.For<IFileClassifier>();
        classifier.ClassifyAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FileClassification(kind == UploadKind.Dat ? FileType.Dat : FileType.Rom, null, 1));
        services.AddSingleton(classifier);
        services.AddScoped<IJobWorkspaceExclusion, JobWorkspaceExclusion>();
        services.AddScoped(provider => new ObservedWorkspace(new UploadWorkspacePreparation(options),
            provider.GetRequiredService<RomdDbContext>(), _root));
        services.AddScoped<IUploadWorkspacePreparation>(provider => provider.GetRequiredService<ObservedWorkspace>());
        services.AddScoped(provider => new ObservedWork(new EfUnitOfWork(provider.GetRequiredService<RomdDbContext>()),
            provider.GetRequiredService<RomdDbContext>(), provider.GetRequiredService<ObservedWorkspace>(), this, mode, cancellation));
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<ObservedWork>());
        return services;
    }

    private static ICommandHandler<AcceptUploadCommand, Guid> Handler(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ICommandHandler<AcceptUploadCommand, Guid>>();
    private string InputPath(Guid id) => Path.Combine(_root, "temp", "jobs", id.ToString("N"), "pack.zip");
    private async Task AssertEmptyAsync()
    {
        await using var read = _database.CreateContext();
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
    }
    private async Task AssertAcceptedAsync(Guid id)
    {
        await using var read = _database.CreateContext();
        var row = await read.Jobs.OfType<UploadJobEntity>().SingleAsync();
        row.Id.ShouldBe(id);
        row.SourceFilename.ShouldBe("pack.zip");
        row.CreatedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds()));
        row.Phase.ShouldBe("Pending");
        var dispatch = await read.JobDispatches.SingleAsync();
        dispatch.JobId.ShouldBe(id);
        dispatch.JobType.ShouldBe("upload");
        dispatch.CreatedAtUtc.ShouldBe(row.CreatedAt);
    }
    public void Dispose() { _database.Dispose(); Directory.Delete(_root, recursive: true); }

    private sealed class ObservedWorkspace(IUploadWorkspacePreparation inner, RomdDbContext db, string root) : IUploadWorkspacePreparation
    {
        public Guid? JobId { get; private set; }
        public bool Copied { get; private set; }
        public async Task<ErrorOr<IPreparedJobWorkspace>> PrepareAsync(JobWorkspaceInput input, Stream source,
            IJobWorkspaceLease lease, CancellationToken ct = default)
        {
            JobId = input.JobId;
            db.Database.CurrentTransaction.ShouldBeNull();
            using var unexpectedWorker = JobWorkspace.TryAcquireExecutionLock(root, input.JobId);
            unexpectedWorker.ShouldBeNull();
            var result = await inner.PrepareAsync(input, source, lease, ct);
            Copied = !result.IsError;
            return result;
        }
    }
    private sealed class ObservedWork(IUnitOfWork inner, RomdDbContext db, ObservedWorkspace workspace,
        MultipartUploadAcceptanceTests fixture, string mode, CancellationTokenSource? cancellation) : IUnitOfWork
    {
        public IOException Fault { get; } = new("after actual upload acceptance SQL");
        public Guid? JobId => workspace.JobId;
        public bool Flushed { get; private set; }
        public bool DisposedUnderLease { get; private set; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            workspace.Copied.ShouldBeTrue();
            (await File.ReadAllBytesAsync(fixture.InputPath(JobId.ShouldNotBeNull()), cancellationToken)).ShouldBe(Bytes);
            return new Transaction(await inner.BeginTransactionAsync(cancellationToken), this);
        }
        private async Task CommitAsync(ITransaction transaction, CancellationToken ct)
        {
            await inner.FlushAsync(ct);
            Flushed = true;
            (await db.Jobs.CountAsync(ct)).ShouldBe(1);
            (await db.JobDispatches.CountAsync(ct)).ShouldBe(1);
            await fixture.AssertEmptyAsync();
            using var unexpectedWorker = JobWorkspace.TryAcquireExecutionLock(fixture._root, JobId.ShouldNotBeNull());
            unexpectedWorker.ShouldBeNull();
            if (mode == "fault")
                throw Fault;
            if (mode == "cancel")
            { cancellation.ShouldNotBeNull().Cancel(); ct.ThrowIfCancellationRequested(); }
            if (mode == "final-sql-fault")
                db.Titles.Add(new TitleEntity { PlatformId = int.MaxValue, Name = "Invalid FK", NormalizedName = "invalid-fk", EnrichmentStatus = "None", CreatedAt = Now });
            await transaction.CommitAsync(ct);
            if (mode == "cancel-after-commit")
                cancellation.ShouldNotBeNull().Cancel();
            if (mode == "ack-fault")
                throw Fault;
        }
        private async ValueTask ReleaseAsync(ITransaction transaction)
        {
            await transaction.DisposeAsync();
            using var unexpectedWorker = JobWorkspace.TryAcquireExecutionLock(fixture._root, JobId.ShouldNotBeNull());
            unexpectedWorker.ShouldBeNull();
            DisposedUnderLease = true;
            if (mode == "cleanup-fault")
                throw new IOException("after actual transaction disposal");
        }
        private sealed class Transaction(ITransaction innerTransaction, ObservedWork owner) : ITransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => owner.CommitAsync(innerTransaction, cancellationToken);
            public Task RollbackAsync(CancellationToken cancellationToken = default) => innerTransaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => owner.ReleaseAsync(innerTransaction);
        }
    }
    private sealed class Input(bool failOnRead = false) : IUploadInput
    {
        public string FileName => "pack.zip";
        public long Length => Bytes.Length;
        public List<Stream> Streams { get; } = [];
        public Stream OpenRead()
        {
            Stream stream = failOnRead ? new FailingStream() : new MemoryStream(Bytes, writable: false);
            Streams.Add(stream);
            return stream;
        }
    }
    private sealed class FailingStream : MemoryStream
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct) =>
            throw new InvalidOperationException("Replay must not read the candidate.");
    }
}
