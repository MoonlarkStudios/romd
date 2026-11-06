using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;
using Romd.Domain.ReferenceData;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class LibraryExperienceRepositoryTests
{
    [Theory]
    [InlineData("matching", null)]
    [InlineData("added", null)]
    [InlineData("removed", null)]
    [InlineData("excluded", null)]
    [InlineData("all", null)]
    [InlineData("matching", "gAmE 05")]
    [InlineData("added", "gAmE 05")]
    [InlineData("removed", "gAmE 05")]
    [InlineData("excluded", "gAmE 05")]
    [InlineData("all", "gAmE 05")]
    public async Task DraftEvaluation_MultipleBatches_PreservesExactCountsViewsAndKeysetPages(string view, string? query)
    {
        const int titleCount = 600;
        await SeedEvaluationTitlesAsync(titleCount);
        await using var context = CreateContext();
        var provider = new BoundedEvaluationCandidates();
        var evaluator = EvaluationService(context, provider);
        var configuration = DraftEvaluationConfiguration();
        var observed = new List<int>();
        string? cursor = null;

        do
        {
            int before = provider.Batches.Count;
            var result = await evaluator.EvaluateAsync(1, configuration, view, query, 0, default, cursor, limit: 97);

            result.IsError.ShouldBeFalse();
            result.Value.SavedCount.ShouldBe(300);
            result.Value.MatchingCount.ShouldBe(300);
            result.Value.AddedCount.ShouldBe(150);
            result.Value.RemovedCount.ShouldBe(150);
            result.Value.RemovalReasons.ShouldHaveSingleItem().ShouldBe(new("Untracked", 150));
            var pageIds = result.Value.Page.Items.Select(item => DecodeEvaluationId(item.Id)).ToArray();
            observed.AddRange(pageIds);
            result.Value.Page.Items.ShouldAllBe(item => item.PlatformName == "NES");
            foreach (var item in result.Value.Page.Items)
            {
                int id = DecodeEvaluationId(item.Id);
                item.WasEligible.ShouldBe(id % 4 is 0 or 2);
                item.IsEligible.ShouldBe(id % 4 is 1 or 2);
                item.IsOwned.ShouldBe(item.IsEligible);
                item.IsPlayable.ShouldBe(item.IsEligible);
                item.Reason.ShouldBe((id % 4) switch { 0 => "Untracked", 3 => "NoLinkedRelease", _ => null });
                item.RatingCategory.ShouldBe(id % 4 == 1 ? "T" : "E");
            }
            var batches = provider.Batches.Skip(before).ToArray();
            batches.Length.ShouldBe(3);
            batches.ShouldAllBe(batch => batch.Length > 0 && batch.Length <= 256);
            batches.SelectMany(batch => batch).ShouldBe(Enumerable.Range(1, titleCount));
            result.Value.Page.HasNextPage.ShouldBe(result.Value.Page.NextCursor is not null);
            cursor = result.Value.Page.NextCursor;
        } while (cursor is not null);

        observed.ShouldBe(Enumerable.Range(1, titleCount).Where(id => MatchesEvaluationView(id, view))
            .Where(id => query is null || $"Game {id:D4}".Contains(query, StringComparison.OrdinalIgnoreCase)));
        observed.Distinct().Count().ShouldBe(observed.Count);
        context.ChangeTracker.Entries().ShouldBeEmpty();
        context.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task DraftEvaluation_ConcurrentChangesAfterFirstBatch_UsesOneSnapshotForAllCountsAndRows()
    {
        await SeedEvaluationTitlesAsync(600);
        var changed = false;
        var provider = new BoundedEvaluationCandidates(async batch =>
        {
            if (changed)
                return;
            changed = true;
            await using var writer = CreateContext();
            await writer.Titles.Where(title => title.Id > batch.Max()).ExecuteUpdateAsync(setters => setters
                .SetProperty(title => title.Name, "Changed after snapshot"));
            await writer.TrackedTitles.ExecuteDeleteAsync();
        });
        await using var context = CreateContext();

        var result = await EvaluationService(context, provider).EvaluateAsync(1, DraftEvaluationConfiguration(),
            "all", "Game 05", 0, default, limit: 100);

        result.IsError.ShouldBeFalse();
        result.Value.Page.Items.Count.ShouldBe(100);
        result.Value.Page.Items.ShouldAllBe(item => item.Name.StartsWith("Game 05", StringComparison.Ordinal));
        result.Value.SavedCount.ShouldBe(300);
        result.Value.MatchingCount.ShouldBe(300);
        // Candidate-free titles obtain tracking/rating state from the same snapshot as the first batch.
        result.Value.Page.Items.Where(item => DecodeEvaluationId(item.Id) % 4 == 3)
            .ShouldAllBe(item => item.Reason == "NoLinkedRelease");
        await using var verify = CreateContext();
        (await verify.Titles.Where(title => title.Id == 599).Select(title => title.Name).SingleAsync())
            .ShouldBe("Changed after snapshot");
        (await verify.TrackedTitles.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task DraftEvaluation_CursorChangesViewOrPolicy_RejectsBeforeLoadingCandidates()
    {
        await SeedEvaluationTitlesAsync(300);
        await using var context = CreateContext();
        var provider = new BoundedEvaluationCandidates();
        var evaluator = EvaluationService(context, provider);
        var configuration = DraftEvaluationConfiguration();
        var first = await evaluator.EvaluateAsync(1, configuration, "all", null, 0, default, limit: 1);
        first.Value.Page.NextCursor.ShouldNotBeNull();
        int reads = provider.Batches.Count;

        var changedView = await evaluator.EvaluateAsync(1, configuration, "matching", null, 0, default, first.Value.Page.NextCursor);
        var changedPolicy = await evaluator.EvaluateAsync(1, configuration with { IncludeUntrackedTitles = true },
            "all", null, 0, default, first.Value.Page.NextCursor);

        changedView.FirstError.Code.ShouldBe("Libraries.InvalidCursor");
        changedPolicy.FirstError.Code.ShouldBe("Libraries.InvalidCursor");
        provider.Batches.Count.ShouldBe(reads);
    }

    [Fact]
    public async Task DraftEvaluation_AfterIdBeyondFirstBatch_PreservesCatalogCountsAndReturnsOnlyLaterRows()
    {
        await SeedEvaluationTitlesAsync(600);
        await using var context = CreateContext();
        var provider = new BoundedEvaluationCandidates();

        var result = await EvaluationService(context, provider).EvaluateAsync(1, DraftEvaluationConfiguration(),
            "all", null, 510, default, limit: 100);

        result.IsError.ShouldBeFalse();
        result.Value.SavedCount.ShouldBe(300);
        result.Value.MatchingCount.ShouldBe(300);
        result.Value.AddedCount.ShouldBe(150);
        result.Value.RemovedCount.ShouldBe(150);
        result.Value.Page.Items.Select(item => DecodeEvaluationId(item.Id)).ShouldBe(Enumerable.Range(511, 90));
        result.Value.Page.HasNextPage.ShouldBeFalse();
        result.Value.Page.NextCursor.ShouldBeNull();
        provider.Batches.Count.ShouldBe(3);
    }

    [Fact]
    public async Task DraftEvaluation_CancelledAfterFirstBatch_StopsFurtherReadsAndDisposesSnapshot()
    {
        await SeedEvaluationTitlesAsync(600);
        using var cancellation = new CancellationTokenSource();
        var provider = new BoundedEvaluationCandidates(_ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });
        await using var context = CreateContext();

        await Should.ThrowAsync<OperationCanceledException>(() => EvaluationService(context, provider)
            .EvaluateAsync(1, DraftEvaluationConfiguration(), "all", null, 0, cancellation.Token));

        provider.Batches.ShouldHaveSingleItem();
        context.Database.CurrentTransaction.ShouldBeNull();
        context.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(256)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(0)] // Public constructor uses the production default.
    public async Task DraftEvaluation_BatchSizes_PreservesExactCountsAndBoundsCandidateScopes(int batchSize)
    {
        int size = batchSize == 0 ? LibraryDraftEvaluator.DefaultTitleBatchSize : batchSize;
        int count = size + 4;
        await SeedEvaluationTitlesAsync(count);
        await using var context = CreateContext();
        var provider = new BoundedEvaluationCandidates(maximumBatchSize: size);

        var result = await EvaluationService(context, provider, batchSize == 0 ? null : size)
            .EvaluateAsync(1, DraftEvaluationConfiguration(), "all", null, size - 2, default, limit: 100);

        result.IsError.ShouldBeFalse();
        result.Value.SavedCount.ShouldBe(count / 2);
        result.Value.MatchingCount.ShouldBe(count / 2);
        result.Value.AddedCount.ShouldBe(count / 4);
        result.Value.RemovedCount.ShouldBe(count / 4);
        result.Value.Page.Items.Select(item => DecodeEvaluationId(item.Id)).ShouldBe(Enumerable.Range(size - 1, 6));
        result.Value.Page.HasNextPage.ShouldBeFalse();
        provider.Batches.Select(batch => batch.Length).ShouldBe([size, 4]);
        provider.Batches.SelectMany(batch => batch).ShouldBe(Enumerable.Range(1, count));
        context.ChangeTracker.Entries().ShouldBeEmpty();
        context.Database.CurrentTransaction.ShouldBeNull();
    }

    private static LibraryConfiguration DraftEvaluationConfiguration() => new()
    {
        ContentRatingPolicy = new() { MaxMinimumAge = 18 }
    };

    private static int DecodeEvaluationId(string value)
    {
        IdCoder.TryDecode(value, out int id).ShouldBeTrue();
        return id;
    }

    private static bool MatchesEvaluationView(int id, string view) => view switch
    {
        "matching" => id % 4 is 1 or 2,
        "added" => id % 4 == 1,
        "removed" => id % 4 == 0,
        "excluded" => id % 4 is 0 or 3,
        _ => true
    };

    private static LibraryDraftEvaluator EvaluationService(Romd.Persistence.RomdDbContext context, ILibraryCandidateReader provider, int? batchSize = 256)
    {
        var catalog = Substitute.For<ICatalogProjectionService>();
        catalog.GetPlatformIdsNeedingRebuildAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        return batchSize is { } size ? new(context, new LibraryRepository(context), provider, catalog, size)
            : new(context, new LibraryRepository(context), provider, catalog);
    }

    private async Task SeedEvaluationTitlesAsync(int count)
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "NES",
            Ownership = ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "NES",
            BaseCompactLabel = "NES",
            CanonicalKey = "nes",
            ShortName = "nes",
            CreatedAt = now
        });
        context.Libraries.Add(new LibraryEntity
        {
            Id = 1,
            Name = "Saved",
            ConfigurationJson = JsonSerializer.Serialize(new LibraryConfiguration
            {
                IncludeUntrackedTitles = true,
                ContentRatingPolicy = new() { MaxMinimumAge = 10 }
            }),
            CreatedAt = now
        });
        context.Titles.AddRange(Enumerable.Range(1, count).Select(id => new TitleEntity
        {
            Id = id,
            Name = $"Game {id:D4}",
            NormalizedName = $"game{id}",
            PlatformId = 1,
            EnrichmentStatus = "None",
            CreatedAt = now
        }));
        context.TrackedTitles.AddRange(Enumerable.Range(1, count).Where(id => id % 4 != 0)
            .Select(id => new TrackedTitleEntity { TitleId = id, CreatedAt = now, UpdatedAt = now }));
        context.TitleContentRatings.AddRange(Enumerable.Range(1, count).Select(id => new TitleContentRatingEntity
        {
            TitleId = id,
            Board = (int)RatingBoard.Esrb,
            Code = id % 4 == 1 ? "T" : "E",
            MinimumAge = id % 4 == 1 ? 13 : 0,
            Designation = (int)RatingDesignation.Rated,
            SourceId = "user",
            CreatedAt = now
        }));
        await context.SaveChangesAsync();
    }

    private sealed class BoundedEvaluationCandidates(Func<int[], Task>? onRead = null, int maximumBatchSize = 256) : ILibraryCandidateReader
    {
        public List<int[]> Batches { get; } = [];

        public async Task<IReadOnlyList<TitleCandidates>> GetCandidatesAsync(LibraryConfiguration config, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            config.IncludeUntrackedTitles.ShouldBeTrue();
            config.TitleSelectionMode.ShouldBe(LibraryTitleSelectionMode.IncludeOnly);
            var ids = config.IncludeTitleIds.ToArray();
            ids.Length.ShouldBeInRange(1, maximumBatchSize);
            ids.ShouldAllBe(id => id > 0);
            Batches.Add(ids);
            if (onRead is not null)
                await onRead(ids);
            return ids.Where(id => id % 4 != 3).Select(id => new TitleCandidates(id, 1, null,
                [new ContentRating
                {
                    Board = RatingBoard.Esrb, Code = id % 4 == 1 ? "T" : "E", MinimumAge = id % 4 == 1 ? 13 : 0,
                    Designation = RatingDesignation.Rated, SourceId = "user"
                }], [new GameCandidate(id, id, 1, null, true, true, [1], [], [])], id % 4 != 0)).ToArray();
        }
    }
}
