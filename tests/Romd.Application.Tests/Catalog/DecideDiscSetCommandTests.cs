using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Commands.DecideDiscSet;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Catalog;

public sealed class DecideDiscSetCommandTests
{
    [Fact]
    public async Task Decision_OwnsProtectedReadAndBothEffectsBeforeConstructingResponseAndCommitting()
    {
        var fixture = new Fixture();

        var result = await fixture.Handle();

        result.IsError.ShouldBeFalse();
        fixture.Events.ShouldBe(["begin", "topology", "target", "facts", "claims", "decision", "libraries", "system", "commit", "dispose"]);
        result.Value.Decision.ShouldBe("accepted");
        result.Value.SystemKey.ShouldBe("psx");
        result.Value.DecidedAt.ShouldBe(fixture.Now);
        result.Value.DecidedBy.ShouldBe(fixture.Actor);
        result.Value.Revision.ShouldBe(fixture.Store.StagedRevision);
        result.Value.Revision.ShouldNotBe(fixture.Store.Row.Revision);
        fixture.Store.StagedActor.ShouldBe(fixture.Actor);
        fixture.Store.StagedTime.ShouldBe(fixture.Now);
        fixture.Store.InvalidatedRevision.ShouldNotBe(Guid.Empty);
        fixture.Store.InvalidatedRevision.ShouldNotBe(fixture.Store.StagedRevision);
        fixture.Store.Keys.ShouldBe(["disc"]);
    }

    [Theory]
    [InlineData("invalid", "DiscSet.Decision", "begin,dispose")]
    [InlineData("missing", "DiscSet.Missing", "begin,topology,target,dispose")]
    [InlineData("stale", "DiscSet.PreconditionFailed", "begin,topology,target,dispose")]
    [InlineData("review", "DiscSet.Review", "begin,topology,target,facts,claims,dispose")]
    public async Task Decision_RejectsAtItsOriginalBoundaryWithoutStagingOrCommitting(string variation, string code, string events)
    {
        var fixture = new Fixture();
        fixture.Store.Missing = variation == "missing";
        fixture.Store.MustNotReadFacts = variation == "stale";
        if (variation == "review")
            fixture.Store.Row = fixture.Store.Row with
            {
                Facts = new(2, "proposed", new("Game", 1,
                [new("disc", "Disc", "fp:disc", 1, 3)], ["NeedsReview"], "test"), null)
            };

        var result = await fixture.Handle(variation == "invalid" ? "ACCEPT" : "accept", variation == "stale");

        result.FirstError.Code.ShouldBe(code);
        fixture.Events.ShouldBe(events.Split(','));
        fixture.Store.StagedRevision.ShouldBe(Guid.Empty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_SystemProjectionFailureBeforeCommit_PreservesExactPrimaryThroughCleanup(bool cancellation)
    {
        var fixture = new Fixture();
        using var source = new CancellationTokenSource();
        Exception primary = cancellation ? new OperationCanceledException(source.Token) : new IOException("Projection failed");
        fixture.Store.SystemFailure = primary;
        fixture.Work.DisposeFailure = new IOException("Secondary disposal");
        Exception? observed = null;

        try
        { await fixture.Handle(); }
        catch (Exception error) { observed = error; }

        observed.ShouldBeSameAs(primary);
        if (observed is OperationCanceledException cancelled)
            cancelled.CancellationToken.ShouldBe(source.Token);
        fixture.Events.ShouldBe(["begin", "topology", "target", "facts", "claims", "decision", "libraries", "system", "dispose"]);
    }

    private sealed class Fixture
    {
        public List<string> Events { get; } = [];
        public Guid Actor { get; } = Guid.NewGuid();
        public DateTimeOffset Now { get; } = DateTimeOffset.UnixEpoch.AddTicks(1234567);
        public Store Store { get; }
        public Work Work { get; }
        public Fixture() { Store = new(Events); Work = new(Events); }
        public Task<ErrorOr.ErrorOr<DiscSetDto>> Handle(string decision = "accept", bool stale = false) =>
            new DecideDiscSetCommandHandler(Store, Work, new Clock(Now), NullLogger<DecideDiscSetCommandHandler>.Instance)
                .HandleAsync(new(1, new(decision, null, null, null, "Reviewed"),
                    RevisionPrecondition.For((stale ? Guid.NewGuid() : Store.Row.Revision).ToString()), Actor));
    }

    private sealed class Store(List<string> events) : IDiscSetDecisionStore
    {
        public DiscSetSnapshot Row { get; set; } = new(1, 7, Guid.NewGuid(), new(2, "proposed",
            new("Game", 1, [new("disc", "Disc", "fp:disc", 1, 3)], [], "test"), null), null, null, null, null);
        public bool Missing { get; set; }
        public bool MustNotReadFacts { get; set; }
        public Exception? SystemFailure { get; set; }
        public Guid StagedRevision { get; private set; }
        public Guid StagedActor { get; private set; }
        public DateTimeOffset StagedTime { get; private set; }
        public Guid InvalidatedRevision { get; private set; }
        public IReadOnlyCollection<string> Keys { get; private set; } = [];
        public Task LockTopologyAsync(CancellationToken ct) { events.Add("topology"); return Task.CompletedTask; }
        public Task<DiscSetDecisionTarget?> ReadAsync(int id, CancellationToken ct)
        { events.Add("target"); id.ShouldBe(Row.Id); return Task.FromResult(Missing ? null : new DiscSetDecisionTarget(Row.Id, Row.PlatformId, Row.Revision)); }
        public DiscSetSnapshot ReadFacts(DiscSetDecisionTarget original)
        { MustNotReadFacts.ShouldBeFalse(); events.Add("facts"); return Row; }
        public Task<DiscSetClaims> ReadClaimsAsync(int platformId, int sourceId, IReadOnlyCollection<string> keys, CancellationToken ct)
        { events.Add("claims"); platformId.ShouldBe(7); sourceId.ShouldBe(2); Keys = keys; return Task.FromResult(new DiscSetClaims([new(2, "disc", "Disc", "fp:disc", 9, 3, false)])); }
        public Task StageDecisionAsync(DiscSetSnapshot original, DiscSetDecisionChange change, string? reason, Guid actor, DateTimeOffset time, Guid revision, CancellationToken ct)
        { events.Add("decision"); original.ShouldBeSameAs(Row); change.Decision.ShouldBe("accepted"); reason.ShouldBe("Reviewed"); StagedRevision = revision; StagedActor = actor; StagedTime = time; return Task.CompletedTask; }
        public Task InvalidateAffectedLibrariesAsync(int id, Guid revision, DateTimeOffset now, CancellationToken ct)
        { events.Add("libraries"); id.ShouldBe(Row.Id); InvalidatedRevision = revision; return Task.CompletedTask; }
        public Task<string?> ReadSystemKeyAsync(int platformId, CancellationToken ct)
        { events.Add("system"); if (SystemFailure is not null) throw SystemFailure; return Task.FromResult<string?>("psx"); }
    }

    private sealed class Work(List<string> events) : IUnitOfWork, ITransaction
    {
        public Exception? DisposeFailure { get; set; }
        public Task<ITransaction> BeginTransactionAsync(CancellationToken ct) { events.Add("begin"); return Task.FromResult<ITransaction>(this); }
        public Task FlushAsync(CancellationToken ct) => throw new InvalidOperationException("Only commit flushes this decision");
        public Task CommitAsync(CancellationToken ct) { events.Add("commit"); return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken ct) => throw new InvalidOperationException("The helper disposes uncommitted decisions");
        public ValueTask DisposeAsync() { events.Add("dispose"); if (DisposeFailure is not null) throw DisposeFailure; return ValueTask.CompletedTask; }
        public TransactionConflict ConflictOf(Exception error) => throw new InvalidOperationException("No implicit decision retry");
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
