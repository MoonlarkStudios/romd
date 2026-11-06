using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Storage;
using Romd.Storage.FileSystem;

namespace Romd.Infrastructure.Storage;

/// <summary>Raw artifact delivery has its own all-output authorization lane; no per-BIN target is accepted.</summary>
public sealed class RomCdArchiveDownloadReader(IRomCdArchiveDownloadCatalog catalog, IContentAddressableStore cas,
    IRomChdDeliveryAdmission admission) : IRomCdArchiveDownloadReader
{
    public async Task<Stream?> OpenAsync(RomCdArchiveDownload target, CancellationToken ct = default)
    {
        var current = await CurrentAsync(target, ct);
        if (current is null)
            return null;
        var output = current.Outputs.OrderBy(row => row.Id).First();
        if (!admission.AdmitsCd(new(new RomContentArtifact(current, output.Id, false))))
            return null;
        return await OpenVerifiedAsync(current.ArtifactSha256, current.ArtifactSize, ct);
    }
    public async Task<Stream?> OpenCueAsync(RomCdArchiveDownload target, CancellationToken ct = default)
    {
        var current = await CurrentAsync(target, ct);
        if (current?.Manifest is not RomReconstructionManifest.CdTracks manifest)
            return null;
        var cue = manifest.Members.OfType<RomCdStoredMember>().Single(member => member.Role == RomCdStoredRole.Cue);
        return await OpenVerifiedAsync(cue.Identity.Sha256, cue.Identity.Size, ct);
    }
    private async Task<RomRepresentationFacts?> CurrentAsync(RomCdArchiveDownload target, CancellationToken ct)
    {
        var current = await catalog.FindForLayoutAsync(target.LayoutId, ct);
        // Layout/container/proof identities are immutable. Output ownership can change, so compare every authorized selector again.
        return current is not null && current.PublicationId == target.PublicationId && current.Representation.Id == target.Representation.Id &&
            current.Representation.Outputs.OrderBy(row => row.Id).SequenceEqual(target.Representation.Outputs.OrderBy(row => row.Id))
            ? current.Representation : null;
    }
    private async Task<Stream?> OpenVerifiedAsync(Romd.Domain.Hashing.Sha256 hash, long size, CancellationToken ct)
    {
        var raw = await cas.OpenRandomAccessAsync(StorageKey.FromHash(hash), ct);
        if (raw is null)
            return null;
        try
        {
            if (raw.Length != size)
            { await raw.DisposeAsync(); return null; }
            return new HashVerifyingStream(raw.AsStream(), StorageKey.FromHash(hash));
        }
        catch { await raw.DisposeAsync(); throw; }
    }
}
