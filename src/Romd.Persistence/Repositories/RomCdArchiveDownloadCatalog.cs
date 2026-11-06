using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories;

public sealed class RomCdArchiveDownloadCatalog(RomdDbContext db, DbContextOptions<RomdDbContext> readOptions,
    RomDeliveryPolicy deliveryPolicy) : IRomCdArchiveDownloadCatalog
{
    /// <summary>Admin recovery follows durable layout custody after later catalog decisions and logical-ROM withdrawal.</summary>
    public Task<RomCdArchiveDownload?> FindForLayoutAsync(int layoutId, CancellationToken ct = default) => FindAsync(layoutId, null, ct);
    internal Task<RomCdArchiveDownload?> FindForLiveLayoutAsync(int layoutId, int releaseId, CancellationToken ct) => FindAsync(layoutId, releaseId, ct);

    private async Task<RomCdArchiveDownload?> FindAsync(int layoutId, int? releaseId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel == IsolationLevel.ReadCommitted)
        {
            await using var independent = new RomdDbContext(readOptions);
            return await OwnedSnapshotAsync(independent, layoutId, releaseId, ct);
        }
        return db.Database.CurrentTransaction is null ? await OwnedSnapshotAsync(db, layoutId, releaseId, ct) : await ReadAsync(db, layoutId, releaseId, ct);
    }
    private async Task<RomCdArchiveDownload?> OwnedSnapshotAsync(RomdDbContext context, int layoutId, int? releaseId, CancellationToken ct)
    {
        await using var snapshot = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var result = await ReadAsync(context, layoutId, releaseId, ct);
        await snapshot.CommitAsync(ct);
        return result;
    }
    private async Task<RomCdArchiveDownload?> ReadAsync(RomdDbContext context, int layoutId, int? releaseId, CancellationToken ct)
    {
        var owners = context.CdArchiveOwners.Where(owner => owner.LayoutId == layoutId);
        if (releaseId is { } release)
            owners = from owner in owners
                     join layout in context.OriginalDiscLayouts on owner.LayoutId equals layout.Id
                     join catalog in context.CatalogReleases on owner.ReleaseId equals catalog.Id
                     where owner.ReleaseId == release && owner.PlatformId == catalog.PlatformId && owner.CatalogFingerprint == catalog.Fingerprint &&
                         !catalog.HasTitleConflict && (layout.State == "pending" || layout.State == "accepted" && layout.ReleaseId == release)
                     select owner;
        var row = await owners.SingleOrDefaultAsync(ct);
        if (row is null)
            return null;
        var facts = (await new MediaFactReader(context).ReadAsync([row.RepresentationId], ct)).GetValueOrDefault(row.RepresentationId);
        if (facts?.Manifest is not RomReconstructionManifest.CdTracks manifest ||
            !facts.Verifications.Any(proof => proof.Id == row.VerificationId && proof.Decoder is RomDecoderIdentity.Native))
            return null;
        if (releaseId is not null)
        {
            int currentCue = await context.OriginalDiscLayouts.Where(layout => layout.Id == layoutId).Select(layout => layout.DescriptorFileId).SingleAsync(ct);
            if (!facts.Descriptors.Any(descriptor => descriptor.FileId == currentCue &&
                manifest.Members[descriptor.Ordinal] is RomCdStoredMember { Role: RomCdStoredRole.Cue } cue && cue.Path == row.EntryPoint))
                return null;
        }
        return new(layoutId, row.PublicationId, facts, Path.ChangeExtension(row.EntryPoint, deliveryPolicy.Formats.FileExtension(RomDeliveryFormat.Chd)));
    }
}
