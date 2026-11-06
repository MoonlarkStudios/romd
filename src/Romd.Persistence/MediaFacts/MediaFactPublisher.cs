using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.MediaFacts;

/// <summary>Publishes measured facts through the worker-only proof entry point in the caller's transaction.</summary>
public sealed class MediaFactPublisher(RomdDbContext context) : IMediaFactPublisher
{
    public async Task<int> CreateLogicalAsync(string originalFilename, RomLogicalIdentity identity, Guid createdBy,
        DateTimeOffset createdAt, CancellationToken ct = default)
    {
        RequireTransaction(ct);
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(originalFilename) || originalFilename.Length > 500 || originalFilename.Any(char.IsControl))
            throw new ArgumentException("A logical ROM requires its original filename.", nameof(originalFilename));
        return await InsertIdAsync($"""
            INSERT INTO romd."Roms" ("OriginalFilename","ContentSha256","ContentSize","Sha1","Md5","Crc32","CreatedAt","CreatedByUserId")
            VALUES ({originalFilename},{identity.Sha256.ToArray()},{identity.Size},{identity.Sha1.ToArray()},{identity.Md5.ToArray()},
                {identity.Crc32.ToArray()},{createdAt.ToUnixTimeMilliseconds()},{createdBy}) RETURNING "Id" AS "Value"
            """, ct);
    }

    public async Task<RomRepresentationFacts> PublishAsync(int artifactFileId, RomContainerFacts container, RomReconstructionManifest manifest,
        RomRepresentationVerification verification, IReadOnlyList<MediaOutputOwner> owners,
        IReadOnlyList<RomRetainedDescriptor> descriptors, CancellationToken ct = default)
    {
        RequireTransaction(ct);
        var transaction = context.Database.CurrentTransaction!;
        const string savepoint = "media_fact_publication";
        await transaction.CreateSavepointAsync(savepoint, ct);
        try
        {
            var published = await PublishCoreAsync(artifactFileId, container, manifest, verification, owners, descriptors, ct);
            await transaction.ReleaseSavepointAsync(savepoint, ct);
            return published;
        }
        catch (Exception primary)
        {
            try
            {
                await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                await transaction.ReleaseSavepointAsync(savepoint, CancellationToken.None);
            }
            catch (Exception recovery)
            {
                if (primary is not OperationCanceledException)
                {
                    recovery.Data["Romd.MediaFacts.PublicationFailure"] = primary;
                    throw;
                }
                primary.Data["Romd.MediaFacts.SavepointRecoveryFailure"] = recovery;
            }
            throw;
        }
    }

    private async Task<RomRepresentationFacts> PublishCoreAsync(int artifactFileId, RomContainerFacts container, RomReconstructionManifest manifest,
        RomRepresentationVerification verification, IReadOnlyList<MediaOutputOwner> owners,
        IReadOnlyList<RomRetainedDescriptor> descriptors, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(artifactFileId);
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(descriptors);
        var capturedOwners = owners.ToArray();
        var capturedDescriptors = descriptors.ToArray();
        if (capturedOwners.Length != verification.Outputs.Count || capturedOwners.Any(owner => owner is null || owner.Selector is null ||
                owner.RomId is <= 0 || owner.Preferred && owner.RomId is null) ||
            capturedOwners.Select(owner => owner.Selector).Distinct().Count() != capturedOwners.Length ||
            capturedOwners.Any(owner => !verification.Outputs.Any(output => output.Selector == owner.Selector)) ||
            capturedOwners.Where(owner => owner.Preferred).GroupBy(owner => owner.RomId).Any(group => group.Count() > 1))
            throw new ArgumentException("Publication requires one explicit association per measured output and one preference per logical ROM.", nameof(owners));
        if (verification.ObservedParametersSha256 != container.Fingerprint)
            throw new ArgumentException("Publication requires independently measured container parameters.", nameof(verification));

        int[] romIds = capturedOwners.Where(owner => owner.RomId is not null).Select(owner => owner.RomId!.Value).Distinct().Order().ToArray();
        await context.Database.ExecuteSqlRawAsync("""SELECT 1 FROM romd."Roms" WHERE "Id" = ANY (@ids) ORDER BY "Id" FOR NO KEY UPDATE""",
            [new NpgsqlParameter("ids", romIds)], ct);
        int id = await InsertIdAsync($"""
            INSERT INTO romd."Representations" ("FileId","Encoding") VALUES ({artifactFileId},{container.Encoding.ToString()}) RETURNING "Id" AS "Value"
            """, ct);
        await InsertContainerAsync(id, container, ct);
        if (manifest is RomReconstructionManifest.CdTracks cd)
            await InsertManifestAsync(id, cd, capturedDescriptors, ct);
        var outputs = new List<RomRepresentationOutput>();
        foreach (var measured in verification.Outputs)
        {
            var owner = capturedOwners.Single(owner => owner.Selector == measured.Selector);
            var identity = measured.Identity;
            int? ordinal = (measured.Selector as RomOutputSelector.Member)?.Ordinal;
            int outputId = await InsertIdAsync($"""
                INSERT INTO romd."RepresentationOutputs" ("RepresentationId","MemberOrdinal","RomId","ContentSha256","ContentSize","Sha1","Md5","Crc32")
                VALUES ({id},{ordinal},{owner.RomId},{identity.Sha256.ToArray()},{identity.Size},{identity.Sha1.ToArray()},{identity.Md5.ToArray()},{identity.Crc32.ToArray()})
                RETURNING "Id" AS "Value"
                """, ct);
            outputs.Add(new(outputId, owner.RomId, measured.Selector, identity));
        }
        var retained = new RomRepresentationFacts(id, artifactFileId, verification.ArtifactSha256, verification.ArtifactSize,
            container, manifest, outputs, [verification], capturedDescriptors);
        await InsertVerificationAsync(retained, verification, ct);
        foreach (var owner in capturedOwners.Where(owner => owner.Preferred))
        {
            int outputId = outputs.Single(output => output.Selector == owner.Selector).Id;
            await context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE romd."Roms" SET "PreferredOutputId" = {outputId} WHERE "Id" = {owner.RomId}""", ct);
        }
        return retained;
    }

    public async Task<bool> AppendVerificationAsync(int representationId, RomRepresentationVerification verification, CancellationToken ct = default)
    {
        RequireTransaction(ct);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(representationId);
        ArgumentNullException.ThrowIfNull(verification);
        // Match the database's publication lock order before reading the current set of decoder rows.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM romd."Roms" WHERE "Id" IN (SELECT "RomId" FROM romd."RepresentationOutputs" WHERE "RepresentationId" = {representationId})
                ORDER BY "Id" FOR NO KEY UPDATE;
            SELECT pg_advisory_xact_lock(9415,{representationId});
            SELECT 1 FROM romd."Representations" WHERE "Id" = {representationId} FOR NO KEY UPDATE;
            """, ct);
        var rows = await new MediaFactReader(context).ReadLockedPublicationAsync([representationId], ct);
        if (!rows.TryGetValue(representationId, out var retained))
            throw new InvalidDataException("The measured representation is no longer retained.");
        _ = new RomRepresentationFacts(retained.Id, retained.FileId, retained.ArtifactSha256, retained.ArtifactSize, retained.Container,
            retained.Manifest, retained.Outputs, [verification], retained.Descriptors);
        if (retained.Verifications.Any(proof => proof.Decoder.Equals(verification.Decoder)))
            return false;
        await InsertVerificationAsync(retained, verification, ct);
        return true;
    }

    private Task InsertVerificationAsync(RomRepresentationFacts retained, RomRepresentationVerification verification, CancellationToken ct)
    {
        var native = (verification.Decoder as RomDecoderIdentity.Native)?.Build;
        string build = native?.BuildLabel ?? ((RomDecoderIdentity.Managed)verification.Decoder).Build;
        string outputs = JsonSerializer.Serialize(verification.Outputs.Select(measured => new
        {
            OutputId = retained.Outputs.Single(output => output.Selector == measured.Selector).Id,
            ContentSha256 = measured.Identity.Sha256.ToString(),
            ContentSize = measured.Identity.Size,
            Sha1 = measured.Identity.Sha1.ToString(),
            Md5 = measured.Identity.Md5.ToString(),
            Crc32 = measured.Identity.Crc32.ToString()
        }));
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT romd.publish_media_verification({verification.Id},{retained.Id},{verification.ArtifactSha256.ToArray()},{verification.ArtifactSize},
                {build},CAST({native?.Rid} AS text),CAST({native?.UpstreamCommit.ToArray()} AS bytea),CAST({native?.RecipeVersion} AS integer),
                {verification.CheckedProfile},{verification.VerifierBuild},{verification.ObservedAt.UtcTicks},
                CAST({verification.ObservedParametersSha256?.ToArray()} AS bytea),CAST({verification.ComputedRawSha1?.ToArray()} AS bytea),
                CAST({verification.ComputedOverallSha1?.ToArray()} AS bytea),CAST({outputs} AS jsonb))
            """, ct);
    }

    private async Task InsertContainerAsync(int id, RomContainerFacts container, CancellationToken ct)
    {
        if (container is RomContainerFacts.Rvz rvz)
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."RvzContainerParameters" ("RepresentationId","Version","DiscType","Compression","CompressionLevel","BlockBytes","HasWiiPartitions","LogicalBytes")
                VALUES ({id},{(long)rvz.Version},{(long)rvz.DiscType},{(long)rvz.Compression},{rvz.CompressionLevel},{rvz.BlockBytes},{rvz.HasWiiPartitions},{rvz.LogicalBytes})
                """, ct);
        else if (container is RomContainerFacts.Chd chd)
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."ChdContainerParameters" ("RepresentationId","Version","LogicalBytes","HunkBytes","UnitBytes","HunkCount",
                    "Codec0","Codec1","Codec2","Codec3","HasParent","StoredRawSha1","StoredOverallSha1","MetadataSha256")
                VALUES ({id},{(int)chd.Version},{chd.LogicalBytes},{(int)chd.HunkBytes},{(int)chd.UnitBytes},{(long)chd.HunkCount},
                    {(long)chd.Codecs[0]},{(long)chd.Codecs[1]},{(long)chd.Codecs[2]},{(long)chd.Codecs[3]},{chd.HasParent},
                    {chd.StoredRawSha1.ToArray()},{chd.StoredOverallSha1.ToArray()},{chd.MetadataSha256.ToArray()})
                """, ct);
            for (int ordinal = 0; ordinal < chd.Metadata.Count; ordinal++)
            {
                var entry = chd.Metadata[ordinal];
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO romd."ChdMetadataEntries" ("RepresentationId","Ordinal","Tag","Flags","Payload")
                    VALUES ({id},{ordinal},{(long)entry.Tag},{(int)entry.Flags},{entry.Payload.ToArray()})
                    """, ct);
            }
        }
    }

    private async Task InsertManifestAsync(int id, RomReconstructionManifest.CdTracks cd, IReadOnlyList<RomRetainedDescriptor> descriptors, CancellationToken ct)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO romd."ReconstructionManifests" ("RepresentationId","LogicalFrames") VALUES ({id},{cd.LogicalFrames})""", ct);
        foreach (var track in cd.Tracks)
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."ReconstructionTracks" ("RepresentationId","Number","FirstFrame","Frames","Kind")
                VALUES ({id},{track.Number},{track.FirstFrame},{track.Frames},{track.Kind.ToString()})
                """, ct);
        for (int ordinal = 0; ordinal < cd.Members.Count; ordinal++)
        {
            var member = cd.Members[ordinal];
            var identity = member.Identity;
            int? storedFileId = member is RomCdStoredMember ? descriptors.Single(descriptor => descriptor.Ordinal == ordinal).FileId : null;
            string? storedRole = (member as RomCdStoredMember)?.Role.ToString();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."ReconstructionMembers" ("RepresentationId","Ordinal","Path","ContentSha256","ContentSize","Sha1","Md5","Crc32","StoredFileId","StoredRole")
                VALUES ({id},{ordinal},{member.Path},{identity.Sha256.ToArray()},{identity.Size},{identity.Sha1.ToArray()},{identity.Md5.ToArray()},{identity.Crc32.ToArray()},
                    {storedFileId},{storedRole})
                """, ct);
            if (member is RomCdBinMember bin)
                foreach (int number in bin.Tracks)
                    await context.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO romd."ReconstructionMemberTracks" ("RepresentationId","MemberOrdinal","TrackNumber") VALUES ({id},{ordinal},{number})
                        """, ct);
        }
    }

    private async Task<int> InsertIdAsync(FormattableString sql, CancellationToken ct) =>
        (await context.Database.SqlQuery<int>(sql).ToArrayAsync(ct)).Single();

    private void RequireTransaction(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Media publication requires the caller's ReadCommitted transaction.");
    }
}
