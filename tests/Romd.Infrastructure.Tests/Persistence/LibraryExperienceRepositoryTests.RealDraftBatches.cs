using Romd.Persistence.Libraries;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenIddict.EntityFrameworkCore;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Libraries;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class LibraryExperienceRepositoryTests
{
    [Fact]
    public async Task DraftEvaluation_RealCandidateBatchesWithDuplicateSources_PreservesCollapsedReleaseEligibility()
    {
        const int titleCount = 300;
        await SeedEvaluationTitlesAsync(titleCount);
        await SeedDuplicateEvaluationSourcesAsync(titleCount);
        var commands = new EvaluationScopeInterceptor();
        await using var context = new RomdDbContext(new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(database.ConnectionString).UseOpenIddict().AddInterceptors(commands).Options);
        var provider = new ObservedMaterializationCandidates(new MaterializationDataProvider(context));
        // The remaining source still asserts the canonical release; ownership and completeness
        // from its duplicate source must survive collapse even though that DAT is excluded.
        var configuration = DraftEvaluationConfiguration() with { ExcludedDatIds = [1] };

        var result = await EvaluationService(context, provider).EvaluateAsync(1, configuration,
            "all", null, 250, default, limit: 100);

        result.IsError.ShouldBeFalse();
        result.Value.SavedCount.ShouldBe(225);
        result.Value.MatchingCount.ShouldBe(225);
        result.Value.AddedCount.ShouldBe(75);
        result.Value.RemovedCount.ShouldBe(75);
        result.Value.RemovalReasons.ShouldHaveSingleItem().ShouldBe(new("Untracked", 75));
        result.Value.Page.Items.Select(item => DecodeEvaluationId(item.Id)).ShouldBe(Enumerable.Range(251, 50));
        result.Value.Page.HasNextPage.ShouldBeFalse();
        foreach (var item in result.Value.Page.Items)
        {
            int id = DecodeEvaluationId(item.Id);
            item.WasEligible.ShouldBe(id % 4 != 1);
            item.IsEligible.ShouldBe(id % 4 != 0);
            item.IsOwned.ShouldBe(item.IsEligible);
            item.IsPlayable.ShouldBe(item.IsEligible);
            item.Reason.ShouldBe(id % 4 == 0 ? "Untracked" : null);
        }

        provider.TitleCounts.ShouldBe([256, 44]);
        provider.CollapsedReleaseCounts.ShouldBe([256, 44]);
        commands.CandidateScopes.Count.ShouldBe(2);
        commands.CandidateScopes[0].ShouldBe(Enumerable.Range(1, 256));
        commands.CandidateScopes[1].ShouldBe(Enumerable.Range(257, 44));
        commands.ScopePredicateCounts.ShouldAllBe(count => count >= 4,
            "the title-id scope applies to the main candidate relation and ROM/region/language child aggregates");
        context.ChangeTracker.Entries().ShouldBeEmpty();
        context.Database.CurrentTransaction.ShouldBeNull();
    }

    private async Task SeedDuplicateEvaluationSourcesAsync(int titleCount)
    {
        await using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        context.Files.Add(new FileEntityPersistence
        {
            Id = 1,
            Sha256 = Sha256.Parse(new string('1', 64)),
            Size = 1,
            SizeOnDisk = 1,
            CreatedAt = now
        });
        context.StageOriginalRom(new OriginalRomFixture
        {
            Id = 1,
            OriginalFilename = "owned.nes",
            FileId = 1,
            Sha1 = Sha1.Parse(new string('1', 40)),
            Md5 = Md5.Parse(new string('1', 32)),
            Crc32 = Crc32.Parse("11111111"),
            CreatedAt = now
        });
        for (int source = 1; source <= 2; source++)
        {
            context.DatFiles.Add(new DatFileEntity
            {
                Id = source,
                Name = $"Source {source}",
                Description = "Duplicate release assertions",
                Type = "NoIntro",
                PlatformId = 1,
                OriginalFilename = $"source{source}.dat",
                FileId = 1,
                GameCount = titleCount,
                Source = new DatSourceEntity
                {
                    Id = source,
                    CatalogSource = new CatalogSourceEntity { Id = source, Kind = "Dat", Status = "Active" }
                },
                CreatedAt = now
            });
        }
        for (int titleId = 1; titleId <= titleCount; titleId++)
        {
            context.CatalogReleases.Add(new CatalogReleaseEntity
            {
                Id = titleId,
                PlatformId = 1,
                CatalogTitleId = titleId,
                Fingerprint = $"release-{titleId}",
                Name = $"Release {titleId}",
                SourceCount = 2,
                CreatedAt = now,
                UpdatedAt = now
            });
            for (int source = 1; source <= 2; source++)
            {
                int gameId = (titleId - 1) * 2 + source;
                context.SourceEntries.Add(new SourceEntryEntity
                {
                    Id = gameId,
                    CatalogSourceId = source,
                    EntryKey = $"title-{titleId}",
                    Name = $"Game {titleId:D4}",
                    PlatformId = 1,
                    HasLocalPayload = source == 1,
                    CreatedAt = now
                });
                context.DatGames.Add(new DatGameEntity
                {
                    Id = gameId,
                    DatFileId = source,
                    SourceEntryId = gameId,
                    Name = $"Game {titleId:D4}",
                    CreatedAt = now
                });
                context.TitleSourceLinks.Add(new TitleSourceLinkEntity
                {
                    SourceEntryId = gameId,
                    TitleId = titleId,
                    CreatedAt = now
                });
                context.CatalogReleaseSources.Add(new CatalogReleaseSourceEntity
                {
                    CatalogReleaseId = titleId,
                    SourceEntryId = gameId,
                    ProviderClaimKey = gameId.ToString(),
                    AssertedTitleId = titleId
                });
                context.DatRoms.Add(new DatRomEntity
                {
                    DatGameId = gameId,
                    Name = $"game{titleId}.nes",
                    Size = 1,
                    RomFileId = source == 1 ? 1 : null,
                    CreatedAt = now
                });
            }
        }
        await context.PublishOriginalRomsAsync();
        await context.SaveChangesAsync();
    }

    private sealed class ObservedMaterializationCandidates(ILibraryCandidateReader inner) : ILibraryCandidateReader
    {
        public List<int> TitleCounts { get; } = [];
        public List<int> CollapsedReleaseCounts { get; } = [];

        public async Task<IReadOnlyList<TitleCandidates>> GetCandidatesAsync(LibraryConfiguration config, CancellationToken ct = default)
        {
            var candidates = await inner.GetCandidatesAsync(config, ct);
            TitleCounts.Add(candidates.Count);
            foreach (var title in candidates)
            {
                title.Candidates.Count.ShouldBe(2);
                title.Candidates.Select(candidate => candidate.CatalogReleaseId).Distinct().ShouldHaveSingleItem()
                    .ShouldBe(title.TitleId);
            }
            var unrestricted = new LibraryConfiguration { IncludeUntrackedTitles = true };
            CollapsedReleaseCounts.Add(MaterializedLibraryProjectionBuilder.Build(1, candidates, unrestricted).Releases.Count);
            return candidates;
        }
    }

    private sealed class EvaluationScopeInterceptor : DbCommandInterceptor
    {
        public List<int[]> CandidateScopes { get; } = [];
        public List<int> ScopePredicateCounts { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"DatGameRegions\"", StringComparison.Ordinal) &&
                command.CommandText.Contains("\"DatGames\"", StringComparison.Ordinal))
            {
                var scopedIds = command.Parameters.Cast<DbParameter>()
                    .Single(parameter => parameter.Value is IEnumerable<int>);
                CandidateScopes.Add(((IEnumerable<int>)scopedIds.Value!).ToArray());
                ScopePredicateCounts.Add(command.CommandText.Split("@" + scopedIds.ParameterName.TrimStart('@'), StringSplitOptions.None).Length - 1);
            }
            return ValueTask.FromResult(result);
        }
    }
}
