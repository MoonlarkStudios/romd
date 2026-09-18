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

    public async Task SetAsync(int platformId, string fieldName, string sourceId,
        CancellationToken cancellationToken = default)
    {
        await ClearAsync(platformId, fieldName, cancellationToken);
        db.PlatformFieldDefaults.Add(new PlatformFieldDefaultEntity
        {
            PlatformId = platformId, FieldName = fieldName, SourceId = sourceId
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAsync(int platformId, string fieldName,
        CancellationToken cancellationToken = default)
    {
        string normalized = fieldName.ToLowerInvariant();
        var existing = await db.PlatformFieldDefaults.AsTracking()
            .Where(e => e.PlatformId == platformId && e.FieldName.ToLower() == normalized)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            db.PlatformFieldDefaults.RemoveRange(existing);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
