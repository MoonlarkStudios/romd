using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Current catalog/source ownership and fenced completion around atomic measured CD fact publication.</summary>
public interface IRomCdArchivePublicationGate
{
    /// <summary>Revalidate catalog scope and create or retain the actual BIN owners inside the caller transaction, before artifact publication.</summary>
    Task<RomCdArchivePublicationAuthority> PrepareAsync(RomMeasuredRepresentation representation, CancellationToken ct);
    Task<JobItem?> CompleteAsync(RomRepresentationFacts retained, CancellationToken ct);
}

public sealed class RomCdArchivePublisher(IFileStorageService files, IUnitOfWork work, IMediaFactPublisher facts, IJobItemRepository outcomes)
{
    public async Task<RomRepresentationFacts> PublishAsync(RomMeasuredRepresentation measured, ITempFile chd, IReadOnlyDictionary<string, ITempFile> descriptors,
        IRomCdArchivePublicationGate gate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(measured);
        ArgumentNullException.ThrowIfNull(chd);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(gate);
        if (measured.Manifest is not RomReconstructionManifest.CdTracks manifest || measured.Container is not RomContainerFacts.Chd ||
            chd.FileSha256 != measured.Measurement.ArtifactSha256 || chd.FileSize != measured.Measurement.ArtifactSize ||
            chd.DatHashes is not { } hashes || hashes.BytesRead != chd.FileSize || hashes.Md5.IsEmpty || hashes.Sha1.IsEmpty)
            throw new InvalidDataException("CD publication requires the exact independently measured artifact and checksums.");
        var stored = manifest.Members.Select((member, ordinal) => (member, ordinal)).Where(pair => pair.member is RomCdStoredMember).ToArray();
        var bins = manifest.Members.OfType<RomCdBinMember>().ToArray();
        if (descriptors.Count != stored.Length || stored.Any(pair => !descriptors.TryGetValue(pair.member.Path, out var source) ||
                source.FileSha256 != pair.member.Identity.Sha256 || source.FileSize != pair.member.Identity.Size))
            throw new InvalidDataException("Every retained descriptor must have its actual measured byte identity before publication.");
        List<IStagedFile> staged = [];
        Exception? failure = null;
        try
        {
            staged.Add(await files.StageRawAsync(chd, ct));
            foreach (var pair in stored)
                staged.Add(await files.StageAsync(descriptors[pair.member.Path], ct));
            await using var transaction = await work.BeginTransactionAsync(ct);
            var authority = await gate.PrepareAsync(measured, ct);
            RomRepresentationFacts retained;
            if (authority is RomCdArchivePublicationAuthority.Keep keep)
            {
                if (!measured.MatchesLayout(keep.Retained) || keep.Retained.Outputs.Any(output => output.RomId is not > 0))
                    throw new InvalidDataException("Retained canonical bytes require the same exact reconstruction layout and current logical owners.");
                retained = keep.Retained;
            }
            else if (authority is RomCdArchivePublicationAuthority.Publish publish)
            {
                var binRomIds = publish.BinRomIds;
                if (binRomIds.Count != bins.Length || bins.Any(bin => !binRomIds.TryGetValue(bin.Path, out int id) || id <= 0))
                    throw new InvalidDataException("Publication requires the actual logical owner of every measured BIN inside its caller transaction.");
                var publication = await files.PublishStagedAsync(staged, ct);
                if (publication.RequiresFlush)
                    await work.FlushAsync(ct);
                var published = publication.ReadAfterFlush();
                if (published.Count != staged.Count || published[0].File.IsCompressed || published[0].File.Sha256 != chd.FileSha256 ||
                    published[0].File.Size != chd.FileSize || published[0].File.SizeOnDisk != chd.FileSize)
                    throw new InvalidDataException("The CD artifact ledger must retain the exact measured raw bytes.");
                await files.RecordArtifactChecksumsAsync(published[0].File.Id, new(chd.FileSha256, hashes.Md5, hashes.Sha1, chd.FileSize), ct);
                var retainedDescriptors = new List<RomRetainedDescriptor>();
                for (int index = 0; index < stored.Length; index++)
                {
                    var file = published[index + 1].File;
                    var member = stored[index].member;
                    if (file.Sha256 != member.Identity.Sha256 || file.Size != member.Identity.Size)
                        throw new InvalidDataException("Descriptor custody differs from its measured bytes.");
                    retainedDescriptors.Add(new(stored[index].ordinal, file.Id, file.Sha256, file.Size));
                }
                if (publish.Retained is { } current)
                {
                    if (!measured.MatchesRetained(current) || current.FileId != published[0].File.Id ||
                        !current.Descriptors.OrderBy(row => row.Ordinal).SequenceEqual(retainedDescriptors.OrderBy(row => row.Ordinal)) ||
                        current.Outputs.Any(output => output.RomId != binRomIds[manifest.Members[((RomOutputSelector.Member)output.Selector).Ordinal].Path]))
                        throw new InvalidDataException("A retained graph must describe these exact staged artifact, descriptor and logical owner identities.");
                    bool appended = await facts.AppendVerificationAsync(current.Id, measured.Measurement, ct);
                    retained = appended ? new(current.Id, current.FileId, current.ArtifactSha256, current.ArtifactSize, current.Container, current.Manifest,
                        current.Outputs, [.. current.Verifications, measured.Measurement], current.Descriptors, current.Creations, current.CdBootInspections) : current;
                }
                else
                {
                    var owners = measured.Measurement.Outputs.Select(output =>
                    {
                        int ordinal = ((RomOutputSelector.Member)output.Selector).Ordinal;
                        return new MediaOutputOwner(output.Selector, binRomIds[manifest.Members[ordinal].Path], false);
                    }).ToArray();
                    retained = await facts.PublishAsync(published[0].File.Id, measured.Container, manifest, measured.Measurement, owners, retainedDescriptors, ct);
                }
            }
            else
                throw new InvalidDataException("Publication requires explicit retained or incoming artifact authority.");
            var outcome = await gate.CompleteAsync(retained, ct);
            await work.FlushAsync(ct);
            await UploadOutcomeCommit.CommitAsync(work, transaction, outcomes, outcome, ct);
            return retained;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            List<Exception> cleanup = [];
            for (int index = staged.Count - 1; index >= 0; index--)
                try
                { await staged[index].DisposeAsync(); }
                catch (Exception error) { cleanup.Add(error); }
            if (cleanup.Count != 0)
                throw new AggregateException("CD archive staging cleanup failed.", failure is null ? cleanup : [failure, .. cleanup]);
        }
    }
}
