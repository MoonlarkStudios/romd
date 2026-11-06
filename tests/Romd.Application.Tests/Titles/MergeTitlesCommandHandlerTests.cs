using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.MergeTitles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed partial class MergeTitlesCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_SourceEntries_UsesAssignmentPortAndPreservesTransactionFlow()
    {
        var titles = Substitute.For<ITitleAggregateReader>();
        var titlesMerge = Substitute.For<ITitleMergeStore>();
        var titlesDetails = Substitute.For<ITitleDetailReader>();
        var trackedTitles = Substitute.For<ITrackedTitleRepository>();
        var assignments = Substitute.For<ITitleSourceAssignmentStore>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        var rematerialization = Substitute.For<IRematerializationScheduler>();
        var libraries = Substitute.For<ILibraryRepository>();
        var projection = Substitute.For<ICatalogProjectionService>();
        var source = NewTitle(101, 7, "Source");
        var target = NewTitle(202, 7, "Target");
        var operationOrder = new List<string>();

        titles.GetAggregateAsync(101, Arg.Any<CancellationToken>()).Returns(source);
        titles.GetAggregateAsync(202, Arg.Any<CancellationToken>()).Returns(target);
        titlesDetails.GetDetailAsync(202, Arg.Any<CancellationToken>()).Returns(new TitleDetailData
        {
            Id = 202,
            Revision = Guid.Empty,
            PlatformId = 7,
            Name = "Target",
            EnrichmentStatus = nameof(EnrichmentStatus.None),
            IsTracked = false
        });
        assignments.GetSourceEntryIdsByTitleAsync(101, Arg.Any<CancellationToken>()).Returns([41, 42]);
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        projection.RebuildPlatformAsync(7, Arg.Any<CancellationToken>()).Returns(true);
        assignments
            .When(store => store.UpsertAssignmentsAsync(
                Arg.Any<IReadOnlyList<TitleSourceAssignment>>(), Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("assignment"));
        unitOfWork.When(work => work.FlushAsync(Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("flush"));
        titlesMerge.When(store => store.DeleteAbsorbedSourceAsync(101, Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("delete-source"));
        rematerialization
            .When(value => value.EnqueueTitleAsync(202, Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("schedule"));
        transaction
            .When(value => value.CommitAsync(Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("commit"));

        var handler = new MergeTitlesCommandHandler(TestSystemCatalog.Create(),
            titles, titlesMerge, titlesDetails,
            trackedTitles,
            assignments,
            unitOfWork,
            rematerialization,
            libraries,
            projection,
            Options.Create(new EnrichmentOptions()),
            NullLogger<MergeTitlesCommandHandler>.Instance, clock: TimeProvider.System);

        var result = await handler.HandleAsync(new MergeTitlesCommand(101, 202, Current, Guid.Empty.ToString()));

        result.IsError.ShouldBeFalse();
        await assignments.Received(1).UpsertAssignmentsAsync(
            Arg.Is<IReadOnlyList<TitleSourceAssignment>>(values =>
                values.Count == 2 &&
                values[0].SourceEntryId == 41 &&
                values[0].TitleId == 202 &&
                values[1].SourceEntryId == 42 &&
                values[1].TitleId == 202),
            Arg.Any<CancellationToken>());
        await titlesMerge.Received(1).ApplyMergeStagedAsync(target, source, Arg.Any<CancellationToken>());
        await trackedTitles.Received(1).ConsolidateAsync(101, 202, Arg.Any<CancellationToken>());
        await titlesMerge.Received(1).DeleteAbsorbedSourceAsync(101, Arg.Any<CancellationToken>());
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        operationOrder.ShouldBe(["assignment", "flush", "delete-source", "schedule", "flush", "commit"]);
        await projection.Received(1).RebuildPlatformAsync(7, Arg.Any<CancellationToken>());
        await libraries.Received(1).FlagForRematerializationByPlatformAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_RebuildThrowsAfterCommit_ReportsDurableSuccess()
    {
        var titles = Substitute.For<ITitleAggregateReader>();
        var titlesMerge = Substitute.For<ITitleMergeStore>();
        var titlesDetails = Substitute.For<ITitleDetailReader>();
        var assignments = Substitute.For<ITitleSourceAssignmentStore>();
        var transaction = Substitute.For<ITransaction>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var libraries = Substitute.For<ILibraryRepository>();
        var projection = Substitute.For<ICatalogProjectionService>();
        var source = NewTitle(101, 7, "Source");
        var target = NewTitle(202, 7, "Target");
        titles.GetAggregateAsync(101, Arg.Any<CancellationToken>()).Returns(source);
        titles.GetAggregateAsync(202, Arg.Any<CancellationToken>()).Returns(target);
        titlesDetails.GetDetailAsync(202, Arg.Any<CancellationToken>()).Returns(new TitleDetailData
        {
            Id = 202,
            Revision = Guid.Empty,
            PlatformId = 7,
            Name = "Target",
            EnrichmentStatus = nameof(EnrichmentStatus.None),
            IsTracked = false
        });
        assignments.GetSourceEntryIdsByTitleAsync(101, Arg.Any<CancellationToken>()).Returns([41]);
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        projection.RebuildPlatformAsync(7, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("rebuild interrupted"));

        var handler = new MergeTitlesCommandHandler(TestSystemCatalog.Create(),
            titles, titlesMerge, titlesDetails,
            Substitute.For<ITrackedTitleRepository>(),
            assignments,
            unitOfWork,
            Substitute.For<IRematerializationScheduler>(),
            libraries,
            projection,
            Options.Create(new EnrichmentOptions()),
            NullLogger<MergeTitlesCommandHandler>.Instance, clock: TimeProvider.System);

        var result = await handler.HandleAsync(new MergeTitlesCommand(101, 202, Current, Guid.Empty.ToString()));

        result.IsError.ShouldBeFalse();
        await projection.Received(1).MarkPlatformDirtyAsync(
            7,
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 101, 202 })));
        await libraries.Received(1).FlagForRematerializationByPlatformAsync(7, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_RebuildRefresh_IsTheResponseOnlyAtTheMergesOwnRevision(bool writerCommittedAfter)
    {
        var titles = Substitute.For<ITitleAggregateReader>();
        var titlesDetails = Substitute.For<ITitleDetailReader>();
        var assignments = Substitute.For<ITitleSourceAssignmentStore>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var projection = Substitute.For<ICatalogProjectionService>();
        titles.GetAggregateAsync(101, Arg.Any<CancellationToken>()).Returns(NewTitle(101, 7, "Source"));
        titles.GetAggregateAsync(202, Arg.Any<CancellationToken>()).Returns(NewTitle(202, 7, "Target"));
        var merged = Guid.NewGuid();
        TitleDetailData Detail(Guid revision, string description) => new()
        {
            Id = 202,
            Revision = revision,
            PlatformId = 7,
            Name = "Target",
            Description = description,
            EnrichmentStatus = nameof(EnrichmentStatus.None),
            IsTracked = false
        };
        // The pre-commit read, then the post-rebuild refresh: regrouped at the same revision, or a later writer's.
        titlesDetails.GetDetailAsync(202, Arg.Any<CancellationToken>()).Returns(
            Detail(merged, "Merged"),
            writerCommittedAfter ? Detail(Guid.NewGuid(), "Later writer") : Detail(merged, "Regrouped"));
        assignments.GetSourceEntryIdsByTitleAsync(101, Arg.Any<CancellationToken>()).Returns([41]);
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<ITransaction>());
        projection.RebuildPlatformAsync(7, Arg.Any<CancellationToken>()).Returns(true);
        var handler = new MergeTitlesCommandHandler(TestSystemCatalog.Create(),
            titles, Substitute.For<ITitleMergeStore>(), titlesDetails,
            Substitute.For<ITrackedTitleRepository>(),
            assignments,
            unitOfWork,
            Substitute.For<IRematerializationScheduler>(),
            Substitute.For<ILibraryRepository>(),
            projection,
            Options.Create(new EnrichmentOptions()),
            NullLogger<MergeTitlesCommandHandler>.Instance, clock: TimeProvider.System);

        var result = await handler.HandleAsync(new MergeTitlesCommand(101, 202, Current, Guid.Empty.ToString()));

        result.IsError.ShouldBeFalse();
        result.Value.Revision.ShouldBe(merged.ToString());
        result.Value.Description.ShouldBe(writerCommittedAfter ? "Merged" : "Regrouped");
        await titlesDetails.Received(2).GetDetailAsync(202, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StaleTargetIfMatch_ReturnsPreconditionFailedBeforeStaging()
    {
        var (handler, merge) = CreateRejectingHandler(NewTitle(101, 7, "Source"), NewTitle(202, 7, "Target"));

        var result = await handler.HandleAsync(new MergeTitlesCommand(
            101, 202, RevisionPrecondition.For(Guid.NewGuid().ToString()), Guid.Empty.ToString()));

        result.FirstError.Code.ShouldBe("Titles.PreconditionFailed");
        ((int)result.FirstError.Type).ShouldBe(412);
        await merge.DidNotReceive().ApplyMergeStagedAsync(Arg.Any<Title>(), Arg.Any<Title>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StaleSourceBatchRevision_ReturnsConflictBeforeStaging()
    {
        var (handler, merge) = CreateRejectingHandler(NewTitle(101, 7, "Source"), NewTitle(202, 7, "Target"));

        var result = await handler.HandleAsync(new MergeTitlesCommand(101, 202, Current, Guid.NewGuid().ToString()));

        result.FirstError.Code.ShouldBe(CatalogErrors.MergeSourceChangedCode);
        result.FirstError.Type.ShouldBe(ErrorOr.ErrorType.Conflict);
        await merge.DidNotReceive().ApplyMergeStagedAsync(Arg.Any<Title>(), Arg.Any<Title>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, "Titles.MergeSourceChanged")]
    [InlineData(true, "Titles.PreconditionFailed")]
    public async Task HandleAsync_SnapshotChangesBeforeTheStagedWrite_ReportsTheTitleThatChanged(
        bool targetChanged, string expectedCode)
    {
        var target = NewTitle(202, 7, "Target");
        var (handler, merge, titles) = CreateHandler(NewTitle(101, 7, "Source"), target);
        merge.ApplyMergeStagedAsync(target, Arg.Any<Title>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new PersistenceConflictException(new InvalidOperationException("stale"))));
        // Both the initial read and conflict classification honor caller cancellation.
        using var request = new CancellationTokenSource();
        titles.GetAggregateAsync(202, request.Token)
            .Returns(target, targetChanged ? NewTitle(202, 7, "Target", Guid.NewGuid()) : NewTitle(202, 7, "Target"));

        var result = await handler.HandleAsync(
            new MergeTitlesCommand(101, 202, Current, Guid.Empty.ToString()), request.Token);

        result.FirstError.Code.ShouldBe(expectedCode);
    }

    /// <summary>The revision of a title rehydrated without one.</summary>
    private static readonly RevisionPrecondition Current = RevisionPrecondition.For(Guid.Empty.ToString());

    private static (MergeTitlesCommandHandler Handler, ITitleMergeStore Merge) CreateRejectingHandler(Title source, Title target)
    {
        var (handler, merge, _) = CreateHandler(source, target);
        return (handler, merge);
    }

    private static (MergeTitlesCommandHandler Handler, ITitleMergeStore Merge, ITitleAggregateReader Titles) CreateHandler(
        Title source, Title target, IUnitOfWork? providedUnitOfWork = null, TimeProvider? providedClock = null)
    {
        var titles = Substitute.For<ITitleAggregateReader>();
        var merge = Substitute.For<ITitleMergeStore>();
        var details = Substitute.For<ITitleDetailReader>();
        details.GetDetailAsync(target.Id, Arg.Any<CancellationToken>()).Returns(new TitleDetailData
        {
            Id = target.Id,
            Revision = target.Revision,
            PlatformId = target.PlatformId,
            Name = target.Name,
            EnrichmentStatus = nameof(EnrichmentStatus.None),
            IsTracked = false
        });
        var unitOfWork = providedUnitOfWork ?? Substitute.For<IUnitOfWork>();
        titles.GetAggregateAsync(source.Id, Arg.Any<CancellationToken>()).Returns(source);
        titles.GetAggregateAsync(target.Id, Arg.Any<CancellationToken>()).Returns(target);
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<ITransaction>());
        var handler = new MergeTitlesCommandHandler(TestSystemCatalog.Create(),
            titles, merge, details,
            Substitute.For<ITrackedTitleRepository>(),
            Substitute.For<ITitleSourceAssignmentStore>(),
            unitOfWork,
            Substitute.For<IRematerializationScheduler>(),
            Substitute.For<ILibraryRepository>(),
            Substitute.For<ICatalogProjectionService>(),
            Options.Create(new EnrichmentOptions()),
            NullLogger<MergeTitlesCommandHandler>.Instance, clock: providedClock ?? TimeProvider.System);
        return (handler, merge, titles);
    }

    private static Title NewTitle(int id, int platformId, string name, Guid revision = default) =>
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
            DateTimeOffset.UtcNow,
            revision: revision);
}
