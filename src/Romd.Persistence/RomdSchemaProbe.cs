using Microsoft.EntityFrameworkCore;

namespace Romd.Persistence;

/// <summary>
///     Reads the <c>romd</c> schema version the worker published. Absent table or row means the
///     worker has not provisioned this database yet.
/// </summary>
public interface IRomdSchemaProbe
{
    Task<int?> GetPublishedVersionAsync(CancellationToken cancellationToken);
}

public sealed class RomdSchemaProbe(RomdDbContext context) : IRomdSchemaProbe
{
    private const string VersionTable =
        $"{PostgreSqlConfiguration.SchemaName}.{PostgreSqlConfiguration.SchemaVersionTable}";

    public async Task<int?> GetPublishedVersionAsync(CancellationToken cancellationToken)
    {
        // PostgreSQL resolves every relation in FROM at parse time, so a missing table cannot be
        // filtered out inside the same statement; probe the catalog first to keep a not-yet-provisioned
        // database from logging a failed command on every readiness check.
        var exists = await context.Database
            .SqlQueryRaw<bool>($"""SELECT to_regclass('{VersionTable}') IS NOT NULL AS "Value" """)
            .ToListAsync(cancellationToken);
        if (exists is not [true])
        {
            return null;
        }

        var versions = await context.Database
            .SqlQueryRaw<int>($"""SELECT s.version AS "Value" FROM {VersionTable} s""")
            .ToListAsync(cancellationToken);
        return versions.Count == 1 ? versions[0] : null;
    }
}
