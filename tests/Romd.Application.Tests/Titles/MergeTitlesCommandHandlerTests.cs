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
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles;

public sealed class MergeTitlesCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_SourceEntries_UsesAssignmentPortAndPreservesTransactionFlow()
    {
        var titles = Substitute.For<ITitleRepository>();
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

        titles.GetForMergeAsync(101, Arg.Any<CancellationToken>()).Returns(source);
        titles.GetForMergeAsync(202, Arg.Any<CancellationToken>()).Returns(target);
        titles.GetTitleDetailAsync(202, Arg.Any<CancellationToken>()).Returns(new TitleDetailData
        {
            Id = 202,
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
        rematerialization
            .When(value => value.EnqueueTitleAsync(202, Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("schedule"));
        transaction
            .When(value => value.CommitAsync(Arg.Any<CancellationToken>()))
            .Do(_ => operationOrder.Add("commit"));

        var handler = new MergeTitlesCommandHandler(TestSystemCatalog.Create(),
            titles,
            trackedTitles,
            assignments,
            unitOfWork,
            rematerialization,
            libraries,
            projection,
            Options.Create(new EnrichmentOptions()),
            NullLogger<MergeTitlesCommandHandler>.Instance);

        var result = await handler.HandleAsync(new MergeTitlesCommand(101, 202));

        result.IsError.ShouldBeFalse();
        await assignments.Received(1).UpsertAssignmentsAsync(
            Arg.Is<IReadOnlyList<TitleSourceAssignment>>(values =>
                values.Count == 2 &&
                values[0].SourceEntryId == 41 &&
                values[0].TitleId == 202 &&
                values[1].SourceEntryId == 42 &&
                values[1].TitleId == 202),
            Arg.Any<CancellationToken>());
        await titles.Received(1).UpdateAsync(target, Arg.Any<CancellationToken>());
        await trackedTitles.Received(1).ConsolidateAsync(101, 202, Arg.Any<CancellationToken>());
        await titles.Received(1).DeleteAsync(101, Arg.Any<CancellationToken>());
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        operationOrder.ShouldBe(["assignment", "schedule", "commit"]);
        await projection.Received(1).RebuildPlatformAsync(7, Arg.Any<CancellationToken>());
        await libraries.Received(1).FlagForRematerializationByPlatformAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_RebuildThrowsAfterCommit_ReportsDurableSuccess()
    {
        var titles = Substitute.For<ITitleRepository>();
        var assignments = Substitute.For<ITitleSourceAssignmentStore>();
        var transaction = Substitute.For<ITransaction>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var libraries = Substitute.For<ILibraryRepository>();
        var projection = Substitute.For<ICatalogProjectionService>();
        var source = NewTitle(101, 7, "Source");
        var target = NewTitle(202, 7, "Target");
        titles.GetForMergeAsync(101, Arg.Any<CancellationToken>()).Returns(source);
        titles.GetForMergeAsync(202, Arg.Any<CancellationToken>()).Returns(target);
        titles.GetTitleDetailAsync(202, Arg.Any<CancellationToken>()).Returns(new TitleDetailData
        {
            Id = 202,
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
            titles,
            Substitute.For<ITrackedTitleRepository>(),
            assignments,
            unitOfWork,
            Substitute.For<IRematerializationScheduler>(),
            libraries,
            projection,
            Options.Create(new EnrichmentOptions()),
            NullLogger<MergeTitlesCommandHandler>.Instance);

        var result = await handler.HandleAsync(new MergeTitlesCommand(101, 202));

        result.IsError.ShouldBeFalse();
        await projection.Received(1).MarkPlatformDirtyAsync(
            7,
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 101, 202 })));
        await libraries.Received(1).FlagForRematerializationByPlatformAsync(7, Arg.Any<CancellationToken>());
    }

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
