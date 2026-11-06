using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ApplyReviewedDatReplacement;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Dat.Parsing;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Dats;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class ReviewedDatReplacementAcceptanceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 4, 12, 11, 10, TimeSpan.FromHours(3)).AddTicks(1234567);
    private static readonly DateTimeOffset PersistedNow = DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds());
    private static readonly byte[] Original = Encoding.UTF8.GetBytes(Document("Old"));
    private static readonly byte[] Candidate = Encoding.UTF8.GetBytes(Document("Reviewed"));
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly string _root = Directory.CreateTempSubdirectory("romd-reviewed-replacement-").FullName;
    private static string Document(string entry) => $"<datafile><header><name>Catalog</name></header><game name='{entry}'><rom name='track.bin' size='123' crc='11111111'/></game></datafile>";

    [Fact]
    public async Task Handle_ComposedOwner_ExactBytesPrecedeBeginAndJobDispatchCommitBeforeWorkerLeaseRelease()
    {
        await using var provider = Services("success").BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<ApplyReviewedDatReplacementCommand, Guid>>();
        var work = scope.ServiceProvider.GetRequiredService<ObservedWork>();
        using var input = new MemoryStream(Candidate, writable: false);

        var result = await handler.HandleAsync(Command(input));

        result.IsError.ShouldBeFalse();
        input.CanRead.ShouldBeTrue();
        work.BeginCount.ShouldBe(1);
        scope.ServiceProvider.GetRequiredService<ObservedWorkspace>().PreparedUnderLease.ShouldBeTrue();
        work.Flushed.ShouldBeTrue();
        work.DisposedUnderLease.ShouldBeTrue();
        await AssertAcceptedAsync(result.Value);
        (await File.ReadAllBytesAsync(InputPath(result.Value))).ShouldBe(Candidate);
        using var worker = JobWorkspace.TryAcquireExecutionLock(_root, result.Value).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("cancel")]
    [InlineData("final-sql-fault")]
    public async Task Handle_AfterActualJobAndDispatchFlush_RejectsWithoutDurableRowsButRetainsReviewedInput(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        await using var provider = Services(mode, cancellation).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var work = scope.ServiceProvider.GetRequiredService<ObservedWork>();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<ApplyReviewedDatReplacementCommand, Guid>>();
        using var input = new MemoryStream(Candidate, writable: false);

        if (mode == "cancel")
            (await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(Command(input), cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else if (mode == "final-sql-fault")
            (await Should.ThrowAsync<DbUpdateException>(() => handler.HandleAsync(Command(input))))
                .InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        else
            (await Should.ThrowAsync<IOException>(() => handler.HandleAsync(Command(input)))).ShouldBeSameAs(work.Fault);

        work.Flushed.ShouldBeTrue();
        work.DisposedUnderLease.ShouldBeTrue();
        scope.ServiceProvider.GetRequiredService<RomdDbContext>().ChangeTracker.Entries().ShouldBeEmpty();
        await AssertEmptyAsync();
        var id = work.JobId.ShouldNotBeNull();
        (await File.ReadAllBytesAsync(InputPath(id))).ShouldBe(Candidate);
        using var collector = JobWorkspace.TryAcquireExecutionLock(_root, id).ShouldNotBeNull();
    }

    [Fact]
    public async Task Handle_DurableCommitThenLostAcknowledgement_PreservesExceptionAndAcceptedInputsRatherThanDeletingOrInventingSuccess()
    {
        await using var provider = Services("ack-fault").BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var work = scope.ServiceProvider.GetRequiredService<ObservedWork>();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<ApplyReviewedDatReplacementCommand, Guid>>();
        using var input = new MemoryStream(Candidate, writable: false);

        (await Should.ThrowAsync<IOException>(() => handler.HandleAsync(Command(input)))).ShouldBeSameAs(work.Fault);

        var id = work.JobId.ShouldNotBeNull();
        await AssertAcceptedAsync(id);
        (await File.ReadAllBytesAsync(InputPath(id))).ShouldBe(Candidate);
        work.DisposedUnderLease.ShouldBeTrue();
        using var worker = JobWorkspace.TryAcquireExecutionLock(_root, id).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("cleanup-fault")]
    [InlineData("cancel-after-commit")]
    public async Task Handle_ActualCommitThenCleanupFaultOrCancellation_ReturnsDurableJobWithInputIntact(string mode)
    {
        using var cancellation = new CancellationTokenSource();
        await using var provider = Services(mode, cancellation).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var work = scope.ServiceProvider.GetRequiredService<ObservedWork>();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<ApplyReviewedDatReplacementCommand, Guid>>();
        using var input = new MemoryStream(Candidate, writable: false);

        var result = await handler.HandleAsync(Command(input), cancellation.Token);

        await AssertAcceptedAsync(result.Value);
        work.DisposedUnderLease.ShouldBeTrue();
        (await File.ReadAllBytesAsync(InputPath(result.Value))).ShouldBe(Candidate);
        using var worker = JobWorkspace.TryAcquireExecutionLock(_root, result.Value).ShouldNotBeNull();
    }

    [Fact]
    public async Task Handle_CopyFaultBeforeBegin_RetainsPartialInputAndOriginalCauseWithoutDatabaseMutation()
    {
        var review = Substitute.For<IReviewedDatReplacementPreparation>();
        var failure = new IOException("reviewed byte copy failed");
        review.PrepareAsync(Arg.Any<int>(), Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FailingDocument(failure));
        await using var provider = Services("success", preparation: review).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var work = scope.ServiceProvider.GetRequiredService<ObservedWork>();
        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<ApplyReviewedDatReplacementCommand, Guid>>();
        using var input = new MemoryStream(Candidate, writable: false);

        (await Should.ThrowAsync<IOException>(() => handler.HandleAsync(Command(input)))).ShouldBeSameAs(failure);

        work.BeginCount.ShouldBe(0);
        await AssertEmptyAsync();
        var copied = scope.ServiceProvider.GetRequiredService<ObservedWorkspace>().JobId.ShouldNotBeNull();
        (await File.ReadAllBytesAsync(InputPath(copied))).ShouldBe(new byte[] { 1, 2 });
        input.CanRead.ShouldBeTrue();
        using var collector = JobWorkspace.TryAcquireExecutionLock(_root, copied).ShouldNotBeNull();
    }

    private ServiceCollection Services(string mode, CancellationTokenSource? cancellation = null,
        IReviewedDatReplacementPreparation? preparation = null)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new ManualTimeProvider(Now));
        services.AddRomdTestingPersistence(_database.ConnectionString);
        services.AddRomdAdminApplicationHandlers();
        services.AddSingleton(options);
        services.AddSingleton(preparation ?? Review());
        services.AddScoped<IJobWorkspaceExclusion, JobWorkspaceExclusion>();
        services.AddScoped(provider => new ObservedWorkspace(new ReplaceDatWorkspacePreparation(options),
            provider.GetRequiredService<RomdDbContext>(), _root));
        services.AddScoped<IReplaceDatWorkspacePreparation>(provider => provider.GetRequiredService<ObservedWorkspace>());
        services.AddScoped(provider => new ObservedWork(new EfUnitOfWork(provider.GetRequiredService<RomdDbContext>()),
            provider.GetRequiredService<RomdDbContext>(), provider.GetRequiredService<ObservedWorkspace>(), this, mode, cancellation));
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<ObservedWork>());
        return services;
    }

    private static IReviewedDatReplacementPreparation Review()
    {
        var dat = DatFile.Rehydrate(7, "Catalog", "Catalog", null, null, null, DatType.Redump, 42,
            "active.dat", 11, Now, null, 1, 1, 0, 13, DatFileLifecycle.Active, null);
        var repository = Substitute.For<IDatRepository>();
        repository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(dat);
        repository.GetActiveBySourceIdAsync(13, Arg.Any<CancellationToken>()).Returns(dat);
        var storage = Substitute.For<IFileStorageService>();
        storage.RetrieveByIdAsync(11, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(Original, writable: false));
        var parser = new DatParser([new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance)]);
        return new DatReplacementReview(repository, storage, parser, parser);
    }

    private static ApplyReviewedDatReplacementCommand Command(Stream input) => new(7, input,
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Original)),
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Candidate)));

    private string InputPath(Guid id) => Path.Combine(_root, "temp", "jobs", id.ToString("N"), "reviewed-catalog.dat");

    private async Task AssertEmptyAsync()
    {
        await using var read = _database.CreateContext();
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
    }

    private async Task AssertAcceptedAsync(Guid id)
    {
        await using var read = _database.CreateContext();
        var job = await read.Jobs.OfType<ReplaceDatJobEntity>().SingleAsync();
        job.Id.ShouldBe(id);
        job.ExistingDatId.ShouldBe(7);
        job.SourceFilename.ShouldBe("reviewed-catalog.dat");
        job.PlatformId.ShouldBeNull();
        job.CreatedByUserId.ShouldBeNull();
        job.Phase.ShouldBe("Pending");
        job.CreatedAt.ShouldBe(PersistedNow);
        var dispatch = await read.JobDispatches.SingleAsync();
        dispatch.JobId.ShouldBe(id);
        dispatch.JobType.ShouldBe("replace_dat");
        dispatch.CreatedAtUtc.ShouldBe(PersistedNow);
        dispatch.AvailableAtUtc.ShouldBe(PersistedNow);
    }

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class ObservedWorkspace(IReplaceDatWorkspacePreparation inner, RomdDbContext db, string root) : IReplaceDatWorkspacePreparation
    {
        public Guid? JobId { get; private set; }
        public bool Copied { get; private set; }
        public bool PreparedUnderLease { get; private set; }
        public async Task<IPreparedJobWorkspace> PrepareAsync(JobWorkspaceInput input, Stream source, IJobWorkspaceLease lease, CancellationToken ct = default)
        {
            JobId = input.JobId;
            db.Database.CurrentTransaction.ShouldBeNull();
            using var unexpectedWorker = JobWorkspace.TryAcquireExecutionLock(root, input.JobId);
            unexpectedWorker.ShouldBeNull();
            PreparedUnderLease = true;
            var result = await inner.PrepareAsync(input, source, lease, ct);
            Copied = true;
            return result;
        }
    }

    private sealed class ObservedWork(IUnitOfWork inner, RomdDbContext db, ObservedWorkspace workspace,
        ReviewedDatReplacementAcceptanceTests fixture, string mode, CancellationTokenSource? cancellation) : IUnitOfWork
    {
        public IOException Fault { get; } = new("failure after actual acceptance SQL");
        public int BeginCount { get; private set; }
        public Guid? JobId => workspace.JobId;
        public bool Flushed { get; private set; }
        public bool DisposedUnderLease { get; private set; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            workspace.Copied.ShouldBeTrue();
            (await File.ReadAllBytesAsync(fixture.InputPath(JobId.ShouldNotBeNull()), cancellationToken)).ShouldBe(Candidate);
            BeginCount++;
            return new Transaction(await inner.BeginTransactionAsync(cancellationToken), this);
        }

        private async Task InspectAndCommitAsync(ITransaction transaction, CancellationToken ct)
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
            {
                cancellation.ShouldNotBeNull().Cancel();
                ct.ThrowIfCancellationRequested();
            }
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
                throw new IOException("after actual transaction cleanup");
        }

        private sealed class Transaction(ITransaction innerTransaction, ObservedWork owner) : ITransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => owner.InspectAndCommitAsync(innerTransaction, cancellationToken);
            public Task RollbackAsync(CancellationToken cancellationToken = default) => innerTransaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => owner.ReleaseAsync(innerTransaction);
        }
    }

    private sealed class FailingDocument(IOException failure) : IPreparedDatReplacement
    {
        public DatReplacementFacts Facts { get; } = new(7, 13, 11, 42, "Catalog", "active", "candidate");
        public Stream OpenRead() => new CopyFaultStream(failure);
        public void Dispose() { }
    }

    private sealed class CopyFaultStream(IOException failure) : MemoryStream
    {
        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct)
        {
            await destination.WriteAsync(new byte[] { 1, 2 }, ct);
            throw failure;
        }
    }
}
