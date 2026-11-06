using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Platform.Commands.SetMetadataPolicy;
using Romd.Admin.Application.Source.Platform.Queries.GetMetadataPolicy;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Security;
using Romd.Application.Common.Systems;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Domain.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Realtime;
using Romd.Infrastructure.Realtime;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed partial class MetadataPolicyTests : IDisposable
{
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private static readonly string[] Priority = ["igdb", "dat"];
    public void Dispose() => database.Dispose();
    private async Task<int> SeedAsync()
    {
        await using var db = database.CreateContext();
        var platform = new PlatformEntity
        {
            Name = "Test system",
            ShortName = "test",
            CanonicalKey = "test",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Test system",
            BaseCompactLabel = "Test system"
        };
        db.Add(platform);
        await db.SaveChangesAsync();
        return platform.Id;
    }
    private static SetMetadataPolicyCommandHandler Handler(RomdDbContext db, int id,
        IEnrichmentJobEnqueuer? enqueuer = null, IAdminEventOutbox? outbox = null, Guid? actor = null, IUnitOfWork? work = null)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(actor);
        var reference = Substitute.For<IReferenceCatalogService>();
        reference.GetSystemKeysAsync(Arg.Any<CancellationToken>()).Returns(new SystemKeys(new Dictionary<int, string> { [id] = "test" }));
        return new(new PlatformRepository(db), new PlatformFieldDefaultRepository(db), new MetadataRematerializationJobRepository(db),
            outbox ?? new AdminRealtimeOutbox(db, TimeProvider.System), enqueuer ?? Substitute.For<IEnrichmentJobEnqueuer>(), user,
            reference, Options.Create(new EnrichmentOptions { GlobalSourcePriority = Priority.ToList() }),
            work ?? new EfUnitOfWork(db), NullLogger<SetMetadataPolicyCommandHandler>.Instance, TimeProvider.System);
    }
    private static RevisionPrecondition Revision(IReadOnlyDictionary<string, string>? defaults = null) =>
        RevisionPrecondition.For(MetadataPolicy.Revision(defaults ?? new Dictionary<string, string>(), Priority));

    [Fact]
    public async Task Save_ChangedPolicy_CommitsDefaultsJobDispatchAndV2EventDespiteEnqueueFailure()
    {
        int id = await SeedAsync();
        await using var db = database.CreateContext();
        var actor = Romd.Persistence.Identity.RomdUser.Create("policy-actor", "policy@example.test");
        db.Add(actor);
        await db.SaveChangesAsync();
        var enqueue = Substitute.For<IEnrichmentJobEnqueuer>();
        enqueue.EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new IOException("transport unavailable")));
        var result = await Handler(db, id, enqueue, actor: actor.Id).HandleAsync(new(id, Revision(), new Dictionary<string, string?> { ["Description"] = "igdb" }));
        result.IsError.ShouldBeFalse();
        result.Value.PendingJobId.ShouldNotBeNull();
        db.ChangeTracker.Clear();
        (await db.PlatformFieldDefaults.SingleAsync()).SourceId.ShouldBe("igdb");
        var job = await db.Set<MetadataRematerializationJobEntity>().SingleAsync();
        job.Id.ShouldBe(result.Value.PendingJobId.Value);
        job.SourceFilename.ShouldBe("test");
        job.CreatedByUserId.ShouldBe(actor.Id);
        (await db.JobDispatches.SingleAsync()).JobId.ShouldBe(job.Id);
        var evt = await db.AdminRealtimeOutboxEvents.SingleAsync();
        evt.SchemaVersion.ShouldBe(2);
        AdminRealtimePayloadSerializer.Deserialize<Romd.Contracts.Management.Models.JobDto>(evt.PayloadJson)
            .ShouldBeOfType<Romd.Contracts.Management.Models.MetadataRematerializationJobDto>().Id.ShouldBe(job.Id);
    }
    [Fact]
    public async Task Save_ReuseAndNoOp_StageNoDuplicateJobDispatchOrEvent_AndGetMatchesPendingId()
    {
        int id = await SeedAsync();
        await using var db = database.CreateContext();
        var handler = Handler(db, id);
        var first = (await handler.HandleAsync(new(id, Revision(), new Dictionary<string, string?> { ["Description"] = "igdb" }))).Value;
        var next = (await handler.HandleAsync(new(id, RevisionPrecondition.For(first.Revision), new Dictionary<string, string?> { ["Genre"] = "dat" }))).Value;
        next.PendingJobId.ShouldBe(first.PendingJobId);
        var noOp = (await handler.HandleAsync(new(id, RevisionPrecondition.For(next.Revision), new Dictionary<string, string?> { ["Genre"] = "dat" }))).Value;
        noOp.PendingJobId.ShouldBe(first.PendingJobId);
        var query = new GetMetadataPolicyQueryHandler(new PlatformRepository(db), new PlatformFieldDefaultRepository(db),
            new MetadataRematerializationJobRepository(db), Options.Create(new EnrichmentOptions { GlobalSourcePriority = Priority.ToList() }));
        (await query.HandleAsync(new(id))).Value.PendingJobId.ShouldBe(first.PendingJobId);
        (await db.Jobs.CountAsync()).ShouldBe(1);
        (await db.JobDispatches.CountAsync()).ShouldBe(1);
        (await db.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(1);
    }
    [Fact]
    public async Task Save_StaleOrOutboxFailure_RollsBackEverySideEffect()
    {
        int id = await SeedAsync();
        await using var db = database.CreateContext();
        var stale = await Handler(db, id).HandleAsync(new(id, RevisionPrecondition.For("stale"), new Dictionary<string, string?> { ["Description"] = "igdb" }));
        stale.FirstError.NumericType.ShouldBe(412);
        var outbox = Substitute.For<IAdminEventOutbox>();
        outbox.EnqueueAsync(Arg.Any<string>(), Arg.Any<Romd.Contracts.Management.Models.JobDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("outbox unavailable")));
        var failed = await Handler(db, id, outbox: outbox).HandleAsync(new(id, Revision(), new Dictionary<string, string?> { ["Description"] = "igdb" }));
        failed.FirstError.Code.ShouldBe("MetadataPolicy.SaveFailed");
        (await db.Jobs.CountAsync()).ShouldBe(0);
        (await db.JobDispatches.CountAsync()).ShouldBe(0);
        (await db.PlatformFieldDefaults.CountAsync()).ShouldBe(0);
    }
    [Fact]
    public async Task HistoricalCasing_Clear_RemovesAllSpellings()
    {
        int id = await SeedAsync();
        await using var db = database.CreateContext();
        db.PlatformFieldDefaults.AddRange(new PlatformFieldDefaultEntity { PlatformId = id, FieldName = "description", SourceId = "igdb" },
            new PlatformFieldDefaultEntity { PlatformId = id, FieldName = "Description", SourceId = "igdb" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var saved = await new PlatformFieldDefaultRepository(db).GetByPlatformIdAsync(id);
        saved.Count.ShouldBe(1);
        var result = await Handler(db, id).HandleAsync(new(id, Revision(saved), new Dictionary<string, string?> { ["Description"] = null }));
        result.IsError.ShouldBeFalse();
        (await db.PlatformFieldDefaults.CountAsync()).ShouldBe(0);
    }
    [Fact]
    public async Task PolicyLockWins_ClaimWaitsThenReadsCommittedDefaultsAndReusesOnlyUnclaimedJob()
    {
        int id = await SeedAsync();
        await using var policyDb = database.CreateContext();
        var initial = (await Handler(policyDb, id).HandleAsync(new(id, Revision(), new Dictionary<string, string?> { ["Description"] = "igdb" }))).Value;
        var pendingId = initial.PendingJobId!.Value;
        var work = new EfUnitOfWork(policyDb);
        await using var transaction = await work.BeginTransactionAsync();
        var defaults = new PlatformFieldDefaultRepository(policyDb);
        await defaults.LockAsync(id);
        (await new MetadataRematerializationJobRepository(policyDb).GetReusablePendingAsync(id))!.Id.ShouldBe(pendingId);
        await using var claimDb = database.CreateContext();
        await claimDb.Database.OpenConnectionAsync();
        int pid = ((Npgsql.NpgsqlConnection)claimDb.Database.GetDbConnection()).ProcessID;
        var claimTask = Claims(claimDb).TryClaimExecutionAsync(pendingId, "delivery");
        await WaitBlockedAsync(pid);
        claimTask.IsCompleted.ShouldBeFalse();
        await defaults.ClearStagedAsync(id, "Description");
        await work.FlushAsync();
        await defaults.SetStagedAsync(id, "Description", "user");
        await transaction.CommitAsync();
        var claimed = await claimTask.WaitAsync(TimeSpan.FromSeconds(10));
        claimed.ShouldNotBeNull();
        claimed.Job.Phase.ShouldBe("Rematerializing");
        (await new PlatformFieldDefaultRepository(claimDb).GetByPlatformIdAsync(id))["Description"].ShouldBe("user");
        var successor = await Handler(policyDb, id).HandleAsync(new(id,
            Revision(new Dictionary<string, string> { ["Description"] = "user" }), new Dictionary<string, string?> { ["Genre"] = "dat" }));
        successor.Value.PendingJobId.ShouldNotBe(pendingId);
    }
    [Fact]
    public async Task ClaimLockWins_PolicyWaitsThenCreatesSuccessorInsteadOfReusingClaimedRow()
    {
        int id = await SeedAsync();
        await using var seed = database.CreateContext();
        var initial = (await Handler(seed, id).HandleAsync(new(id, Revision(), new Dictionary<string, string?> { ["Description"] = "igdb" }))).Value;
        var gate = new ClaimGate();
        await using var claimDb = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(gate)));
        var claimTask = Claims(claimDb).TryClaimExecutionAsync(initial.PendingJobId!.Value, "delivery");
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var policyDb = database.CreateContext();
        await policyDb.Database.OpenConnectionAsync();
        int pid = ((Npgsql.NpgsqlConnection)policyDb.Database.GetDbConnection()).ProcessID;
        var patchTask = Handler(policyDb, id).HandleAsync(new(id, RevisionPrecondition.For(initial.Revision), new Dictionary<string, string?> { ["Genre"] = "dat" }));
        try
        { await WaitBlockedAsync(pid); patchTask.IsCompleted.ShouldBeFalse(); }
        finally { gate.Release.TrySetResult(); }
        (await claimTask.WaitAsync(TimeSpan.FromSeconds(10))).ShouldNotBeNull();
        var updated = await patchTask.WaitAsync(TimeSpan.FromSeconds(10));
        updated.IsError.ShouldBeFalse();
        updated.Value.PendingJobId.ShouldNotBe(initial.PendingJobId);
        (await seed.Set<MetadataRematerializationJobEntity>().CountAsync()).ShouldBe(2);
        (await seed.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(2);
    }
    private static ClaimedJobRepository<MetadataRematerializationJob, MetadataRematerializationJobRepository> Claims(RomdDbContext db) =>
        new(new MetadataRematerializationJobRepository(db), db, TimeProvider.System, new());
    private async Task WaitBlockedAsync(int pid)
    {
        await using var observer = database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await observer.Database.SqlQuery<int>($"SELECT cardinality(pg_blocking_pids({pid})) AS \"Value\"").AnyAsync(x => x > 0, timeout.Token))
            await Task.Yield();
    }
    private sealed class ClaimGate : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE romd.\"Jobs\"", StringComparison.Ordinal) &&
                command.CommandText.Contains("ExecutionFenceToken", StringComparison.Ordinal) && !Reached.Task.IsCompleted)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public void Validator_UsesSortedDefaultsAndOrderedGlobalPriority()
    {
        var a = new Dictionary<string, string> { ["Genre"] = "dat", ["Description"] = "igdb" };
        var b = new Dictionary<string, string> { ["Description"] = "igdb", ["Genre"] = "dat" };
        MetadataPolicy.Revision(a, Priority).ShouldBe(MetadataPolicy.Revision(b, Priority));
        MetadataPolicy.Revision(a, Priority).ShouldNotBe(MetadataPolicy.Revision(a, Priority.Reverse().ToArray()));
    }
}
