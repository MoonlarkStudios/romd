using Romd.Domain.Libraries;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Extensions;

/// <summary>
///     Extension methods for applying Library filtering to EF Core queries
///     using the current materialized title and release projections.
/// </summary>
public static class LibraryFiltering
{
    private static readonly string ValidConfigurationState = LibraryConfigurationState.Valid.ToString();

    /// <summary>
    ///     Applies Library filtering to a Title query via accessible title projection rows.
    /// </summary>
    public static IQueryable<TitleEntity> ApplyLibrary(
        this IQueryable<TitleEntity> query,
        int? libraryId,
        IQueryable<MaterializedLibraryTitleEntity> materializedTitles,
        IQueryable<LibraryEntity> libraries)
    {
        if (libraryId is null)
        {
            return query;
        }

        int id = libraryId.Value;
        var scopedTitles = ApplyValidLibraryScope(materializedTitles, id, libraries);
        return query.Where(t =>
            scopedTitles.Any(m => m.TitleId == t.Id && m.ExposedReleaseCount > 0));
    }

    /// <summary>
    ///     Applies Library filtering to a DatGame query via accessible release projection rows.
    /// </summary>
    public static IQueryable<DatGameEntity> ApplyLibrary(
        this IQueryable<DatGameEntity> query,
        int? libraryId,
        IQueryable<MaterializedLibraryReleaseEntity> materializedReleases,
        IQueryable<LibraryEntity> libraries)
    {
        if (libraryId is null)
        {
            return query;
        }

        int id = libraryId.Value;
        var scopedReleases = ApplyValidLibraryScope(materializedReleases, id, libraries);
        return query.Where(g =>
            scopedReleases.Any(release => release.DatGameId == g.Id && release.IsExposed));
    }

    private static IQueryable<MaterializedLibraryTitleEntity> ApplyValidLibraryScope(
        IQueryable<MaterializedLibraryTitleEntity> materializedTitles,
        int libraryId,
        IQueryable<LibraryEntity> libraries)
    {
        var scopedTitles = materializedTitles.Where(title => title.LibraryId == libraryId);
        return scopedTitles.Where(title => libraries.Any(library =>
                library.Id == title.LibraryId &&
                library.ConfigurationState == ValidConfigurationState));
    }

    private static IQueryable<MaterializedLibraryReleaseEntity> ApplyValidLibraryScope(
        IQueryable<MaterializedLibraryReleaseEntity> materializedReleases,
        int libraryId,
        IQueryable<LibraryEntity> libraries)
    {
        var scopedReleases = materializedReleases.Where(release => release.LibraryId == libraryId);
        return scopedReleases.Where(release => libraries.Any(library =>
                library.Id == release.LibraryId &&
                library.ConfigurationState == ValidConfigurationState));
    }
}
