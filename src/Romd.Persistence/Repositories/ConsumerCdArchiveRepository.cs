using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

/// <summary>One live library snapshot authorizes every original BIN exposed by a shared container.</summary>
public sealed class ConsumerCdArchiveRepository(RomdDbContext db, IRomContentCatalog contents, IRomChdDeliveryAdmission admission,
    RomDeliveryPolicy deliveryPolicy, IDerivedDescriptorReader derivedDescriptors, DbContextOptions<RomdDbContext> readOptions) : IConsumerCdArchiveRepository
{
    public Task<ConsumerLibraryReadResult<RomCdArchiveDownload>> FindAsync(ConsumerLibraryScope scope, int releaseId, CancellationToken ct = default) =>
        new LiveConsumerLibraryQuery(db).ReadAsync<RomCdArchiveDownload>(scope, async (libraryId, token) =>
        {
            var release = await new ConsumerReleaseManifestRepository(db, deliveryPolicy, contents, derivedDescriptors, admission).GetManifestForLibraryAsync(scope.UserId, libraryId, releaseId, token);
            if (release is not ConsumerLibraryProjectionResult<ConsumerReleaseManifest>.Found found)
                return new ConsumerLibraryProjectionResult<RomCdArchiveDownload>.ItemNotFound();
            var target = await FindForReleaseAsync(found.Value, token);
            return target is null ? new ConsumerLibraryProjectionResult<RomCdArchiveDownload>.ItemNotFound()
                : new ConsumerLibraryProjectionResult<RomCdArchiveDownload>.Found(target);
        }, ct);

    /// <summary>The caller owns the consistent all-member library read; do not open a second authorization snapshot.</summary>
    internal async Task<RomCdArchiveDownload?> FindForReleaseAsync(ConsumerReleaseManifest release, CancellationToken token, bool requireAdmission = false)
    {
        int releaseId = release.ReleaseId;
        // A detached or rejected layout keeps its admin recovery archive but no longer speaks for the release.
        var layouts = await (from row in db.CdArchiveOwners
                             join layout in db.OriginalDiscLayouts on row.LayoutId equals layout.Id
                             where row.ReleaseId == releaseId &&
                                 (layout.State == "pending" || layout.State == "accepted" && layout.ReleaseId == releaseId)
                             select row.LayoutId).Distinct().Take(2).ToArrayAsync(token);
        if (layouts.Length != 1 || await new RomCdArchiveDownloadCatalog(db, readOptions, deliveryPolicy).FindForLiveLayoutAsync(layouts[0], releaseId, token) is not { } target)
            return null;
        if (requireAdmission && !admission.AdmitsCd(new(new RomContentArtifact(target.Representation, target.Representation.Outputs.OrderBy(x => x.Id).First().Id, false))))
            return null;
        var manifest = (RomReconstructionManifest.CdTracks)target.Representation.Manifest;
        for (int ordinal = 0; ordinal < manifest.Members.Count; ordinal++)
        {
            if (manifest.Members[ordinal] is not RomCdBinMember bin)
                continue;
            var owner = target.Representation.Outputs.SingleOrDefault(row => row.Selector is RomOutputSelector.Member member && member.Ordinal == ordinal);
            if (owner?.RomId is not { } romId || !release.Items.Any(item => item.Source is ConsumerReleaseManifestSource.Original { AvailableTarget: { } memberTarget } && memberTarget.RomId == romId && item.IsAvailable &&
                item.Formats.Any(format => format.Delivery.Target is { Format: RomDeliveryFormat.Original } original &&
                    original.ContentSha256 == bin.Identity.Sha256 && original.ContentSizeBytes == bin.Identity.Size)))
                return null;
        }
        return target;
    }
}
