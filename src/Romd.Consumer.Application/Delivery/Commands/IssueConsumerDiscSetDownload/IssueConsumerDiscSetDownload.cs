using ErrorOr;
using Romd.Application.Common.Content;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetManifest;
using Romd.Consumer.Application.Libraries;
using Romd.Contracts.Consumer.Releases;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Paths;
using Romd.Domain.Source.Rom;

namespace Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetDownload;

public sealed record IssueConsumerDiscSetDownloadCommand(int Id, ConsumerDiscSetSelectionRequest Selection,
    string Format) : ICommand<ContentGrant>;

public sealed class IssueConsumerDiscSetDownloadCommandHandler(ICurrentUser user, IConsumerDiscSetRepository repository,
    IConsumerDiscSetManifestBuilder manifests, IConsumerDiscSetDownloadTickets tickets)
    : ICommandHandler<IssueConsumerDiscSetDownloadCommand, ContentGrant>
{
    public async Task<ErrorOr<ContentGrant>> HandleAsync(IssueConsumerDiscSetDownloadCommand command, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated || user.UserId is not { } userId)
            return ConsumerErrors.CurrentUserRequired();
        if (!ConsumerDiscSetArchiveFormats.Choices.Any(x => x.Format == command.Format))
            return Error.Validation("format", "Choose original or chd.");
        var result = await repository.ReadForDownloadAsync(new(userId), command.Id, ct);
        if (result is ConsumerLibraryReadResult<ConsumerDiscSetPlan>.LibraryUnavailable or ConsumerLibraryReadResult<ConsumerDiscSetPlan>.ProjectionInconsistent)
            return ConsumerErrors.CurrentLibraryUnavailable();
        if (result is not ConsumerLibraryReadResult<ConsumerDiscSetPlan>.Found found)
            return ConsumerErrors.ContentNotFound();
        if (found.Value.Revision != command.Selection.Revision)
            return Error.Conflict("DiscSet.StaleRevision", "The disc set changed. Reload its status before downloading.");
        // Reuse all-member authorization, explicit selection/partial consent and exact CUE dependency validation.
        // No individual capabilities escape if any later archive check fails.
        var built = await manifests.BuildAsync(new(command.Id, command.Selection.Revision, command.Selection, IssueGrants: false),
            found.Value, found.LibraryId, userId, ct);
        if (built.IsError)
            return built.Errors;
        var entries = new List<ConsumerDiscSetArchiveEntry>();
        var layouts = new List<(IReadOnlyList<string> Files, string EntryPoint)>();
        foreach (var (disc, index) in built.Value.Discs.Select((disc, index) => (disc, index)))
        {
            var source = found.Value.Discs.Single(x => IdCoder.Encode(x.Release.ReleaseId) == disc.ReleaseId);
            string directory = DiscPlaylist.DirectoryFor(index + 1) + "/";
            if (command.Format == "chd" && source.CdArchive is { } cd && CanReplace(disc, source, cd))
            {
                string name = cd.Filename;
                entries.Add(new(directory + name, cd.Representation.ArtifactSha256, cd.Representation.ArtifactSize,
                    new ConsumerDiscSetArchiveSource.CdArchive(cd)));
                layouts.Add(([name], name));
                continue;
            }
            foreach (var file in disc.Files)
            {
                string path = file.RelativePath[directory.Length..];
                bool descriptor = source.OriginalLayout?.Files.Any(x => x.Path == path && x.RomId is null) == true ||
                    source.Release.Items.Any(x => x.RelativePath == path && x.Source is ConsumerReleaseManifestSource.Derived);
                var hash = Sha256.Parse(file.Sha256);
                long size = file.SizeBytes;
                ConsumerDiscSetArchiveSource selected;
                if (descriptor)
                    selected = new ConsumerDiscSetArchiveSource.Descriptor();
                else
                {
                    var target = source.Release.Items.SelectMany(x => x.Formats).Select(x => x.Delivery)
                        .Where(x => x.State == RomDeliveryState.Available).Select(x => x.Target)
                        .FirstOrDefault(x => x is { Format: RomDeliveryFormat.Original } && x.Sha256 == hash && x.SizeBytes == size && RomDeliveryTargets.IsValid(x));
                    if (target is null)
                        return Error.Conflict("DiscSet.Unavailable", "A selected original is unavailable.");
                    selected = new ConsumerDiscSetArchiveSource.Original(target);
                }
                entries.Add(new(file.RelativePath, hash, size, selected));
            }
            layouts.Add((disc.Files.Select(x => x.RelativePath[directory.Length..]).ToArray(), disc.EntryPoint[directory.Length..]));
        }
        if (!DiscPlaylist.TryCreate(layouts, out var playlist, out _))
            return Error.Conflict("DiscSet.Layout", "This set has no portable delivery layout.");
        string filename = string.Concat(found.Value.Name.Take(160).Select(c => char.IsControl(c) || "/\\:*?\"<>|".Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (filename.Length == 0)
            filename = "disc-set";
        // Keep the playlist name stable between partial/full downloads. External emulators may key shared saves by it.
        string playlistPath = filename + ".m3u";
        if (PortablePathSet.Validate([playlistPath]) is not null)
            playlistPath = "disc-set-" + IdCoder.Encode(found.Value.Id) + ".m3u";
        if (built.Value.Selection?.Kind == "partial")
            filename += "-partial";
        var ticket = tickets.Issue(new(filename + ".zip", playlist!.Text, entries, playlistPath));
        return new ContentGrant { DownloadUrl = ticket.DownloadUrl, ExpiresAt = ticket.ExpiresAt };
    }

    private static bool CanReplace(Romd.Contracts.Consumer.Releases.ConsumerDiscSetDisc delivered, ConsumerDiscSetDisc source, RomCdArchiveDownload archive)
    {
        if (source.OriginalLayout is { } layout && archive.LayoutId != layout.Id ||
            archive.Representation.Manifest is not RomReconstructionManifest.CdTracks manifest)
            return false;
        var bins = manifest.Members.OfType<RomCdBinMember>().ToArray();
        var files = delivered.Files.Where(x => !x.RelativePath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)).ToArray();
        // Never drop required sidecars or allow a whole CHD to expose another release's BINs.
        return bins.Length > 0 &&
            files.Select(file => (Hash: file.Sha256, Size: (long)file.SizeBytes)).OrderBy(x => x.Hash, StringComparer.Ordinal).ThenBy(x => x.Size)
                .SequenceEqual(bins.Select(bin => (Hash: bin.Identity.Sha256.ToString(), Size: bin.Identity.Size))
                    .OrderBy(x => x.Hash, StringComparer.Ordinal).ThenBy(x => x.Size));
    }
}
