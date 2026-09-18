using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.MoveGame;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class MoveGameCommandHandlerTests
{
    private readonly ICatalogProjectionService _catalogProjection = Substitute.For<ICatalogProjectionService>();
    private readonly ILibraryRepository _libraryRepository = Substitute.For<ILibraryRepository>();
    private readonly ITitleSourceAssignmentStore _titleSourceAssignments = Substitute.For<ITitleSourceAssignmentStore>();
    private readonly ITitleRepository _titleRepository = Substitute.For<ITitleRepository>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public MoveGameCommandHandlerTests()
    {
        _unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        _catalogProjection.RebuildPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Fact]
    public async Task HandleAsync_ExistingTarget_UsesAssignmentPortInsideExistingTransactionFlow()
    {
        var operationOrder = new List<string>();
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 101));
        _titleRepository.GetByIdAsync(202, Arg.Any<CancellationToken>()).Returns(NewTitle(202, 7, "Target"));
        _titleRepository.HasGamesAsync(101, Arg.Any<CancellationToken>()).Returns(true);
        _titleSourceAssignments
            .When(store => store.UpsertAssignmentsAsync(
                Arg.Any<IReadOnlyList<TitleSourceAssignment>>(), Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("assignment"));
        _transaction
            .When(transaction => transaction.CommitAsync(Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("commit"));

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(41, 202, null));

        result.IsError.ShouldBeFalse();
        await _titleSourceAssignments.Received(1).UpsertAssignmentsAsync(
            Arg.Is<IReadOnlyList<TitleSourceAssignment>>(assignments =>
                assignments.Count == 1 &&
                assignments[0].SourceEntryId == 41 &&
                assignments[0].TitleId == 202),
            Arg.Any<CancellationToken>());
        await _transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        operationOrder.ShouldBe(["assignment", "commit"]);
        await _catalogProjection.Received(1).RebuildPlatformAsync(7, Arg.Any<CancellationToken>());
        await _libraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ExistingLocallyBackedTarget_ResponseCarriesCommittedPayloadFact()
    {
        var operationOrder = new List<string>();
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 101));
        _titleRepository.GetByIdAsync(202, Arg.Any<CancellationToken>())
            .Returns(NewTitle(202, 7, "Target"));
        _titleRepository.HasGamesAsync(101, Arg.Any<CancellationToken>()).Returns(true);
        _titleRepository.HasLocalPayloadAsync(202, Arg.Any<CancellationToken>()).Returns(call =>
        {
            operationOrder.Add("read-payload");
            return true;
        });
        _transaction.When(transaction => transaction.CommitAsync(Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("commit"));

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(41, 202, null));

        result.IsError.ShouldBeFalse();
        result.Value.NewTitle.ShouldNotBeNull();
        result.Value.NewTitle.HasLocalPayload.ShouldBeTrue();
        operationOrder.ShouldBe(["read-payload", "commit"]);
    }

    [Fact]
    public async Task HandleAsync_NewUnavailableTarget_ResponseCarriesFalsePayloadFact()
    {
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 101));
        _titleRepository.GetByNormalizedNameAsync(7, "newtitle", Arg.Any<CancellationToken>())
            .Returns((Title?)null);
        _titleRepository.AddAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>())
            .Returns(NewTitle(303, 7, "New Title"));
        _titleRepository.HasGamesAsync(101, Arg.Any<CancellationToken>()).Returns(true);
        _titleRepository.HasLocalPayloadAsync(303, Arg.Any<CancellationToken>()).Returns(false);

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(41, null, "New Title"));

        result.IsError.ShouldBeFalse();
        result.Value.NewTitle.ShouldNotBeNull();
        result.Value.NewTitle.HasLocalPayload.ShouldBeFalse();
        result.Value.TitleCreated.ShouldBeTrue();
    }

    [Fact]
    public async Task HandleAsync_MissingSourceEntry_DoesNotBeginTransaction()
    {
        _titleSourceAssignments.GetAssignmentContextAsync(999, Arg.Any<CancellationToken>())
            .Returns((TitleSourceAssignmentContext?)null);

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(999, null, null));

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
        var expectedError = CatalogErrors.GamePlatformRequired(41);

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(41, 202, null));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(expectedError.Code);
        result.FirstError.Description.ShouldBe(expectedError.Description);
        result.FirstError.Type.ShouldBe(expectedError.Type);
        await _unitOfWork.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _titleSourceAssignments.DidNotReceive().UpsertAssignmentsAsync(
            Arg.Any<IReadOnlyList<TitleSourceAssignment>>(), Arg.Any<CancellationToken>());
        await _titleSourceAssignments.DidNotReceive().ClearAssignmentsAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
        await _titleRepository.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _titleRepository.DidNotReceive().GetByNormalizedNameAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _titleRepository.DidNotReceive().AddAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>());
        await _titleRepository.DidNotReceive().DeleteOrRetainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _catalogProjection.DidNotReceive().RebuildPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _libraryRepository.DidNotReceive()
            .FlagForRematerializationByPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnroutedSourceEntryWithInvalidParameters_PreservesParameterValidationPrecedence()
    {
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, null, 101));

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(41, 202, "New Title"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(CatalogErrors.InvalidMoveParameters().Code);
        await _unitOfWork.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_RebuildThrowsAfterCommit_ReportsDurableSuccess()
    {
        _titleSourceAssignments.GetAssignmentContextAsync(41, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(41, 7, 101));
        _titleRepository.GetByIdAsync(202, Arg.Any<CancellationToken>()).Returns(NewTitle(202, 7, "Target"));
        _titleRepository.HasGamesAsync(101, Arg.Any<CancellationToken>()).Returns(true);
        _catalogProjection.RebuildPlatformAsync(7, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("rebuild interrupted"));

        var result = await CreateHandler().HandleAsync(new MoveGameCommand(41, 202, null));

        result.IsError.ShouldBeFalse();
        await _catalogProjection.Received(1).MarkPlatformDirtyAsync(
            7,
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 101, 202 })));
        await _libraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(7, Arg.Any<CancellationToken>());
    }

    private MoveGameCommandHandler CreateHandler() =>
        new(TestSystemCatalog.Create(),
            _titleRepository,
            _titleSourceAssignments,
            _unitOfWork,
            _libraryRepository,
            _catalogProjection,
            NullLogger<MoveGameCommandHandler>.Instance);

    private static Title NewTitle(int id, int platformId, string name) =>
        Title.Rehydrate(
            id,
            platformId,
            name,
            name.ToLowerInvariant(),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            EnrichmentStatus.None,
            null,
            DateTimeOffset.UtcNow);
}
