using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Commands.QueueArtworkCompletion;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

using ArtworkCompletionJob = Romd.Domain.Jobs.ArtworkCompletionJob;

namespace Romd.Application.Tests.Artwork;

public sealed class QueueArtworkCompletionCommandTests
{
    [Fact]
    public async Task HandleAsync_EmptyRequestId_RejectsBeforeDatabaseWork()
    {
        var fixture = new Fixture();
        fixture.Request = fixture.Request with { RequestId = Guid.Empty };

        (await fixture.HandleAsync()).FirstError.Code.ShouldBe("Artwork.InvalidRequest");

        fixture.Acceptance.ReceivedCalls().ShouldBeEmpty();
        await fixture.Work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
    }

    [Fact]
    public async Task HandleAsync_ExactReplay_CommitsWithoutOwnerLockOrReadingLiveScope()
    {
        var fixture = new Fixture();
        fixture.Acceptance.FindRequestAsync(fixture.Request.RequestId, Arg.Any<CancellationToken>())
            .Returns(new ArtworkCompletionRequestIdentity(fixture.Request.RequestId, ArtworkCompletionIntent.Signature(fixture.Request)));

        (await fixture.HandleAsync()).Value.ShouldBe(fixture.Request.RequestId);

        await fixture.Transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await fixture.Acceptance.DidNotReceiveWithAnyArgs().LockOwnersAsync(default);
        await fixture.Acceptance.DidNotReceiveWithAnyArgs().SnapshotScopeAsync(default!, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("different signature")]
    public async Task HandleAsync_RequestIdWithOtherFamilyOrIntent_RejectsBeforeOwnerLock(string? signature)
    {
        var fixture = new Fixture();
        fixture.Acceptance.FindRequestAsync(fixture.Request.RequestId, Arg.Any<CancellationToken>())
            .Returns(new ArtworkCompletionRequestIdentity(fixture.Request.RequestId, signature));

        (await fixture.HandleAsync()).FirstError.Code.ShouldBe("Artwork.RequestConflict");

        await fixture.Transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await fixture.Acceptance.DidNotReceiveWithAnyArgs().LockOwnersAsync(default);
    }

    [Fact]
    public async Task HandleAsync_SelectedReuse_CommitsExistingIdentityWithoutSnapshotOrNewJob()
    {
        var fixture = new Fixture();
        var existing = Guid.NewGuid();
        fixture.Acceptance.FindActiveSelectedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(existing);

        (await fixture.HandleAsync()).Value.ShouldBe(existing);

        await fixture.Acceptance.Received(1).LockOwnersAsync(Arg.Any<CancellationToken>());
        await fixture.Acceptance.DidNotReceiveWithAnyArgs().SnapshotScopeAsync(default!, default);
        await fixture.Transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_RetryOriginalMissing_RejectsBeforeTransactionEvenWhenRequestIdCouldReplay()
    {
        var fixture = new Fixture();
        var retryJobId = Guid.NewGuid();
        fixture.Request = new(Guid.NewGuid(), Scope: "Retry", RetryJobId: retryJobId, RetryStatus: "Failed");
        fixture.Acceptance.ReadRetryModeAsync(retryJobId, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        (await fixture.HandleAsync()).FirstError.Code.ShouldBe("Artwork.TitleNotFound");

        await fixture.Work.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
        await fixture.Acceptance.DidNotReceiveWithAnyArgs().FindRequestAsync(default, default);
    }

    [Fact]
    public async Task HandleAsync_NewRetry_UsesOriginalModeAndCapturesSeparatePostReadAndPostFlushTimes()
    {
        var fixture = new Fixture();
        var original = Guid.NewGuid();
        fixture.Request = new(Guid.NewGuid(), Scope: "Retry", RetryJobId: original, RetryStatus: "Failed", Mode: "Artwork");
        fixture.Acceptance.ReadRetryModeAsync(original, Arg.Any<CancellationToken>()).Returns("Enrich");
        fixture.Acceptance.SnapshotScopeAsync(Arg.Any<ArtworkCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ErrorOrFactory.From(new ArtworkCompletionScopeSnapshot([new(1, "Snapshot")], [])));
        var afterRead = fixture.Clock.Now.AddTicks(1234567);
        var afterFlush = afterRead.AddTicks(7654321);
        fixture.Acceptance.ReadOwnersAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            fixture.Clock.Now = afterRead;
            return Task.FromResult<IReadOnlyList<ArtworkCompletionOwner>>([]);
        });
        var receipt = Substitute.For<IStagedArtworkCompletionAcceptance>();
        receipt.ReadAfterFlush().Returns(fixture.Request.RequestId);
        ArtworkCompletionJob? staged = null;
        fixture.Acceptance.AddJobStaged(Arg.Any<ArtworkCompletionJob>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<ArtworkCompletionPlannedItem>>())
            .Returns(call => { staged = call.Arg<ArtworkCompletionJob>(); return receipt; });
        fixture.Work.FlushAsync(Arg.Any<CancellationToken>()).Returns(_ => { fixture.Clock.Now = afterFlush; return Task.CompletedTask; });

        (await fixture.HandleAsync()).Value.ShouldBe(fixture.Request.RequestId);

        staged!.Mode.ShouldBe("Enrich");
        staged.CreatedAt.ShouldBe(afterRead);
        await fixture.Acceptance.Received(1).InsertItemsStagedAsync(receipt, afterFlush, Arg.Any<CancellationToken>());
        await fixture.Acceptance.Received(1).AttachRetryStagedAsync(receipt, original, Arg.Any<CancellationToken>());
        await fixture.Acceptance.Received(1).SnapshotScopeAsync(Arg.Is<ArtworkCompletionRequest>(request => request.Mode == "Enrich"), Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            _ = fixture.Acceptance.ReadRetryModeAsync(original, Arg.Any<CancellationToken>());
            _ = fixture.Work.BeginTransactionAsync(Arg.Any<CancellationToken>());
            _ = fixture.Acceptance.LockRequestAsync(fixture.Request.RequestId, Arg.Any<CancellationToken>());
            _ = fixture.Acceptance.LockOwnersAsync(Arg.Any<CancellationToken>());
            _ = fixture.Work.FlushAsync(Arg.Any<CancellationToken>());
            receipt.ReadAfterFlush();
            _ = fixture.Acceptance.InsertItemsStagedAsync(receipt, afterFlush, Arg.Any<CancellationToken>());
            _ = fixture.Acceptance.AttachRetryStagedAsync(receipt, original, Arg.Any<CancellationToken>());
            _ = fixture.Transaction.CommitAsync(Arg.Any<CancellationToken>());
        });
    }

    private sealed class Fixture
    {
        public IArtworkCompletionAcceptance Acceptance { get; } = Substitute.For<IArtworkCompletionAcceptance>();
        public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        public Clock Clock { get; } = new();
        public ArtworkCompletionRequest Request { get; set; } = new(Guid.NewGuid(), [IdCoder.Encode(1)]);
        public Fixture() => Work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Transaction);
        public Task<ErrorOr<Guid>> HandleAsync() => new QueueArtworkCompletionCommandHandler(Acceptance, Work, Clock,
            NullLogger<QueueArtworkCompletionCommandHandler>.Instance).HandleAsync(new(Request, Guid.NewGuid()));
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
