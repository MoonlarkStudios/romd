using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Export;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class ExportRepository(
    RomdDbContext context,
    IConsumerReleaseSelector releaseSelector) : IExportRepository
{
    private static readonly string ValidConfigurationState = LibraryConfigurationState.Valid.ToString();

    public Task<IReadOnlyList<ExportFileData>> GetExportFilesAsync(
        ExportScope scope,
        CancellationToken ct = default) => scope switch
    {
        ExportScope.Library library => ReadLibraryFilesAsync(
            BuildLibraryExportQuery(
                library.LibraryId,
                library.MaterializationGeneration,
                titleId: null),
            ct),
        ExportScope.AllCatalog => ReadAllCatalogFilesAsync(titleId: null, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    public Task<IReadOnlyList<ExportFileData>> GetExportFilesForTitleAsync(
        int titleId,
        AuthorizedExportScope authorization,
        CancellationToken ct = default) => authorization.Scope switch
    {
        ExportScope.Library library => ReadLibraryFilesAsync(
            BuildLibraryExportQuery(
                library.LibraryId,
                library.MaterializationGeneration,
                titleId,
                authorization.EffectiveLibraryUserId),
            ct),
        ExportScope.AllCatalog when authorization.EffectiveLibraryUserId is null =>
            ReadAllCatalogFilesAsync(titleId, ct),
        ExportScope.AllCatalog => throw new ArgumentException(
            "All-catalog export cannot be authorized by a library-scoped user.",
            nameof(authorization)),
        _ => throw new ArgumentOutOfRangeException(nameof(authorization))
    };

    private IQueryable<LibraryExportRow> BuildLibraryExportQuery(
        int libraryId,
        long materializationGeneration,
        int? titleId,
        Guid? effectiveLibraryUserId = null)
    {
        // Candidate selection and file resolution must share one database statement. A
        // multi-query shape can observe different materialization/library generations and
        // return a torn subset. Both left joins are intentional: fileless candidates still
        // participate in the selector and can legitimately win over a candidate with files.
        return
            from release in context.MaterializedLibraryReleases
            where release.LibraryId == libraryId &&
                  (!titleId.HasValue || release.TitleId == titleId.Value) &&
                  release.IsExposed &&
                  release.IsOwned &&
                  release.CatalogReleaseId != null &&
                  context.Libraries.Any(library =>
                      library.Id == release.LibraryId &&
                      library.ConfigurationState == ValidConfigurationState &&
                      !library.NeedsMaterialization &&
                      library.MaterializationGeneration == materializationGeneration) &&
                  (!effectiveLibraryUserId.HasValue || context.Users.Any(user =>
                      user.Id == effectiveLibraryUserId.Value &&
                      user.LibraryId == release.LibraryId))
            join catalogRelease in context.CatalogReleases
                on release.CatalogReleaseId equals (int?)catalogRelease.Id
            join title in context.Titles on release.TitleId equals title.Id
            join platform in context.Platforms on release.PlatformId equals platform.Id
            join catalogFile in context.CatalogReleaseFiles
                on catalogRelease.Id equals catalogFile.CatalogReleaseId into catalogFiles
            from catalogFile in catalogFiles.DefaultIfEmpty()
            join romFile in context.RomFiles
                on catalogFile.Sha1 equals (Sha1?)romFile.Sha1 into romFiles
            from romFile in romFiles.DefaultIfEmpty()
            orderby release.TitleId
            select new LibraryExportRow(
                title.Id,
                title.Name,
                title.PlatformId,
                platform.Name,
                catalogRelease.Id,
                catalogRelease.Name,
                catalogRelease.Revision,
                catalogRelease.SizeBytes,
                release.IsComplete,
                release.IsPlayable,
                (int?)romFile.Id,
                (int?)romFile.FileId,
                romFile.OriginalFilename);
    }

    private async Task<IReadOnlyList<ExportFileData>> ReadLibraryFilesAsync(
        IQueryable<LibraryExportRow> scopedQuery,
        CancellationToken ct)
    {
        var files = new HashSet<ExportFileData>();
        var titleRows = new List<LibraryExportRow>();
        int? titleId = null;
        await foreach (var row in scopedQuery.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (titleId.HasValue && titleId.Value != row.TitleId)
            {
                AppendSelectedTitleFiles(titleRows, files);
                titleRows.Clear();
            }

            titleId = row.TitleId;
            titleRows.Add(row);
        }

        AppendSelectedTitleFiles(titleRows, files);
        return files
            .OrderBy(file => file.TitleId)
            .ThenBy(file => file.RomFileId)
            .ThenBy(file => file.FileId)
            .ThenBy(file => file.OriginalFilename, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<IReadOnlyList<ExportFileData>> ReadAllCatalogFilesAsync(
        int? titleId,
        CancellationToken ct)
    {
        var query = from title in context.Titles
            join platform in context.Platforms on title.PlatformId equals platform.Id
            join link in context.EffectiveTitleSourceLinks() on title.Id equals link.TitleId
            join game in context.DatGames on link.SourceEntryId equals game.SourceEntryId
            join rom in context.DatRoms on game.Id equals rom.DatGameId
            join romFile in context.RomFiles on rom.RomFileId equals romFile.Id
            where rom.RomFileId != null && (!titleId.HasValue || title.Id == titleId.Value)
            select new ExportFileData(
                title.Id,
                title.Name,
                title.PlatformId,
                platform.Name,
                romFile.Id,
                romFile.FileId,
                romFile.OriginalFilename);

        return await query
            .Distinct()
            .ToListAsync(ct);
    }

    private void AppendSelectedTitleFiles(
        IReadOnlyList<LibraryExportRow> rows,
        HashSet<ExportFileData> files)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var selected = releaseSelector.SelectDefault(
            rows
                .GroupBy(row => new ReleaseSelectionKey(
                    row.ReleaseId,
                    row.ReleaseName,
                    row.Revision,
                    row.SizeBytes,
                    row.IsComplete,
                    row.IsPlayable))
                .Select(release => ToSelectionCandidate(release.Key))
                .ToList(),
            ConsumerReleasePreference.Default);
        if (selected is null)
        {
            return;
        }

        foreach (var row in rows.Where(row => row.ReleaseId == selected.Id && row.RomFileId.HasValue))
        {
            files.Add(new ExportFileData(
                row.TitleId,
                row.TitleName,
                row.PlatformId,
                row.PlatformName,
                row.RomFileId!.Value,
                row.FileId!.Value,
                row.OriginalFilename!));
        }
    }

    private static ConsumerReleaseSelectionCandidate ToSelectionCandidate(ReleaseSelectionKey row) =>
        new(
            new ConsumerReleaseData
            {
                Id = row.ReleaseId,
                Name = row.Name,
                Revision = row.Revision,
                Regions = [],
                Languages = [],
                SizeBytes = row.SizeBytes,
                IsComplete = row.IsComplete
            },
            row.IsPlayable,
            [],
            []);

    private sealed record ReleaseSelectionKey(
        int ReleaseId,
        string Name,
        string? Revision,
        long SizeBytes,
        bool IsComplete,
        bool IsPlayable);

    private sealed record LibraryExportRow(
        int TitleId,
        string TitleName,
        int PlatformId,
        string PlatformName,
        int ReleaseId,
        string ReleaseName,
        string? Revision,
        long SizeBytes,
        bool IsComplete,
        bool IsPlayable,
        int? RomFileId,
        int? FileId,
        string? OriginalFilename);
}
