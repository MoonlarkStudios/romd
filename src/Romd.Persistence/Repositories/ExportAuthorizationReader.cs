using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Export;
using Romd.Domain.Libraries;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class ExportAuthorizationReader(RomdDbContext context) : IExportAuthorizationReader
{
    public async Task<ExportScope.Library?> GetEffectiveLibraryScopeAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        var row = await (
                from user in context.Users.AsNoTracking()
                where user.Id == userId && user.LibraryId != null
                join library in context.Libraries.AsNoTracking()
                    on user.LibraryId equals library.Id
                where library.MaterializationGeneration >= 0
                select new
                {
                    library.Id,
                    library.ConfigurationJson,
                    library.ConfigurationState,
                    library.ConfigurationError,
                    library.NeedsMaterialization,
                    library.MaterializationGeneration
                })
            .SingleOrDefaultAsync(ct);

        if (row is null || row.NeedsMaterialization)
        {
            return null;
        }

        var configuration = LibraryEntity.DeserializeConfig(
            row.ConfigurationJson,
            row.ConfigurationState,
            row.ConfigurationError);

        return configuration.State == LibraryConfigurationState.Valid
            ? new ExportScope.Library(row.Id, row.MaterializationGeneration)
            : null;
    }

    public async Task<ExportScope.Library?> GetCurrentLibraryScopeAsync(
        int libraryId,
        CancellationToken ct = default)
    {
        var row = await context.Libraries
            .AsNoTracking()
            .Where(library =>
                library.Id == libraryId &&
                !library.NeedsMaterialization &&
                library.MaterializationGeneration >= 0)
            .Select(library => new
            {
                library.Id,
                library.ConfigurationJson,
                library.ConfigurationState,
                library.ConfigurationError,
                library.MaterializationGeneration
            })
            .SingleOrDefaultAsync(ct);

        if (row is null)
        {
            return null;
        }

        var configuration = LibraryEntity.DeserializeConfig(
            row.ConfigurationJson,
            row.ConfigurationState,
            row.ConfigurationError);

        return configuration.State == LibraryConfigurationState.Valid
            ? new ExportScope.Library(row.Id, row.MaterializationGeneration)
            : null;
    }
}
