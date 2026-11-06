using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.MoveGame;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class MoveGameCommandHandlerTests
{
    private readonly ICatalogProjectionService _catalogProjection = Substitute.For<ICatalogProjectionService>();
    private readonly ILibraryRepository _libraryRepository = Substitute.For<ILibraryRepository>();
    private readonly ITitleSourceAssignmentStore _titleSourceAssignments = Substitute.For<ITitleSourceAssignmentStore>();
    private readonly ITitleCreationStore _titleCreation = Substitute.For<ITitleCreationStore>();
    private readonly ITitleOrphanCleanup _orphanCleanup = Substitute.For<ITitleOrphanCleanup>();
    private readonly ITitleReader _titleReader = Substitute.For<ITitleReader>();
    private readonly ITitleRowLock _titleLock = Substitute.For<ITitleRowLock>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public MoveGameCommandHandlerTests()
    {
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        _catalogProjection.RebuildPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _titleLock.LockAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _titleReader.GetSummaryAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Summary(call.Arg<int>(), 7, "Existing title"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HandleAsync_PrimaryAndCleanupFail_PreservesExactFailureWithoutRebuild(bool commit, bool canceled)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = canceled ? new OperationCanceledException(cancellation.Token) : new IOException("primary mutation failure");
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>()).Returns(new TitleSourceAssignmentContext(41, 7, 101));
        _transaction.DisposeAsync().Returns(_ => ValueTask.FromException(new IOException("secondary cleanup")));
        if (commit)
            _transaction.When(value => value.CommitAsync(Arg.Any<CancellationToken>())).Do(_ => throw primary);
        else
            _titleSourceAssignments.When(value => value.UpsertAssignmentsAsync(Arg.Any<IReadOnlyList<TitleSourceAssignment>>(), Arg.Any<CancellationToken>())).Do(_ => throw primary);

        Exception? observed = null;
        try
        { await CreateHandler().HandleAsync(new(101, 41, 202, null), cancellation.Token); }
        catch (Exception error) { observed = error; }

        observed.ShouldBeSameAs(primary);
        await _transaction.Received(1).DisposeAsync();
        await _catalogProjection.DidNotReceiveWithAnyArgs().RebuildPlatformAsync(default, default);
        await _transaction.Received(commit ? 1 : 0).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MissingPathTitle_PrecedesMissingGame()
    {
        _titleReader.GetSummaryAsync(101, Arg.Any<CancellationToken>()).Returns((TitleSummary?)null);
        var result = await CreateHandler().HandleAsync(new MoveGameCommand(101, 999, null, null));
        result.FirstError.Code.ShouldBe("Catalog.TitleNotFound");
        await _titleSourceAssignments.DidNotReceive().GetAssignmentContextAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_GameUnderAnotherTitle_ReturnsNotFound()
    {
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 303));

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(101, 41, 202, null));

        result.FirstError.Code.ShouldBe("Catalog.GameNotFound");
        await _titleLock.DidNotReceive().LockAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_LocksBothTitlesInIdOrderAndRechecksTheGameUnderTheLock()
    {
        var locked = new List<int>();
        _titleLock.LockAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                locked.Add(call.Arg<int>());
                return true;
            });
        // Another move takes the game while this one waits for the locks.
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 303), new TitleSourceAssignmentContext(41, 7, 404));

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(303, 41, 202, null));

        locked.ShouldBe([202, 303]);
        result.FirstError.Code.ShouldBe("Catalog.GameNotFound");
        await _titleSourceAssignments.DidNotReceive().UpsertAssignmentsAsync(
            Arg.Any<IReadOnlyList<TitleSourceAssignment>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ExistingTarget_UsesAssignmentPortInsideExistingTransactionFlow()
    {
        var operationOrder = new List<string>();
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 101));
        _titleReader.GetSummaryAsync(202, Arg.Any<CancellationToken>()).Returns(Summary(202, 7, "Target"));
        _orphanCleanup.HasSourceEntriesAsync(101, Arg.Any<CancellationToken>()).Returns(true);
        _titleSourceAssignments
            .When(store => store.UpsertAssignmentsAsync(
                Arg.Any<IReadOnlyList<TitleSourceAssignment>>(), Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("assignment"));
        _unitOfWork.When(work => work.FlushAsync(Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("flush"));
        _orphanCleanup.When(store => store.HasSourceEntriesAsync(101, Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("orphan-read"));
        _transaction
            .When(transaction => transaction.CommitAsync(Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("commit"));

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(101, 41, 202, null));

        result.IsError.ShouldBeFalse();
        await _titleSourceAssignments.Received(1).UpsertAssignmentsAsync(
            Arg.Is<IReadOnlyList<TitleSourceAssignment>>(assignments =>
                assignments.Count == 1 &&
                assignments[0].SourceEntryId == 41 &&
                assignments[0].TitleId == 202),
            Arg.Any<CancellationToken>());
        await _transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        operationOrder.ShouldBe(["assignment", "flush", "orphan-read", "commit"]);
        await _catalogProjection.Received(1).RebuildPlatformAsync(7, Arg.Any<CancellationToken>());
        await _libraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AssignmentFlushFails_DoesNotReadDependentStateOrCommit()
    {
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 101));
        _unitOfWork.FlushAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("Injected flush failure.")));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            CreateHandler().HandleAsync(new MoveGameCommand(101, 41, 202, null)));

        await _orphanCleanup.DidNotReceive().HasSourceEntriesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _catalogProjection.DidNotReceive().MarkPlatformDirtyAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyCollection<int>>());
        await _transaction.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
        await _transaction.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task HandleAsync_ExistingLocallyBackedTarget_ResponseCarriesCommittedPayloadFact()
    {
        var operationOrder = new List<string>();
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 101));
        _orphanCleanup.HasSourceEntriesAsync(101, Arg.Any<CancellationToken>()).Returns(true);
        // Validation and the response both read the summary; the response read carries the
        // projection-owned payload fact and must precede commit.
        _titleReader.GetSummaryAsync(202, Arg.Any<CancellationToken>()).Returns(call =>
        {
            operationOrder.Add("read-summary");
            return Summary(202, 7, "Target", hasLocalPayload: true);
        });
        _transaction.When(transaction => transaction.CommitAsync(Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("commit"));

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(101, 41, 202, null));

        result.IsError.ShouldBeFalse();
        result.Value.NewTitle.ShouldNotBeNull();
        result.Value.NewTitle.HasLocalPayload.ShouldBeTrue();
        operationOrder.ShouldBe(["read-summary", "read-summary", "commit"]);
    }

    [Fact]
    public async Task HandleAsync_NewUnavailableTarget_ResponseCarriesFalsePayloadFact()
    {
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 101));
        _titleCreation.ExistsWithNormalizedNameAsync(7, "newtitle", Arg.Any<CancellationToken>())
            .Returns(false);
        var creation = Substitute.For<IStagedTitleCreation>();
        creation.ReadIdAfterFlush().Returns(303);
        _titleCreation.AddStagedAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>())
            .Returns(creation);
        _orphanCleanup.HasSourceEntriesAsync(101, Arg.Any<CancellationToken>()).Returns(true);
        _titleReader.GetSummaryAsync(303, Arg.Any<CancellationToken>())
            .Returns(Summary(303, 7, "New Title", hasLocalPayload: false));

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(101, 41, null, "New Title"));

        result.IsError.ShouldBeFalse();
        result.Value.NewTitle.ShouldNotBeNull();
        result.Value.NewTitle.HasLocalPayload.ShouldBeFalse();
        result.Value.TitleCreated.ShouldBeTrue();
    }

    [Fact]
    public async Task HandleAsync_NewTitle_FlushesBeforeReadingItsOwnIdentityAndAssigningTheGame()
    {
        var order = new List<string>();
        var createdAt = new DateTimeOffset(2026, 11, 4, 12, 34, 56, TimeSpan.Zero).AddTicks(1234560);
        var creation = Substitute.For<IStagedTitleCreation>();
        creation.ReadIdAfterFlush().Returns(_ => { order.Add("read-created-id"); return 303; });
        _titleCreation.AddStagedAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>()).Returns(creation);
        _titleCreation.When(store => store.AddStagedAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("stage-title"));
        _unitOfWork.When(work => work.FlushAsync(Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("flush"));
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 101));
        _titleSourceAssignments.When(store => store.UpsertAssignmentsAsync(
            Arg.Any<IReadOnlyList<TitleSourceAssignment>>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("stage-assignment"));

        var result = await CreateHandler(new FixedTimeProvider(createdAt))
            .HandleAsync(new MoveGameCommand(101, 41, null, "New Title"));

        result.IsError.ShouldBeFalse();
        order.ShouldBe(["stage-title", "flush", "read-created-id", "stage-assignment", "flush"]);
        await _titleCreation.Received(1).AddStagedAsync(Arg.Is<Title>(title => title.Id == 0 &&
            title.CreatedAt == createdAt && title.Name == "New Title" && title.NormalizedName == "newtitle"), Arg.Any<CancellationToken>());

    }

    [Fact]
    public async Task HandleAsync_MissingSourceEntry_DoesNotBeginTransaction()
    {
        _titleSourceAssignments.GetAssignmentContextAsync(999, Arg.Any<CancellationToken>())
            .Returns((TitleSourceAssignmentContext?)null);

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(101, 999, null, null));

        result.IsError.ShouldBeTrue();
        await _unitOfWork.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _titleSourceAssignments.DidNotReceive().UpsertAssignmentsAsync(
            Arg.Any<IReadOnlyList<TitleSourceAssignment>>(), Arg.Any<CancellationToken>());
        await _titleSourceAssignments.DidNotReceive().ClearAssignmentsAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnroutedSourceEntry_ReturnsPlatformRequiredWithoutMutation()
    {
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, null, 101));
        var expectedError = CatalogErrors.GamePlatformRequired();

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(101, 41, 202, null));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(expectedError.Code);
        result.FirstError.Description.ShouldBe(expectedError.Description);
        result.FirstError.Type.ShouldBe(expectedError.Type);
        await _unitOfWork.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _titleSourceAssignments.DidNotReceive().UpsertAssignmentsAsync(
            Arg.Any<IReadOnlyList<TitleSourceAssignment>>(), Arg.Any<CancellationToken>());
        await _titleSourceAssignments.DidNotReceive().ClearAssignmentsAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
        await _titleReader.Received(1).GetSummaryAsync(101, Arg.Any<CancellationToken>());
        await _titleReader.DidNotReceive().GetSummaryAsync(Arg.Is<int>(id => id != 101), Arg.Any<CancellationToken>());
        await _titleCreation.DidNotReceive().ExistsWithNormalizedNameAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _titleCreation.DidNotReceive().AddStagedAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>());
        await _orphanCleanup.DidNotReceive().DeleteOrRetainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _catalogProjection.DidNotReceive().RebuildPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _libraryRepository.DidNotReceive()
            .FlagForRematerializationByPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnroutedSourceEntryWithInvalidParameters_PreservesParameterValidationPrecedence()
    {
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, null, 101));

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(101, 41, 202, "New Title"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(CatalogErrors.InvalidMoveParameters().Code);
        await _unitOfWork.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_RebuildThrowsAfterCommit_ReportsDurableSuccess()
    {
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 101));
        _titleReader.GetSummaryAsync(202, Arg.Any<CancellationToken>()).Returns(Summary(202, 7, "Target"));
        _orphanCleanup.HasSourceEntriesAsync(101, Arg.Any<CancellationToken>()).Returns(true);
        _catalogProjection.RebuildPlatformAsync(7, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("rebuild interrupted"));

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(101, 41, 202, null));

        result.IsError.ShouldBeFalse();
        await _catalogProjection.Received(1).MarkPlatformDirtyAsync(
            7,
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 101, 202 })));
        await _libraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(7, Arg.Any<CancellationToken>());
    }

    private MoveGameCommandHandler CreateHandler(TimeProvider? clock = null) =>
        new(TestSystemCatalog.Create(),
            _titleCreation,
            _orphanCleanup,
            _titleReader,
            _titleLock,
            _titleSourceAssignments,
            _unitOfWork,
            _libraryRepository,
            _catalogProjection,
            NullLogger<MoveGameCommandHandler>.Instance, clock ?? TimeProvider.System);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static TitleSummary Summary(int id, int platformId, string name, bool hasLocalPayload = false) =>
        new(id, platformId, name, EnrichmentStatus.None, null, null, null, null, null, null, null,
            DateTimeOffset.UtcNow, null, hasLocalPayload);
}
