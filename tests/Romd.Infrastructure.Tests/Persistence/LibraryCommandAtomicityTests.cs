using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries.Commands.CreateLibrary;
using Romd.Admin.Application.Libraries.Commands.DeleteLibrary;
using Romd.Admin.Application.Libraries.Commands.ForceMaterializeLibrary;
using Romd.Admin.Application.Libraries.Commands.UpdateLibrary;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Contracts.Management.Realtime;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Libraries;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Realtime;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class LibraryCommandAtomicityTests
{
    [Fact]
    public async Task CreateLibrary_Success_CommitsGeneratedIdDefaultSwitchEventAndPostCommitSchedule()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedLibraryAsync(database.Context, 1, "Previous", isDefault: true);
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var handler = CreateHandler(database, scheduler);

        var result = await handler.HandleAsync(
            new CreateLibraryCommand("Arcade", new LibraryConfiguration(), true));

        result.IsError.ShouldBeFalse();
        IdCoder.TryDecode(result.Value.Id, out int createdId).ShouldBeTrue();
        createdId.ShouldBeGreaterThan(1);
        await using var read = database.CreateReadContext();
        var previous = await read.Libraries.SingleAsync(row => row.Id == 1);
        var created = await read.Libraries.SingleAsync(row => row.Id == createdId);
        previous.IsDefault.ShouldBeFalse();
        created.IsDefault.ShouldBeTrue();
        created.NeedsMaterialization.ShouldBeTrue();
        var payload = await GetOnlyLibraryEventPayloadAsync(read);
        payload.LibraryId.ShouldBe(IdCoder.Encode(createdId));
        payload.Name.ShouldBe("Arcade");
        payload.NeedsMaterialization.ShouldBeTrue();
        await scheduler.Received(1).EnqueueIfNeededAsync(createdId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateLibrary_CommitFails_RollsBackInsertDefaultSwitchAndEvent()
    {
        await using var database = await TestDatabase.CreateAsync(failCommit: true);
        await SeedLibraryAsync(database.Context, 1, "Previous", isDefault: true);
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var handler = CreateHandler(database, scheduler);

        var result = await handler.HandleAsync(
            new CreateLibraryCommand("Arcade", new LibraryConfiguration(), true));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Libraries.PersistenceFailed");
        await using var read = database.CreateReadContext();
        (await read.Libraries.SingleAsync()).IsDefault.ShouldBeTrue();
        (await read.Libraries.AnyAsync(row => row.Name == "Arcade")).ShouldBeFalse();
        (await read.AdminRealtimeOutboxEvents.AnyAsync()).ShouldBeFalse();
        await scheduler.DidNotReceiveWithAnyArgs().EnqueueIfNeededAsync(default, default);
    }

    [Fact]
    public async Task CreateLibrary_CancelledAfterGeneratedIdFlush_RollsBackDefaultAndInsertWithoutEvent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedLibraryAsync(database.Context, 1, "Previous", isDefault: true);
        using var cancellation = new CancellationTokenSource();
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var handler = new CreateLibraryCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(database.Context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
            database.Repository,
            scheduler,
            database.Outbox,
            new CancelAfterFlushUnitOfWork(database.UnitOfWork, cancellation),
            NullLogger<CreateLibraryCommandHandler>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(
            new CreateLibraryCommand("Arcade", new LibraryConfiguration(), true),
            cancellation.Token));

        await using var read = database.CreateReadContext();
        (await read.Libraries.SingleAsync()).IsDefault.ShouldBeTrue();
        (await read.Libraries.AnyAsync(row => row.Name == "Arcade")).ShouldBeFalse();
        (await read.AdminRealtimeOutboxEvents.AnyAsync()).ShouldBeFalse();
        await scheduler.DidNotReceiveWithAnyArgs().EnqueueIfNeededAsync(default, default);
    }

    [Fact]
    public async Task UpdateLibrary_Success_CommitsTargetDefaultEventAndPreservesPersistenceOnlyAuditState()
    {
        await using var database = await TestDatabase.CreateAsync();
        var createdBy = Guid.NewGuid();
        await SeedLibraryAsync(database.Context, 1, "Previous", isDefault: true);
        await SeedLibraryAsync(database.Context, 2, "Target", createdByUserId: createdBy);
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var handler = UpdateHandler(database, scheduler);

        var result = await handler.HandleAsync(
            new UpdateLibraryCommand(2, "Updated", new LibraryConfiguration(), true));

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        var previous = await read.Libraries.SingleAsync(row => row.Id == 1);
        var target = await read.Libraries.SingleAsync(row => row.Id == 2);
        previous.IsDefault.ShouldBeFalse();
        target.Name.ShouldBe("Updated");
        target.IsDefault.ShouldBeTrue();
        target.NeedsMaterialization.ShouldBeTrue();
        target.CreatedByUserId.ShouldBe(createdBy);
        var payload = await GetOnlyLibraryEventPayloadAsync(read);
        payload.LibraryId.ShouldBe(IdCoder.Encode(2));
        payload.Name.ShouldBe("Updated");
        await scheduler.Received(1).EnqueueIfNeededAsync(2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateLibrary_CommitFails_RollsBackTargetPreviousDefaultAndEvent()
    {
        await using var database = await TestDatabase.CreateAsync(failCommit: true);
        await SeedLibraryAsync(database.Context, 1, "Previous", isDefault: true);
        await SeedLibraryAsync(database.Context, 2, "Target");
        var scheduler = Substitute.For<ILibraryMaterializationScheduler>();
        var handler = UpdateHandler(database, scheduler);

        var result = await handler.HandleAsync(
            new UpdateLibraryCommand(2, "Updated", new LibraryConfiguration(), true));

        result.IsError.ShouldBeTrue();
        await using var read = database.CreateReadContext();
        var previous = await read.Libraries.SingleAsync(row => row.Id == 1);
        var target = await read.Libraries.SingleAsync(row => row.Id == 2);
        previous.IsDefault.ShouldBeTrue();
        previous.Name.ShouldBe("Previous");
        target.IsDefault.ShouldBeFalse();
        target.Name.ShouldBe("Target");
        (await read.AdminRealtimeOutboxEvents.AnyAsync()).ShouldBeFalse();
        await scheduler.DidNotReceiveWithAnyArgs().EnqueueIfNeededAsync(default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteLibrary_CommitOutcome_KeepsDeleteAndEventAtomic(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit);
        await SeedLibraryAsync(database.Context, 7, "Disposable");
        var handler = new DeleteLibraryCommandHandler(
            database.Repository,
            database.Outbox,
            database.UnitOfWork,
            NullLogger<DeleteLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new DeleteLibraryCommand(7));

        await using var read = database.CreateReadContext();
        (await read.Libraries.AnyAsync(row => row.Id == 7)).ShouldBe(failCommit);
        if (failCommit)
        {
            result.IsError.ShouldBeTrue();
            (await read.AdminRealtimeOutboxEvents.AnyAsync()).ShouldBeFalse();
        }
        else
        {
            result.IsError.ShouldBeFalse();
            var payload = await GetOnlyLibraryEventPayloadAsync(read);
            payload.LibraryId.ShouldBe(IdCoder.Encode(7));
            payload.Name.ShouldBe("Disposable");
        }
    }

    [Fact]
    public async Task ForceMaterialize_Success_CommitsFlagPendingJobCreatorAndExactEvent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var creatorId = Guid.NewGuid();
        await SeedUserAsync(database.Context, creatorId);
        await SeedLibraryAsync(database.Context, 7, "Arcade");
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        var handler = ForceHandler(database, enqueuer, creatorId);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        (await read.Libraries.SingleAsync()).NeedsMaterialization.ShouldBeTrue();
        var job = await read.Jobs.OfType<MaterializationJobEntity>().SingleAsync();
        job.Id.ShouldBe(result.Value);
        job.Phase.ShouldBe(MaterializationPhase.Pending.ToString());
        job.CreatedByUserId.ShouldBe(creatorId);
        job.HangfireJobId.ShouldBeNull();
        var payload = await GetOnlyLibraryEventPayloadAsync(read);
        payload.LibraryId.ShouldBe(IdCoder.Encode(7));
        payload.Name.ShouldBe("Arcade");
        payload.NeedsMaterialization.ShouldBeTrue();
        await enqueuer.Received(1).EnqueueAsync(result.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForceMaterialize_ConfigurationChangesAfterInitialRead_PreservesEditAndUsesCurrentName()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedLibraryAsync(database.Context, 7, "Before");
        var jobs = Substitute.For<IMaterializationJobRepository>();
        Guid editedRevision = Guid.Empty;
        jobs.GetActiveForLibraryAsync(7, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                // The handler has read the old library but has not acquired its write lock.
                // Commit the competing configuration edit from an independent context.
                await using var editingContext = database.CreateReadContext();
                var editingRepository = new LibraryRepository(editingContext);
                var edited = (await editingRepository.GetByIdAsync(7))!;
                edited.UpdateConfiguration("After", new LibraryConfiguration { ShowMissingGames = true });
                edited.MarkAsDefault();
                await editingRepository.UpdateAsync(edited);
                editedRevision = (await editingRepository.GetByIdAsync(7))!.MaterializationRevision;
                return (MaterializationJob?)null;
            });
        jobs.AddStagedAsync(Arg.Any<MaterializationJob>(), Arg.Any<CancellationToken>())
            .Returns(call => database.JobRepository.AddStagedAsync(
                call.Arg<MaterializationJob>(), call.Arg<CancellationToken>()));
        var handler = new ForceMaterializeLibraryCommandHandler(
            database.Repository,
            jobs,
            Substitute.For<IMaterializationJobEnqueuer>(),
            database.Outbox,
            Substitute.For<ICurrentUser>(),
            database.UnitOfWork,
            NullLogger<ForceMaterializeLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        var persisted = (await new LibraryRepository(read).GetByIdAsync(7))!;
        persisted.Name.ShouldBe("After");
        persisted.Configuration.ShowMissingGames.ShouldBeTrue();
        persisted.IsDefault.ShouldBeTrue();
        persisted.NeedsMaterialization.ShouldBeTrue();
        persisted.MaterializationRevision.ShouldNotBe(editedRevision);
        (await read.Jobs.OfType<MaterializationJobEntity>().SingleAsync()).SourceFilename.ShouldBe("After");
        (await GetOnlyLibraryEventPayloadAsync(read)).Name.ShouldBe("After");
    }

    [Fact]
    public async Task ForceMaterialize_FinalCommitFailure_RollsBackFlagJobAndEvent()
    {
        await using var database = await TestDatabase.CreateAsync(failCommit: true);
        await SeedLibraryAsync(database.Context, 7, "Arcade");
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        var handler = ForceHandler(database, enqueuer, null);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Libraries.PersistenceFailed");
        await AssertNoForceMaterializeEffectsAsync(database);
        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    [Fact]
    public async Task ForceMaterialize_CancelledAfterFinalFlush_RollsBackFlagJobAndEvent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedLibraryAsync(database.Context, 7, "Arcade");
        using var cancellation = new CancellationTokenSource();
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        var currentUser = Substitute.For<ICurrentUser>();
        var unitOfWork = new CancelAfterCommitFlushUnitOfWork(
            database.UnitOfWork,
            cancellation);
        var handler = new ForceMaterializeLibraryCommandHandler(
            database.Repository,
            database.JobRepository,
            enqueuer,
            database.Outbox,
            currentUser,
            unitOfWork,
            NullLogger<ForceMaterializeLibraryCommandHandler>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(
            new ForceMaterializeLibraryCommand(7),
            cancellation.Token));

        await AssertNoForceMaterializeEffectsAsync(database);
        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    [Fact]
    public async Task ForceMaterialize_ActiveDispatchedRetry_ReturnsSameJobWithoutTouchingLibraryEventOrEnqueue()
    {
        await using var database = await TestDatabase.CreateAsync();
        var originalUpdatedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        await SeedLibraryAsync(database.Context, 7, "Arcade", updatedAt: originalUpdatedAt);
        var existing = MaterializationJob.Create(7, "Arcade");
        existing.SetHangfireJobId("hangfire-dispatched");
        await database.JobRepository.AddAsync(existing);
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        var handler = ForceHandler(database, enqueuer, null);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(existing.Id);
        await using var read = database.CreateReadContext();
        var library = await read.Libraries.SingleAsync();
        library.NeedsMaterialization.ShouldBeFalse();
        library.UpdatedAt.ShouldBe(originalUpdatedAt);
        (await read.Jobs.OfType<MaterializationJobEntity>().CountAsync()).ShouldBe(1);
        (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    [Fact]
    public async Task MaterializationJobRepository_AddAsync_DetachesOnlyAddedJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedLibraryAsync(database.Context, 7, "Arcade");
        var trackedLibrary = await database.Context.Libraries.AsTracking().SingleAsync();

        await database.JobRepository.AddAsync(MaterializationJob.Create(7, "Arcade"));

        database.Context.Entry(trackedLibrary).State.ShouldBe(EntityState.Unchanged);
        database.Context.ChangeTracker.Entries<MaterializationJobEntity>().ShouldBeEmpty();
    }

    [Fact]
    public async Task ForceMaterialize_TerminalRetry_CreatesNewJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedLibraryAsync(database.Context, 7, "Arcade");
        var terminal = MaterializationJob.Create(7, "Arcade");
        terminal.Cancel();
        await database.JobRepository.AddAsync(terminal);
        var handler = ForceHandler(
            database,
            Substitute.For<IMaterializationJobEnqueuer>(),
            null);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldNotBe(terminal.Id);
        await using var read = database.CreateReadContext();
        var jobs = await read.Jobs.OfType<MaterializationJobEntity>()
            .OrderBy(job => job.CreatedAt)
            .ToListAsync();
        jobs.Count.ShouldBe(2);
        jobs.ShouldContain(job => job.Id == terminal.Id && job.Phase == MaterializationPhase.Cancelled.ToString());
        jobs.ShouldContain(job => job.Id == result.Value && job.Phase == MaterializationPhase.Pending.ToString());
    }

    [Fact]
    public async Task ForceMaterialize_InvalidConfiguration_PersistsObservablePendingJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedLibraryAsync(
            database.Context,
            7,
            "Broken",
            configurationState: LibraryConfigurationState.Invalid,
            configurationError: "Missing platform");
        var handler = ForceHandler(
            database,
            Substitute.For<IMaterializationJobEnqueuer>(),
            null);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        var library = await read.Libraries.SingleAsync();
        library.ConfigurationState.ShouldBe(LibraryConfigurationState.Invalid.ToString());
        library.ConfigurationError.ShouldBe("Missing platform");
        library.NeedsMaterialization.ShouldBeTrue();
        (await read.Jobs.OfType<MaterializationJobEntity>().SingleAsync()).Id.ShouldBe(result.Value);
    }

    [Fact]
    public async Task ForceMaterialize_SequentialDuplicateRequests_PersistSingleWinnerAndRetryConverges()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(database.ConnectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        try
        {
            await using (var setup = new RomdDbContext(options))
            {
                await SeedLibraryAsync(setup, 7, "Arcade");
            }

            await using var firstContext = new RomdDbContext(options);
            await using var secondContext = new RomdDbContext(options);
            var firstJobs = new MaterializationJobRepository(firstContext, TimeProvider.System);
            var secondJobs = new MaterializationJobRepository(secondContext, TimeProvider.System);
            var firstHandler = CreateIsolatedContextForceHandler(firstContext, firstJobs);
            var secondHandler = CreateIsolatedContextForceHandler(secondContext, secondJobs);

            var first = await firstHandler.HandleAsync(new ForceMaterializeLibraryCommand(7));
            var second = await secondHandler.HandleAsync(new ForceMaterializeLibraryCommand(7));

            first.IsError.ShouldBeFalse();
            second.IsError.ShouldBeFalse();
            second.Value.ShouldBe(first.Value);

            await using (var read = new RomdDbContext(options))
            {
                var winner = await read.Jobs.OfType<MaterializationJobEntity>().SingleAsync();
                winner.Id.ShouldBe(first.Value);
                (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(1);
                (await read.Libraries.SingleAsync()).NeedsMaterialization.ShouldBeTrue();
            }

            await using var retryContext = new RomdDbContext(options);
            var retryHandler = CreateIsolatedContextForceHandler(
                retryContext,
                new MaterializationJobRepository(retryContext, TimeProvider.System));
            var retry = await retryHandler.HandleAsync(new ForceMaterializeLibraryCommand(7));

            retry.IsError.ShouldBeFalse();
            retry.Value.ShouldBe(first.Value);
            await using var finalRead = new RomdDbContext(options);
            (await finalRead.Jobs.OfType<MaterializationJobEntity>().CountAsync()).ShouldBe(1);
            (await finalRead.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(1);
        }
        finally
        {
        }
    }

    [Fact]
    public async Task ForceMaterialize_LoserCommitHitsActiveUniqueIndex_ConvergesOnWinnerJobWithoutSideEffects()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedLibraryAsync(database.Context, 7, "Arcade");
        var winner = MaterializationJob.Create(7, "Arcade");
        await database.JobRepository.AddAsync(winner);
        var jobs = new NullFirstActiveLookupJobRepository(database.JobRepository);
        var enqueuer = Substitute.For<IMaterializationJobEnqueuer>();
        var handler = new ForceMaterializeLibraryCommandHandler(
            database.Repository,
            jobs,
            enqueuer,
            database.Outbox,
            Substitute.For<ICurrentUser>(),
            database.UnitOfWork,
            NullLogger<ForceMaterializeLibraryCommandHandler>.Instance);

        var result = await handler.HandleAsync(new ForceMaterializeLibraryCommand(7));

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(winner.Id);
        await using var read = database.CreateReadContext();
        var library = await read.Libraries.SingleAsync();
        library.NeedsMaterialization.ShouldBeFalse();
        var job = await read.Jobs.OfType<MaterializationJobEntity>().SingleAsync();
        job.Id.ShouldBe(winner.Id);
        job.Phase.ShouldBe(MaterializationPhase.Pending.ToString());
        job.HangfireJobId.ShouldBeNull();
        (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        await enqueuer.DidNotReceiveWithAnyArgs().EnqueueAsync(default, default);
    }

    private static CreateLibraryCommandHandler CreateHandler(
        TestDatabase database,
        ILibraryMaterializationScheduler scheduler) =>
        new(new Romd.Persistence.ReferenceData.ReferenceCatalogService(database.Context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
            database.Repository,
            scheduler,
            database.Outbox,
            database.UnitOfWork,
            NullLogger<CreateLibraryCommandHandler>.Instance);

    private static UpdateLibraryCommandHandler UpdateHandler(
        TestDatabase database,
        ILibraryMaterializationScheduler scheduler) =>
        new(new Romd.Persistence.ReferenceData.ReferenceCatalogService(database.Context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
            database.Repository,
            scheduler,
            database.Outbox,
            database.UnitOfWork,
            NullLogger<UpdateLibraryCommandHandler>.Instance);

    private static ForceMaterializeLibraryCommandHandler ForceHandler(
        TestDatabase database,
        IMaterializationJobEnqueuer enqueuer,
        Guid? creatorId)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(creatorId);
        return new ForceMaterializeLibraryCommandHandler(
            database.Repository,
            database.JobRepository,
            enqueuer,
            database.Outbox,
            currentUser,
            database.UnitOfWork,
            NullLogger<ForceMaterializeLibraryCommandHandler>.Instance);
    }

    private static ForceMaterializeLibraryCommandHandler CreateIsolatedContextForceHandler(
        RomdDbContext context,
        IMaterializationJobRepository jobs) =>
        new(
            new LibraryRepository(context),
            jobs,
            Substitute.For<IMaterializationJobEnqueuer>(),
            new AdminRealtimeOutbox(context, TimeProvider.System),
            Substitute.For<ICurrentUser>(),
            new EfUnitOfWork(context),
            NullLogger<ForceMaterializeLibraryCommandHandler>.Instance);

    private static async Task AssertNoForceMaterializeEffectsAsync(TestDatabase database)
    {
        await using var read = database.CreateReadContext();
        (await read.Libraries.SingleAsync()).NeedsMaterialization.ShouldBeFalse();
        (await read.Jobs.OfType<MaterializationJobEntity>().AnyAsync()).ShouldBeFalse();
        (await read.AdminRealtimeOutboxEvents.AnyAsync()).ShouldBeFalse();
    }

    private static async Task<AdminRealtimeLibraryUpdatedPayload> GetOnlyLibraryEventPayloadAsync(
        RomdDbContext context)
    {
        var row = await context.AdminRealtimeOutboxEvents.SingleAsync();
        row.EventType.ShouldBe(AdminRealtimeEventTypes.LibraryUpdated);
        return AdminRealtimePayloadSerializer.Deserialize<AdminRealtimeLibraryUpdatedPayload>(row.PayloadJson);
    }

    private static async Task SeedLibraryAsync(
        RomdDbContext context,
        int id,
        string name,
        bool isDefault = false,
        Guid? createdByUserId = null,
        DateTimeOffset? updatedAt = null,
        LibraryConfigurationState configurationState = LibraryConfigurationState.Valid,
        string? configurationError = null)
    {
        context.Libraries.Add(new LibraryEntity
        {
            Id = id,
            Name = name,
            ConfigurationJson = JsonSerializer.Serialize(new LibraryConfiguration()),
            ConfigurationState = configurationState.ToString(),
            ConfigurationError = configurationError,
            IsDefault = isDefault,
            NeedsMaterialization = false,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId ?? Guid.Empty,
            UpdatedAt = updatedAt
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task SeedUserAsync(RomdDbContext context, Guid userId)
    {
        context.Users.Add(new RomdUser
        {
            Id = userId,
            UserName = $"user-{userId:N}",
            NormalizedUserName = $"USER-{userId:N}",
            Email = $"{userId:N}@example.test",
            NormalizedEmail = $"{userId:N}@EXAMPLE.TEST",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private sealed class NullFirstActiveLookupJobRepository(IMaterializationJobRepository inner)
        : IMaterializationJobRepository
    {
        private bool _missNextActiveLookup = true;

        public Task<MaterializationJob?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<MaterializationJob>> GetActiveAsync(CancellationToken ct = default) =>
            inner.GetActiveAsync(ct);

        public Task<MaterializationJob?> GetActiveForLibraryAsync(
            int libraryId,
            CancellationToken ct = default)
        {
            if (_missNextActiveLookup)
            {
                _missNextActiveLookup = false;
                return Task.FromResult<MaterializationJob?>(null);
            }

            return inner.GetActiveForLibraryAsync(libraryId, ct);
        }

        public Task AddStagedAsync(MaterializationJob job, CancellationToken ct = default) =>
            inner.AddStagedAsync(job, ct);

        public Task AddAsync(MaterializationJob job, CancellationToken ct = default) =>
            inner.AddAsync(job, ct);

        public Task<bool> TryAddIfNoActiveForLibraryAsync(
            MaterializationJob job,
            CancellationToken ct = default) =>
            inner.TryAddIfNoActiveForLibraryAsync(job, ct);

        public Task SetHangfireJobIdAsync(
            Guid jobId,
            string hangfireJobId,
            CancellationToken ct = default) =>
            inner.SetHangfireJobIdAsync(jobId, hangfireJobId, ct);

        public Task UpdateAsync(MaterializationJob job, CancellationToken ct = default) =>
            inner.UpdateAsync(job, ct);
    }

    private sealed class CancelAfterFlushUnitOfWork(
        IUnitOfWork inner,
        CancellationTokenSource cancellation) : IUnitOfWork
    {
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            await cancellation.CancelAsync();
            throw new OperationCanceledException(cancellation.Token);
        }

        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            inner.BeginTransactionAsync(cancellationToken);
    }

    private sealed class FailingCommitUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new FailingCommitTransaction(
                inner,
                await inner.BeginTransactionAsync(cancellationToken));
    }

    private sealed class CancelAfterCommitFlushUnitOfWork(
        IUnitOfWork inner,
        CancellationTokenSource cancellation) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(
            CancellationToken cancellationToken = default) =>
            new CancelAfterCommitFlushTransaction(
                inner,
                await inner.BeginTransactionAsync(cancellationToken),
                cancellation);
    }

    private sealed class CancelAfterCommitFlushTransaction(
        IUnitOfWork unitOfWork,
        ITransaction inner,
        CancellationTokenSource cancellation) : ITransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await unitOfWork.FlushAsync(cancellationToken);
            await cancellation.CancelAsync();
            await inner.RollbackAsync(CancellationToken.None);
            throw new OperationCanceledException(cancellation.Token);
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FailingCommitTransaction(
        IUnitOfWork unitOfWork,
        ITransaction inner) : ITransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await unitOfWork.FlushAsync(cancellationToken);
            await inner.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException("Injected commit failure.");
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            inner.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase _connection;
        private readonly DbContextOptions<RomdDbContext> _options;

        private TestDatabase(
            PostgreSqlTestDatabase connection,
            DbContextOptions<RomdDbContext> options,
            RomdDbContext context,
            bool failCommit)
        {
            _connection = connection;
            _options = options;
            Context = context;
            Repository = new LibraryRepository(context);
            JobRepository = new MaterializationJobRepository(context, TimeProvider.System);
            Outbox = new AdminRealtimeOutbox(context, TimeProvider.System);
            IUnitOfWork unitOfWork = new EfUnitOfWork(context);
            UnitOfWork = failCommit ? new FailingCommitUnitOfWork(unitOfWork) : unitOfWork;
        }

        public RomdDbContext Context { get; }
        public LibraryRepository Repository { get; }
        public MaterializationJobRepository JobRepository { get; }
        public AdminRealtimeOutbox Outbox { get; }
        public IUnitOfWork UnitOfWork { get; }

        public RomdDbContext CreateReadContext() => new(_options);

        public static async Task<TestDatabase> CreateAsync(bool failCommit = false)
        {
            var connection = PostgreSqlTestDatabase.Create();
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var context = new RomdDbContext(options);
            return new TestDatabase(connection, options, context, failCommit);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
