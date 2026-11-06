using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Content;

namespace Romd.Persistence.Catalog;

/// <summary>Read-only consumer composition; never resolves admin mutation services or write locks.</summary>
public sealed class ConsumerOriginalDiscLayoutReader(RomdDbContext db) : IOriginalDiscLayoutReader
{
    public async Task<OriginalDiscLayoutIdentity?> ReadIdentityAsync(int platformId, int releaseId, string fingerprint, CancellationToken ct)
    {
        RequireSnapshot();
        if (!await db.CatalogReleases.AnyAsync(x => x.Id == releaseId && x.PlatformId == platformId
            && x.Fingerprint == fingerprint && !x.HasTitleConflict, ct))
            return null;
        var rows = await db.OriginalDiscLayouts.Include(x => x.Files).Where(x => x.PlatformId == platformId
            && x.Fingerprint == fingerprint && x.State == "accepted").OrderBy(x => x.Id).Take(2).ToListAsync(ct);
        if (rows.Count == 0)
            return await db.OriginalDiscLayouts.AnyAsync(x => x.PlatformId == platformId && x.ReleaseId == releaseId
                && x.State == "accepted", ct) ? null : new(null, null);
        if (rows.Count != 1)
            return null;
        var row = rows[0];
        var active = row.Files.Where(x => x.BindingRevision == row.Revision).ToArray();
        // A partial binding publication is not an authoritative replacement. Byte/ROM
        // availability is deliberately left to ResolveAsync and delivery readiness.
        if (active.Length == 0)
            return null;
        string[]? references;
        try
        { references = JsonSerializer.Deserialize<string[]>(row.DependencyPathsJson); }
        catch (JsonException) { return null; }
        if (references is null || references.Length == 0 || references.Any(path => !active.Any(x => x.Path == path)))
            return null;
        return new(row.Id, row.Revision);
    }

    public async Task<bool> HasStaleAsync(int platformId, int releaseId, string fingerprint, CancellationToken ct)
    {
        RequireSnapshot();
        bool bound = await db.OriginalDiscLayouts.AnyAsync(x => x.State == "accepted" && x.PlatformId == platformId
            && (x.ReleaseId == releaseId || x.Fingerprint == fingerprint), ct);
        return bound && await ResolveAsync(platformId, releaseId, fingerprint, ct) is null;
    }
    public async Task<OriginalDiscLayout?> ResolveAsync(int platformId, int releaseId, string fingerprint, CancellationToken ct)
    {
        RequireSnapshot();
        var rows = await db.OriginalDiscLayouts.Include(x => x.Files).Where(x => x.PlatformId == platformId
            && x.Fingerprint == fingerprint && x.State == "accepted").OrderBy(x => x.Id).Take(2).ToListAsync(ct);
        if (rows.Count != 1 || !await db.CatalogReleases.AnyAsync(x => x.Id == releaseId && x.PlatformId == platformId
                && x.Fingerprint == fingerprint && !x.HasTitleConflict, ct))
            return null;
        var row = rows[0];
        var active = row.Files.Where(x => x.BindingRevision == row.Revision).OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
        if (active.Length == 0)
            return null;
        var fileIds = active.Select(x => x.FileId).Append(row.DescriptorFileId).Distinct().ToArray();
        var files = await db.Files.Where(x => fileIds.Contains(x.Id)).ToListAsync(ct);
        var descriptor = files.SingleOrDefault(x => x.Id == row.DescriptorFileId);
        if (descriptor is null || files.Count != fileIds.Length)
            return null;
        var required = await db.CatalogReleaseFiles.Where(x => x.CatalogReleaseId == releaseId).ToListAsync(ct);
        var romIds = active.Select(x => x.RomId).ToArray();
        var roms = await db.RomFiles.Where(x => romIds.Contains(x.Id)).ToListAsync(ct);
        if (required.Count == 0 || required.Any(x => x.Sha1 == null ||
                !roms.Any(r => r.Sha1 == x.Sha1 && r.ContentSize == x.Size) &&
                !OriginalDiscLayoutCatalogCoverage.SatisfiedByDescriptor(x, descriptor)))
            return null;
        var backing = await OriginalDiscLayoutBacking.ReadAsync(db, romIds, ct);
        var result = new List<OriginalDiscLayoutFile> { new(row.EntryPoint, descriptor.Id, descriptor.Sha256, descriptor.Size, null, "descriptor") };
        foreach (var binding in active)
        {
            var file = backing.SingleOrDefault(x => x.FileId == binding.FileId && x.RomId == binding.RomId);
            var rom = roms.SingleOrDefault(x => x.Id == binding.RomId);
            if (file is null || rom is null || file.Sha256 != rom.ContentSha256 || file.Size != rom.ContentSize)
                return null;
            result.Add(new(binding.Path, file.FileId, file.Sha256, file.Size, rom.Id, "catalog-original"));
        }
        string[]? references;
        try
        { references = JsonSerializer.Deserialize<string[]>(row.DependencyPathsJson); }
        catch (JsonException) { return null; }
        if (references is null || references.Length == 0 || references.Any(path => !result.Any(x => x.Path == path)))
            return null;
        return new(row.Id, row.Revision, platformId, releaseId, fingerprint, row.EntryPoint, result);
    }
    private void RequireSnapshot()
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Layout reads require the caller's consistent authorization snapshot.");
    }
}
