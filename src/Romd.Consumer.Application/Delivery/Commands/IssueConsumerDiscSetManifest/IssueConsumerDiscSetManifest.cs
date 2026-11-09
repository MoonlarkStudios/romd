using System.Security.Cryptography;
using System.Text;
using ErrorOr;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Consumer.Releases;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

using ContractConsumerDiscSetDisc = Romd.Contracts.Consumer.Releases.ConsumerDiscSetDisc;

using RomDeliveryFormat = Romd.Domain.Source.Rom.RomDeliveryFormat;

namespace Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetManifest;

public sealed record IssueConsumerDiscSetManifestCommand(int Id, Guid Revision,
    ConsumerDiscSetSelectionRequest? Selection = null, bool IssueGrants = true) : ICommand<ConsumerDiscSetManifest>;

public sealed class IssueConsumerDiscSetManifestCommandHandler(ICurrentUser currentUser,
    IConsumerDiscSetRepository repository, IConsumerContentGrantIssuer issuer, IServerInstanceIdentity server,
    IRomContentReader reader, IConsumerOriginalLayoutGrantIssuer attachmentIssuer,
    IConsumerContentArtifactResolver artifacts, IConsumerDerivedDescriptorGrantIssuer derivedIssuer,
    IConsumerDerivedDescriptorSource derivedSource) : ICommandHandler<IssueConsumerDiscSetManifestCommand, ConsumerDiscSetManifest>, IConsumerDiscSetManifestBuilder
{
    private abstract record FileSource
    {
        private FileSource() { }
        public sealed record Original(RomDeliveryTarget Target) : FileSource;
        public sealed record Attachment(OriginalDiscLayout Layout, int FileId) : FileSource;
        public sealed record Derived(DerivedDescriptorBinding Descriptor) : FileSource;
    }
    private sealed record BoundFile(string Path, Sha256 Sha256, long SizeBytes, FileSource Source)
    {
        public ConsumerReleaseFileProvenance Provenance => Source is FileSource.Derived
            ? ConsumerReleaseFileProvenance.Derived : ConsumerReleaseFileProvenance.Original;
    }
    public async Task<ErrorOr<ConsumerDiscSetManifest>> HandleAsync(IssueConsumerDiscSetManifestCommand command, CancellationToken ct = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();
        var result = await repository.ReadStatusAsync(new(userId), command.Id, ct);
        if (result is ConsumerLibraryReadResult<ConsumerDiscSetPlan>.LibraryUnavailable or ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ProjectionInconsistent)
            return Conflict("Library", "The published Library is unavailable or inconsistent.");
        if (result is not ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found found)
            return ConsumerErrors.ContentNotFound();
        // A stale pin of a visible set is a conflict, never an unknown set
        // (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency").
        if (found.Value.Revision != command.Revision)
            return Conflict("StaleRevision", "The disc set changed. Reload its status before requesting a manifest.");
        return await BuildAsync(command, found.Value, found.LibraryId, userId, ct);
    }

    public async Task<ErrorOr<ConsumerDiscSetManifest>> BuildAsync(IssueConsumerDiscSetManifestCommand command,
        ConsumerDiscSetPlan plan, int libraryId, Guid userId, CancellationToken ct = default)
    {
        var status = DiscSetContracts.Status(plan, server.InstanceId);
        var request = command.Selection;
        if (request is not null && (request.ContractVersion != 1 || request.Revision != plan.Revision || request.ObservationId != status.ObservationId))
            return Conflict("Observation", "The observed set changed. Reload its status before selecting discs.");
        if (request is not null && request.SelectedReleaseIds is null)
            return Conflict("Selection", "An explicit ordered selection is required.");
        string[] selectedIds = request?.SelectedReleaseIds?.ToArray() ?? plan.Discs.Select(x => IdCoder.Encode(x.Release.ReleaseId)).ToArray();
        var selected = plan.Discs.Where(x => selectedIds.Contains(IdCoder.Encode(x.Release.ReleaseId), StringComparer.Ordinal)).ToArray();
        if (selected.Length == 0 || selected.Length != selectedIds.Length
            || !selected.Select(x => IdCoder.Encode(x.Release.ReleaseId)).SequenceEqual(selectedIds, StringComparer.Ordinal))
            return Conflict("Selection", "Select unique authorized members in their published order.");
        var omitted = plan.Discs.Except(selected).Select(x => new ConsumerDiscSetOmittedDisc(IdCoder.Encode(x.Release.ReleaseId), x.Ordinal)).ToArray();
        bool partial = omitted.Length > 0 || status.MissingExpectedOrdinals.Count > 0;
        if (partial && request?.AllowPartial != true)
            return Conflict("PartialConsent", "Explicit partial-install consent is required for known omissions.");
        if (selected.Any(x => x.Readiness != "ready"))
            return Conflict("Unavailable", "Every selected disc and companion file must be available.");
        if (selected.Any(disc => disc.Release.UserId != userId || disc.Release.Items.Any(item =>
                item.Source is ConsumerReleaseManifestSource.Derived derived && derived.Descriptor.ReleaseId != disc.Release.ReleaseId)))
            return ConsumerErrors.ContentNotFound();
        var bound = new List<(ConsumerDiscSetDisc Disc, IReadOnlyList<BoundFile> Files)>();
        foreach (var disc in selected)
        {
            var files = new List<BoundFile>();
            if (disc.OriginalLayout is { } layout)
            {
                if (layout.PlatformId != disc.Release.PlatformId || layout.ReleaseId != disc.Release.ReleaseId || layout.Id <= 0 || layout.Revision == Guid.Empty)
                    return Conflict("Layout", "The retained layout does not match this release.");
                foreach (var file in layout.Files.OrderBy(x => x.Path == layout.EntryPoint ? 0 : 1).ThenBy(x => x.Path, StringComparer.Ordinal))
                {
                    if (file.RomId is { } romId)
                    {
                        var target = disc.Release.Items.SelectMany(x => x.Formats).Select(x => x.Delivery)
                            .Where(x => x.Format == RomDeliveryFormat.Original && x.State == RomDeliveryState.Available)
                            .Select(x => x.Target).FirstOrDefault(x => x is not null && x.RomId == romId && x.FileId == file.FileId
                                && x.Sha256 == file.Sha256 && x.SizeBytes == file.SizeBytes && RomDeliveryTargets.IsValid(x));
                        if (target is null)
                            return Conflict("Unavailable", "A pinned original dependency is unavailable.");
                        files.Add(new(file.Path, file.Sha256, file.SizeBytes, new FileSource.Original(target)));
                    }
                    else
                    {
                        if (file.Role != "descriptor" || file.Path != layout.EntryPoint || !file.Path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)
                            || file.SizeBytes is < 0 or > ConsumerDerivedDescriptors.MaximumBytes)
                            return Conflict("Descriptor", "The retained descriptor cannot be delivered.");
                        files.Add(new(file.Path, file.Sha256, file.SizeBytes, new FileSource.Attachment(layout, file.FileId)));
                    }
                }
            }
            else
            {
                foreach (var item in disc.Release.Items.OrderBy(x => x.RelativePath == disc.EntryPoint ? 0 : 1).ThenBy(x => x.RelativePath, StringComparer.Ordinal))
                {
                    if (item.Source is ConsumerReleaseManifestSource.Derived derived)
                    {
                        var descriptor = derived.Descriptor;
                        if (descriptor.RelativePath != item.RelativePath || !await derivedSource.IsRetainedAsync(descriptor, ct))
                            return Conflict("Unavailable", "The captured descriptor is unavailable.");
                        files.Add(new(item.RelativePath, descriptor.Identity.Sha256, descriptor.Identity.Size, new FileSource.Derived(descriptor)));
                    }
                    else if (item.Source is ConsumerReleaseManifestSource.Original { AvailableTarget: { } target })
                        files.Add(new(item.RelativePath, target.Sha256, target.SizeBytes, new FileSource.Original(target)));
                    else
                        return Conflict("Unavailable", "Every selected disc and companion file must be available.");
                }
            }
            if (disc.EntryPoint.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
            {
                var descriptorFiles = files.Where(x => x.Path == disc.EntryPoint).ToArray();
                if (descriptorFiles.Length != 1 || descriptorFiles[0].SizeBytes > ConsumerDerivedDescriptors.MaximumBytes)
                    return Conflict("Descriptor", "A single bounded CUE descriptor is required.");
                var cue = descriptorFiles[0];
                await using var stream = cue.Source is FileSource.Original original ? await reader.OpenAsync(original.Target, ct) : await artifacts.ResolveAsync(cue.Sha256, ct);
                if (stream is null)
                    return Conflict("Descriptor", "CUE descriptor is unavailable.");
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + count > cue.SizeBytes)
                        return Conflict("Descriptor", "CUE descriptor exceeds the supported limit.");
                    await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
                }
                byte[] bytes = buffer.ToArray();
                if (bytes.LongLength != cue.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != cue.Sha256.ToString()
                    || !CueDependencies.Validate(bytes, disc.EntryPoint, files.Select(x => x.Path).ToArray()))
                    return Conflict("Descriptor", "CUE references are unsafe, unsupported or missing from the authorized file set.");
            }
            bound.Add((disc, files));
        }
        if (!DiscPlaylist.TryCreate(bound.Select(x => ((IReadOnlyList<string>)x.Files.Select(f => f.Path).ToArray(), x.Disc.EntryPoint)).ToArray(), out var playlist, out _))
            return Conflict("Layout", "This set has no portable delivery layout.");
        var selection = new ConsumerDiscSetSelection(partial ? "partial" : "all-known",
            partial ? "partial" : plan.ExpectedDiscCount is null ? "unknown-total" : "complete", selectedIds, omitted, status.MissingExpectedOrdinals);
        // Snapshot excludes URLs/readiness/observation, so renewal succeeds only for the same exact plan.
        string snapshot = DiscSetContracts.Hash(new
        {
            Server = server.InstanceId,
            Set = plan.Id,
            plan.Revision,
            plan.ExpectedDiscCount,
            Selection = selection,
            Members = bound.Select(x => new
            {
                x.Disc.Ordinal,
                x.Disc.Release.ReleaseId,
                x.Disc.EntryPoint,
                LayoutId = x.Disc.OriginalLayout?.Id,
                LayoutRevision = x.Disc.OriginalLayout?.Revision,
                Files = x.Files.Select(f => new { f.Path, Hash = f.Sha256.ToString(), f.SizeBytes, f.Provenance, Source = SourceIdentity(f.Source) })
            }),
            Playlist = playlist!.Sha256
        });
        if (request?.RequiredSnapshotId is { } required && required != snapshot)
            return Conflict("Snapshot", "The selected delivery snapshot changed; an explicit install update is required.");
        // Every policy, selection, descriptor and snapshot check has succeeded before issuing any capability.
        var discs = bound.Select((x, i) => new ContractConsumerDiscSetDisc(IdCoder.Encode(x.Disc.Release.ReleaseId), x.Disc.Ordinal,
            x.Disc.Release.ReleaseName, playlist!.EntryPoints[i], x.Files.Select(file =>
            {
                ConsumerIssuedContentGrant? grant = null;
                if (command.IssueGrants)
                    grant = file.Source switch
                    {
                        FileSource.Original original => issuer.IssueDownloadGrant(new(userId, libraryId, x.Disc.Release.TitleId,
                            x.Disc.Release.ReleaseId, original.Target)),
                        FileSource.Attachment attachment => attachmentIssuer.Issue(new(userId, libraryId, x.Disc.Release.TitleId,
                            x.Disc.Release.ReleaseId, attachment.Layout.Id, attachment.Layout.Revision, attachment.FileId,
                            file.Sha256, file.SizeBytes, file.Path)),
                        FileSource.Derived derived => derivedIssuer.Issue(new(userId, libraryId, x.Disc.Release.TitleId, derived.Descriptor)),
                        _ => throw new InvalidOperationException("Unknown disc delivery source.")
                    };
                return new ConsumerDiscSetFile(DiscPlaylist.DirectoryFor(i + 1) + "/" + file.Path, (ByteCount)file.SizeBytes,
                    file.Sha256.ToString(), file.Provenance, new() { DownloadUrl = grant?.DownloadUrl ?? string.Empty, ExpiresAt = grant?.ExpiresAt ?? DateTimeOffset.MinValue });
            }).ToArray())
        {
            LayoutId = x.Disc.OriginalLayout is { } original ? IdCoder.Encode(original.Id) : null,
            LayoutRevision = x.Disc.OriginalLayout?.Revision.ToString("D")
        }).ToArray();
        return new ConsumerDiscSetManifest(server.InstanceId.ToString("D"), IdCoder.Encode(plan.Id), plan.Revision, snapshot,
            IdCoder.Encode(plan.Discs[0].Release.TitleId), plan.Discs[0].Release.PlatformShortName, plan.Name, plan.ExpectedDiscCount,
            selection.Completeness == "complete", discs, new("m3u-sha256:" + playlist!.Sha256, "playlist.m3u", playlist.Sha256,
                (ByteCount)Encoding.UTF8.GetByteCount(playlist.Text), "utf-8", playlist.Text))
        { ObservationId = status.ObservationId, Selection = selection };
    }
    private static object SourceIdentity(FileSource source) => source switch
    {
        FileSource.Original original => new { Kind = "original", original.Target.RomId, original.Target.FileId, original.Target.ReadBinding },
        FileSource.Attachment attachment => new { Kind = "attachment", attachment.Layout.Id, attachment.Layout.Revision, attachment.FileId },
        FileSource.Derived derived => new
        {
            Kind = "derived",
            derived.Descriptor.Id,
            derived.Descriptor.Revision,
            derived.Descriptor.CatalogFileId,
            derived.Descriptor.RepresentationId,
            derived.Descriptor.MemberOrdinal,
            derived.Descriptor.FileId,
            derived.Descriptor.GeneratorProfile
        },
        _ => throw new InvalidOperationException("Unknown disc delivery source.")
    };
    private static Error Conflict(string code, string description) => Error.Conflict("DiscSet." + code, description);
}
