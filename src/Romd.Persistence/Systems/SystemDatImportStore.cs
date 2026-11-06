using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Hashing;

namespace Romd.Persistence.Systems;

public sealed class SystemDatImportStore(RomdDbContext db) : ISystemDatImportStore
{
    public async Task<bool> EnableStagedAsync(int platformId, CancellationToken ct = default)
    {
        RequireTransaction(ct);
        return await db.Platforms.Where(platform => platform.Id == platformId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(platform => platform.IsEnabled, true), ct) != 0;
    }

    public Task<bool> IsDocumentInstalledAsync(Sha256 hash, CancellationToken ct = default)
    {
        RequireTransaction(ct);
        return db.DatFiles.AnyAsync(dat => db.Files.Any(file => file.Id == dat.FileId && file.Sha256 == hash), ct);
    }

    private void RequireTransaction(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("System DAT import requires a caller-owned transaction.");
    }
}
