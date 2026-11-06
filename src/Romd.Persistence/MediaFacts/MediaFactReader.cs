using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Persistence.MediaFacts;

/// <summary>Materializes the complete guarded graph in a bounded set of queries, without stored policy decisions.</summary>
public sealed class MediaFactReader(RomdDbContext context) : IMediaFactReader
{
    public Task<IReadOnlyDictionary<int, RomRepresentationFacts>> ReadAsync(IReadOnlyCollection<int> representationIds,
        CancellationToken ct = default) => ReadCoreAsync(representationIds, false, ct);

    // Only publication code that already holds the logical/advisory/representation locks may use ReadCommitted.
    internal Task<IReadOnlyDictionary<int, RomRepresentationFacts>> ReadLockedPublicationAsync(IReadOnlyCollection<int> representationIds,
        CancellationToken ct) => ReadCoreAsync(representationIds, true, ct);

    private async Task<IReadOnlyDictionary<int, RomRepresentationFacts>> ReadCoreAsync(IReadOnlyCollection<int> representationIds,
        bool lockedPublication, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(representationIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(representationIds.Count, IMediaFactReader.MaximumBatchSize);
        foreach (int id in representationIds)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ct.ThrowIfCancellationRequested();
        int[] ids = representationIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new Dictionary<int, RomRepresentationFacts>();
        var ambientIsolation = context.Database.CurrentTransaction?.GetDbTransaction().IsolationLevel;
        if (lockedPublication ? ambientIsolation != IsolationLevel.ReadCommitted
            : ambientIsolation is not (null or IsolationLevel.RepeatableRead or IsolationLevel.Serializable))
            throw new InvalidOperationException("Batched media reads require a snapshot or the publisher's protected ReadCommitted transaction.");
        // Standalone reads need a snapshot across all tables, including concurrent retention cleanup.
        await using var snapshot = context.Database.CurrentTransaction is null
            ? await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        if (snapshot is not null)
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        Task<T[]> Rows<T>(string sql) => context.Database.SqlQueryRaw<T>(sql, new NpgsqlParameter("ids", ids)).ToArrayAsync(ct);
        var representations = await Rows<RepresentationRow>("""
            SELECT r."Id",r."FileId",r."Encoding",f."Sha256" AS "ArtifactSha256",f."Size" AS "ArtifactSize"
            FROM romd."Representations" r JOIN romd."Files" f ON f."Id" = r."FileId" WHERE r."Id" = ANY (@ids)
            """);
        var outputs = (await Rows<OutputRow>("""SELECT * FROM romd."RepresentationOutputs" WHERE "RepresentationId" = ANY (@ids)""")).ToLookup(row => row.RepresentationId);
        var chds = (await Rows<ChdRow>("""SELECT * FROM romd."ChdContainerParameters" WHERE "RepresentationId" = ANY (@ids)""")).ToDictionary(row => row.RepresentationId);
        var rvzs = (await Rows<RvzRow>("""SELECT * FROM romd."RvzContainerParameters" WHERE "RepresentationId" = ANY (@ids)""")).ToDictionary(row => row.RepresentationId);
        var metadata = (await Rows<MetadataRow>("""SELECT * FROM romd."ChdMetadataEntries" WHERE "RepresentationId" = ANY (@ids) ORDER BY "Ordinal" """)).ToLookup(row => row.RepresentationId);
        var manifests = (await Rows<ManifestRow>("""SELECT * FROM romd."ReconstructionManifests" WHERE "RepresentationId" = ANY (@ids)""")).ToDictionary(row => row.RepresentationId);
        var tracks = (await Rows<TrackRow>("""SELECT * FROM romd."ReconstructionTracks" WHERE "RepresentationId" = ANY (@ids) ORDER BY "Number" """)).ToLookup(row => row.RepresentationId);
        var members = (await Rows<MemberRow>("""
            SELECT m.*,f."Sha256" AS "StoredSha256",f."Size" AS "StoredSize" FROM romd."ReconstructionMembers" m
            LEFT JOIN romd."Files" f ON f."Id" = m."StoredFileId" WHERE m."RepresentationId" = ANY (@ids) ORDER BY m."Ordinal"
            """)).ToLookup(row => row.RepresentationId);
        var placements = (await Rows<PlacementRow>("""SELECT * FROM romd."ReconstructionMemberTracks" WHERE "RepresentationId" = ANY (@ids) ORDER BY "TrackNumber" """)).ToLookup(row => (row.RepresentationId, row.MemberOrdinal));
        var proofs = (await Rows<ProofRow>("""SELECT * FROM romd."Verifications" WHERE "RepresentationId" = ANY (@ids) ORDER BY "ObservedAtUtcTicks","Id" """)).ToLookup(row => row.RepresentationId);
        var measured = (await Rows<MeasuredRow>("""SELECT * FROM romd."VerificationOutputs" WHERE "RepresentationId" = ANY (@ids)""")).ToLookup(row => row.VerificationId);
        var creations = (await Rows<CreationRow>("""SELECT * FROM romd."RepresentationCreations" WHERE "RepresentationId" = ANY (@ids) ORDER BY "ComparedAtUtcTicks","Id" """)).ToLookup(row => row.RepresentationId);
        var sources = (await Rows<CreationSourceRow>("""
            SELECT s.* FROM romd."CreationSources" s JOIN romd."RepresentationCreations" c ON c."Id" = s."CreationId"
            WHERE c."RepresentationId" = ANY (@ids) ORDER BY s."TargetOutputId"
            """)).ToLookup(row => row.CreationId);
        var cdBoots = (await Rows<CdBootRow>("""SELECT * FROM romd."CdBootInspections" WHERE "RepresentationId" = ANY (@ids) ORDER BY "ObservedAtUtcTicks","Id" """)).ToLookup(row => row.RepresentationId);
        var cdEncodings = (await Rows<CdEncodingRow>("""SELECT * FROM romd."CdEncodingObservations" WHERE "RepresentationId" = ANY (@ids)""")).ToDictionary(row => row.CreationId);
        var result = new Dictionary<int, RomRepresentationFacts>();
        try
        {
            foreach (var representation in representations)
            {
                int id = representation.Id;
                RomContainerFacts container = EnumValue<RomRepresentationEncoding>(representation.Encoding) switch
                {
                    RomRepresentationEncoding.Identity => RomContainerFacts.Identity.Instance,
                    RomRepresentationEncoding.Rvz when rvzs.TryGetValue(id, out var rvz) => new RomContainerFacts.Rvz(
                        checked((uint)rvz.Version), checked((uint)rvz.DiscType), checked((uint)rvz.Compression), rvz.CompressionLevel, rvz.BlockBytes, rvz.HasWiiPartitions, rvz.LogicalBytes),
                    RomRepresentationEncoding.Chd when chds.TryGetValue(id, out var chd) => Chd(chd, metadata[id].ToArray()),
                    _ => throw new InvalidDataException("Captured container facts are missing.")
                };
                RomReconstructionManifest manifest = RomReconstructionManifest.WholeFile.Instance;
                var retained = members[id].ToArray();
                if (manifests.TryGetValue(id, out var cd))
                {
                    RequireOrdinals(retained.Select(row => row.Ordinal));
                    manifest = new RomReconstructionManifest.CdTracks(cd.LogicalFrames,
                        tracks[id].Select(row => new RomCdTrack(row.Number, row.FirstFrame, row.Frames, EnumValue<RomCdTrackKind>(row.Kind))).ToArray(),
                        retained.Select<MemberRow, RomCdFileMember>(row => row.StoredFileId is not null
                            ? new RomCdStoredMember(row.Path, CdIdentity(row), EnumValue<RomCdStoredRole>(row.StoredRole))
                            : new RomCdBinMember(row.Path, CdIdentity(row), CdIdentity(row), placements[(id, row.Ordinal)].Select(track => track.TrackNumber).ToArray())).ToArray());
                }
                var declared = outputs[id].Select(row => new RomRepresentationOutput(row.Id, row.RomId, Selector(row.MemberOrdinal), Identity(row))).ToArray();
                var byId = declared.ToDictionary(output => output.Id);
                if (creations[id].Any(row => (row.EncoderRid is null) != (row.EncoderSourceTag is null) ||
                    (row.EncoderRid is null) != (row.EncoderSourceSha256 is null) || (row.EncoderRid is null) != (row.EncoderRecipeVersion is null)))
                    throw new InvalidDataException("Retained native encoder identity is incomplete.");
                var verifications = proofs[id].Select(row =>
                {
                    Sha256? observed = row.ObservedParametersSha256 is null ? null : Sha256.FromSpan(row.ObservedParametersSha256);
                    if (observed != container.Fingerprint)
                        throw new InvalidDataException("Retained parameter observation disagrees.");
                    return new RomRepresentationVerification(row.Id,
                    Sha256.FromSpan(row.ArtifactSha256), row.ArtifactSize,
                    row.DecoderRid is not null ? new RomDecoderIdentity.Native(new(row.DecoderRid, Sha1.FromSpan(row.DecoderUpstreamCommit ?? []), row.DecoderRecipeVersion ?? 0))
                        : new RomDecoderIdentity.Managed(row.DecoderBuild), row.CheckedProfile, row.VerifierBuild, new DateTimeOffset(row.ObservedAtUtcTicks, TimeSpan.Zero),
                    measured[row.Id].Select(output => new RomMeasuredOutput(
                        byId.TryGetValue(output.OutputId, out var declaredOutput) ? declaredOutput.Selector : throw new InvalidDataException("Measured output is missing."), Identity(output))).ToArray(), container,
                    row.ComputedRawSha1 is null ? null : Sha1.FromSpan(row.ComputedRawSha1), row.ComputedOverallSha1 is null ? null : Sha1.FromSpan(row.ComputedOverallSha1));
                }).ToArray();
                result.Add(id, new(id, representation.FileId, Sha256.FromSpan(representation.ArtifactSha256), representation.ArtifactSize, container, manifest, declared, verifications,
                    retained.Where(row => row.StoredFileId is not null).Select(row => new RomRetainedDescriptor(row.Ordinal, row.StoredFileId!.Value,
                        Sha256.FromSpan(row.StoredSha256 ?? []), row.StoredSize ?? -1)).ToArray(),
                    creations[id].Select(row => new RomRepresentationCreation(row.Id, row.VerificationId, RecipeId.Parse(row.RecipeId), row.EncoderBuild,
                        new(row.ComparedAtUtcTicks, TimeSpan.Zero), Sha256.FromSpan(row.ParametersSha256),
                        row.EncoderRid is not null ? new(row.EncoderRid, row.EncoderSourceTag ?? "", Sha256.FromSpan(row.EncoderSourceSha256 ?? []), row.EncoderRecipeVersion ?? 0) : null,
                        sources[row.Id].Select(source => new RomCreationSource(source.TargetOutputId, source.SourceRepresentationId, source.SourceOutputId,
                            source.SourceVerificationId, source.SourceFileId, Sha256.FromSpan(source.SourceArtifactSha256), source.SourceArtifactSize)).ToArray(),
                        cdEncodings.TryGetValue(row.Id, out var cdEncoding) ? new(cdEncoding.BootInspectionId, Sha256.FromSpan(cdEncoding.SourceRequestSha256), cdEncoding.SourceRequestBytes) : null)).ToArray(),
                    cdBoots[id].Select(row => new RomCdBootInspection(row.Id, row.VerificationId, row.InspectorBuild, new(row.ObservedAtUtcTicks, TimeSpan.Zero), row.Boot())).ToArray()));
            }
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Retained media facts are incomplete or contradictory.", error);
        }
        if (snapshot is not null)
            await snapshot.CommitAsync(ct);
        return result;
    }

    private static RomContainerFacts.Chd Chd(ChdRow row, MetadataRow[] metadata)
    {
        RequireOrdinals(metadata.Select(entry => entry.Ordinal));
        var captured = new RomContainerFacts.Chd(checked((uint)row.Version), row.LogicalBytes, checked((uint)row.HunkBytes), checked((uint)row.UnitBytes), checked((uint)row.HunkCount),
            [checked((uint)row.Codec0), checked((uint)row.Codec1), checked((uint)row.Codec2), checked((uint)row.Codec3)], row.HasParent, Sha1.FromSpan(row.StoredRawSha1), Sha1.FromSpan(row.StoredOverallSha1),
            metadata.Select(entry => new RomChdMetadataEntry(checked((uint)entry.Tag), checked((byte)entry.Flags), entry.Payload)).ToArray());
        if (captured.MetadataSha256 != Sha256.FromSpan(row.MetadataSha256))
            throw new InvalidDataException("Retained CHD metadata fingerprint disagrees.");
        return captured;
    }
    private static void RequireOrdinals(IEnumerable<int> ordinals)
    {
        var captured = ordinals.ToArray();
        if (!captured.SequenceEqual(Enumerable.Range(0, captured.Length)))
            throw new InvalidDataException("Retained member ordering is incomplete.");
    }
    private static T EnumValue<T>(string? text) where T : struct, Enum =>
        Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value) && value.ToString() == text ? value : throw new InvalidDataException("Retained fact type is unknown.");
    private static RomOutputSelector Selector(int? ordinal) => ordinal is { } member ? new RomOutputSelector.Member(member) : RomOutputSelector.Whole.Instance;
    private static RomLogicalIdentity Identity(IdentityRow row) => new(row.ContentSize, Sha256.FromSpan(row.ContentSha256), Sha1.FromSpan(row.Sha1), Md5.FromSpan(row.Md5), Crc32.FromSpan(row.Crc32));
    private static RomCdFileIdentity CdIdentity(IdentityRow row) => new(row.ContentSize, Sha256.FromSpan(row.ContentSha256), Sha1.FromSpan(row.Sha1), Md5.FromSpan(row.Md5), Crc32.FromSpan(row.Crc32));

    private sealed class CdEncodingRow
    {
        public Guid CreationId { get; init; }
        public Guid BootInspectionId { get; init; }
        public byte[] SourceRequestSha256 { get; init; } = []; public long SourceRequestBytes { get; init; }
    }
    private sealed class CdBootRow
    {
        public Guid Id { get; init; }
        public int RepresentationId { get; init; }
        public Guid VerificationId { get; init; }
        public string InspectorBuild { get; init; } = ""; public string Profile { get; init; } = ""; public long ObservedAtUtcTicks { get; init; }
        public long FirstTrackIndex01 { get; init; }
        public long VolumeBlocks { get; init; }
        public long RootBlock { get; init; }
        public long RootBytes { get; init; }
        public string BootPath { get; init; } = ""; public long BootBlock { get; init; }
        public long BootBytes { get; init; }
        public long EntryPoint { get; init; }
        public long LoadAddress { get; init; }
        public long LoadBytes { get; init; }
        public RomPs1CdBoot Boot() => new(Profile, FirstTrackIndex01, checked((uint)VolumeBlocks), checked((uint)RootBlock), checked((uint)RootBytes), BootPath,
            checked((uint)BootBlock), checked((uint)BootBytes), checked((uint)EntryPoint), checked((uint)LoadAddress), checked((uint)LoadBytes));
    }
    private sealed class CreationRow
    {
        public Guid Id { get; init; }
        public int RepresentationId { get; init; }
        public Guid VerificationId { get; init; }
        public string RecipeId { get; init; } = ""; public string EncoderBuild { get; init; } = "";
        public long ComparedAtUtcTicks { get; init; }
        public byte[] ParametersSha256 { get; init; } = [];
        public string? EncoderRid { get; init; }
        public string? EncoderSourceTag { get; init; }
        public byte[]? EncoderSourceSha256 { get; init; }
        public int? EncoderRecipeVersion { get; init; }
    }
    private sealed class CreationSourceRow
    {
        public Guid CreationId { get; init; }
        public int TargetOutputId { get; init; }
        public int SourceRepresentationId { get; init; }
        public int SourceOutputId { get; init; }
        public Guid SourceVerificationId { get; init; }
        public int SourceFileId { get; init; }
        public byte[] SourceArtifactSha256 { get; init; } = []; public long SourceArtifactSize { get; init; }
    }
    private sealed class RepresentationRow
    {
        public int Id { get; init; }
        public int FileId { get; init; }
        public string Encoding { get; init; } = "";
        public byte[] ArtifactSha256 { get; init; } = []; public long ArtifactSize { get; init; }
    }
    private class IdentityRow
    {
        public byte[] ContentSha256 { get; init; } = []; public long ContentSize { get; init; }
        public byte[] Sha1 { get; init; } = []; public byte[] Md5 { get; init; } = []; public byte[] Crc32 { get; init; } = [];
    }
    private sealed class OutputRow : IdentityRow
    {
        public int Id { get; init; }
        public int RepresentationId { get; init; }
        public int? MemberOrdinal { get; init; }
        public int? RomId { get; init; }
    }
    private sealed class MemberRow : IdentityRow
    {
        public int RepresentationId { get; init; }
        public int Ordinal { get; init; }
        public string Path { get; init; } = "";
        public int? StoredFileId { get; init; }
        public string? StoredRole { get; init; }
        public byte[]? StoredSha256 { get; init; }
        public long? StoredSize { get; init; }
    }
    private sealed class MeasuredRow : IdentityRow
    {
        public Guid VerificationId { get; init; }
        public int OutputId { get; init; }
    }
    private sealed class ManifestRow { public int RepresentationId { get; init; } public long LogicalFrames { get; init; } }
    private sealed class TrackRow
    {
        public int RepresentationId { get; init; }
        public int Number { get; init; }
        public long FirstFrame { get; init; }
        public int Frames { get; init; }
        public string Kind { get; init; } = "";
    }
    private sealed class PlacementRow { public int RepresentationId { get; init; } public int MemberOrdinal { get; init; } public int TrackNumber { get; init; } }
    private sealed class MetadataRow
    {
        public int RepresentationId { get; init; }
        public int Ordinal { get; init; }
        public long Tag { get; init; }
        public int Flags { get; init; }
        public byte[] Payload { get; init; } = [];
    }
    private sealed class RvzRow
    {
        public int RepresentationId { get; init; }
        public long Version { get; init; }
        public long DiscType { get; init; }
        public long Compression { get; init; }
        public int CompressionLevel { get; init; }
        public int BlockBytes { get; init; }
        public bool HasWiiPartitions { get; init; }
        public long LogicalBytes { get; init; }
    }
    private sealed class ChdRow
    {
        public int RepresentationId { get; init; }
        public int Version { get; init; }
        public long LogicalBytes { get; init; }
        public int HunkBytes { get; init; }
        public int UnitBytes { get; init; }
        public long HunkCount { get; init; }
        public long Codec0 { get; init; }
        public long Codec1 { get; init; }
        public long Codec2 { get; init; }
        public long Codec3 { get; init; }
        public bool HasParent { get; init; }
        public byte[] StoredRawSha1 { get; init; } = []; public byte[] StoredOverallSha1 { get; init; } = []; public byte[] MetadataSha256 { get; init; } = [];
    }
    private sealed class ProofRow
    {
        public Guid Id { get; init; }
        public int RepresentationId { get; init; }
        public byte[] ArtifactSha256 { get; init; } = []; public long ArtifactSize { get; init; }
        public string DecoderBuild { get; init; } = ""; public string? DecoderRid { get; init; }
        public byte[]? DecoderUpstreamCommit { get; init; }
        public int? DecoderRecipeVersion { get; init; }
        public string CheckedProfile { get; init; } = ""; public string VerifierBuild { get; init; } = ""; public long ObservedAtUtcTicks { get; init; }
        public byte[]? ObservedParametersSha256 { get; init; }
        public byte[]? ComputedRawSha1 { get; init; }
        public byte[]? ComputedOverallSha1 { get; init; }
    }
}
