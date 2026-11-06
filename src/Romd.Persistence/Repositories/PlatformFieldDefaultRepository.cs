using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Platform;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class PlatformFieldDefaultRepository(RomdDbContext db) : IPlatformFieldDefaultRepository
{
    public Task LockAsync(int platformId, CancellationToken cancellationToken = default) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(1380928837, {platformId})", cancellationToken);

    public Task<int> CountTitlesAsync(int platformId, CancellationToken cancellationToken = default) =>
        db.Titles.CountAsync(title => title.PlatformId == platformId, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetByPlatformIdAsync(
        int platformId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.PlatformFieldDefaults.Where(e => e.PlatformId == platformId)
            .OrderByDescending(e => e.UpdatedAt).ThenByDescending(e => e.Id).ToListAsync(cancellationToken);
        return rows.GroupBy(e => e.FieldName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => MetadataPolicy.Fields.FirstOrDefault(field =>
                string.Equals(field, group.Key, StringComparison.OrdinalIgnoreCase)) ?? group.Key,
                group => group.First().SourceId, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetStagedAsync(int platformId, string fieldName, string sourceId,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        string normalized = fieldName.ToLowerInvariant();
        if (await db.PlatformFieldDefaults.AnyAsync(
                row => row.PlatformId == platformId && row.FieldName.ToLower() == normalized, cancellationToken))
            throw new InvalidOperationException("Clear and flush the persisted field default before setting its replacement.");

        db.PlatformFieldDefaults.RemoveRange(PendingDefaults(platformId, fieldName));
        db.PlatformFieldDefaults.Add(new PlatformFieldDefaultEntity
        {
            PlatformId = platformId,
            FieldName = fieldName,
            SourceId = sourceId
        });
    }

    public async Task ClearStagedAsync(int platformId, string fieldName,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        string normalized = fieldName.ToLowerInvariant();
        var existing = await db.PlatformFieldDefaults.AsTracking()
            .Where(e => e.PlatformId == platformId && e.FieldName.ToLower() == normalized)
            .ToListAsync(cancellationToken);
        db.PlatformFieldDefaults.RemoveRange(existing.Concat(PendingDefaults(platformId, fieldName)));
    }

    private PlatformFieldDefaultEntity[] PendingDefaults(int platformId, string fieldName) =>
        db.ChangeTracker.Entries<PlatformFieldDefaultEntity>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.PlatformId == platformId &&
                string.Equals(entry.Entity.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Entity).ToArray();

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Platform field default writes require the caller's transaction.");
    }
}
