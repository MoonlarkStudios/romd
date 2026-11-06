using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>Domain bounds for DVD CHD evidence; Media owns the container limits and a cross-layer test pins these to them.</summary>
public static class RomChdDvdLimits
{
    public const long MaximumArtifactBytes = 10L << 30;
    public const long MaximumLogicalBytes = 10L << 30;
    public const uint MaximumHunkBytes = 1U << 20;
    public const uint MaximumHunkCount = 2621440;
}

/// <summary>Version 5 CHD codec FourCCs a DVD container may name; a cross-layer test pins these to the decoder's identifiers.</summary>
public static class RomChdCodecs
{
    public const uint None = 0;
    public const uint Zlib = 0x7a6c6962;
    public const uint Lzma = 0x6c7a6d61;
    public const uint Huffman = 0x68756666;
    public const uint Flac = 0x666c6163;
    public const uint Zstd = 0x7a737464;
}

/// <summary>Exact caller-observed singleton DVD marker; validation does not read a container.</summary>
public sealed record RomChdDvdMetadataFacts
{
    public const string ProfileVersion = "chd-dvd-metadata/v1";
    // SHA256 of tag BE32, flags byte, payload length BE32, payload: 44564420010000000100.
    public static readonly Sha256 SingletonFingerprint = Sha256.Parse("c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406");

    public RomChdDvdMetadataFacts(int entryCount, uint tag, byte flags, uint payloadBytes, byte payloadByte,
        string profile, Sha256 fingerprint)
    {
        if (entryCount != 1 || tag != 0x44564420 || flags != 1 || payloadBytes != 1 || payloadByte != 0 ||
            profile != ProfileVersion || fingerprint != SingletonFingerprint)
            throw new ArgumentException("DVD metadata requires the exact observed singleton marker and fingerprint.");
        EntryCount = entryCount;
        Tag = tag;
        Flags = flags;
        PayloadBytes = payloadBytes;
        PayloadByte = payloadByte;
        Profile = profile;
        Fingerprint = fingerprint;
    }

    public int EntryCount { get; }
    public uint Tag { get; }
    public byte Flags { get; }
    public uint PayloadBytes { get; }
    public byte PayloadByte { get; }
    public string Profile { get; }
    public Sha256 Fingerprint { get; }
}

/// <summary>Immutable container parameter observation; neither canonical policy nor physical DVD proof.</summary>
public sealed record RomChdContainerParameters
{
    public RomChdContainerParameters(uint version, long logicalBytes, uint hunkBytes, uint unitBytes, uint hunkCount,
        uint codec0, uint codec1, uint codec2, uint codec3, bool hasParent, RomChdDvdMetadataFacts metadata,
        string capturedEncodingProfile)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (version != 5 || hasParent || logicalBytes is < 2048 or > RomChdDvdLimits.MaximumLogicalBytes || unitBytes != 2048 ||
            logicalBytes % 2048 != 0 || hunkBytes is < 2048 or > RomChdDvdLimits.MaximumHunkBytes || hunkBytes % 2048 != 0 ||
            hunkCount is 0 or > RomChdDvdLimits.MaximumHunkCount ||
            (ulong)hunkCount != (ulong)logicalBytes / hunkBytes + ((ulong)logicalBytes % hunkBytes == 0 ? 0UL : 1UL) ||
            !AllowedCodec(codec0) || !AllowedCodec(codec1) || !AllowedCodec(codec2) || !AllowedCodec(codec3))
            throw new ArgumentException("CHD DVD parameters require bounded version5 geometry, non-CD codecs and no parent.");
        ArgumentException.ThrowIfNullOrWhiteSpace(capturedEncodingProfile);
        if (capturedEncodingProfile.Length > 512 || capturedEncodingProfile.Any(char.IsControl))
            throw new ArgumentException("Invalid captured CHD parameter label.", nameof(capturedEncodingProfile));
        Version = version;
        LogicalBytes = logicalBytes;
        HunkBytes = hunkBytes;
        UnitBytes = unitBytes;
        HunkCount = hunkCount;
        Codec0 = codec0;
        Codec1 = codec1;
        Codec2 = codec2;
        Codec3 = codec3;
        HasParent = hasParent;
        Metadata = metadata;
        CapturedEncodingProfile = capturedEncodingProfile;
    }

    public uint Version { get; }
    public long LogicalBytes { get; }
    public uint HunkBytes { get; }
    public uint UnitBytes { get; }
    public uint HunkCount { get; }
    public uint Codec0 { get; }
    public uint Codec1 { get; }
    public uint Codec2 { get; }
    public uint Codec3 { get; }
    public bool HasParent { get; }
    public RomChdDvdMetadataFacts Metadata { get; }
    public string CapturedEncodingProfile { get; }

    private static bool AllowedCodec(uint codec) => codec is RomChdCodecs.None or RomChdCodecs.Zlib or RomChdCodecs.Lzma or
        RomChdCodecs.Huffman or RomChdCodecs.Flac or RomChdCodecs.Zstd;
}

/// <summary>The decoder build that measured a CHD: its RID, libchdr upstream source pin and build recipe version.</summary>
/// <remarks>Binary bytes are deliberately absent, so rebuilding an image never invalidates stored evidence; a different
/// pin or recipe needs fresh verification before serving. Shape only: serving approval is a separate admission.</remarks>
public sealed record RomChdDecoderBuild
{
    public RomChdDecoderBuild(string rid, Sha1 upstreamCommit, int recipeVersion)
    {
        if (!RomNativeBuildLabels.IsRid(rid) || upstreamCommit.IsEmpty || recipeVersion < 1)
            throw new ArgumentException("CHD decoder evidence requires a bounded RID, a source pin and a recipe version.");
        Rid = rid;
        UpstreamCommit = upstreamCommit;
        RecipeVersion = recipeVersion;
    }

    public string Rid { get; }
    public Sha1 UpstreamCommit { get; }
    public int RecipeVersion { get; }
    public string BuildLabel => $"libchdr/{Rid}/pin:{UpstreamCommit}/recipe:{RecipeVersion.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>Shape rules shared by native build evidence labels.</summary>
internal static class RomNativeBuildLabels
{
    /// <summary>A bounded portable RID or tag label: lowercase ASCII letters, digits, '.' and '-'.</summary>
    public static bool IsRid(string? value) => value is { Length: > 0 and <= 32 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');
}

/// <summary>Stored/computed container hashes tied to one exact independent logical measurement.</summary>
/// <remarks>Internal SHA1 values are evidence, not file or delivered identity. This never approves a decoder for serving.</remarks>
public sealed record RomChdIntegrity
{
    public const string Claim = "container-integrity/v1";
    public const string ProfileVersion = "chd-v5-raw-overall/v1";

    private RomChdIntegrity(Guid id, Guid verificationId, Sha256 artifactSha256, long artifactSize,
        Sha256 contentSha256, long contentSize, Sha1 sha1, Md5 md5, Crc32 crc32,
        RomChdContainerParameters parameters, Sha1 storedRawSha1, Sha1 computedRawSha1,
        Sha1 storedOverallSha1, Sha1 computedOverallSha1, RomChdDecoderBuild decoder,
        string measurementProfile, string verifierBuild, DateTimeOffset measurementVerifiedAt, string profile, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(decoder);
        if (id == Guid.Empty || verificationId == Guid.Empty || artifactSha256.IsEmpty || contentSha256.IsEmpty ||
            sha1.IsEmpty || md5.IsEmpty || crc32.IsEmpty || artifactSize is < 124 or > RomChdDvdLimits.MaximumArtifactBytes ||
            contentSize != parameters.LogicalBytes || profile != ProfileVersion ||
            !ValidLabel(measurementProfile, 128) || !ValidLabel(verifierBuild, 256) ||
            storedRawSha1.IsEmpty || computedRawSha1.IsEmpty || storedOverallSha1.IsEmpty || computedOverallSha1.IsEmpty ||
            storedRawSha1 != computedRawSha1 || storedOverallSha1 != computedOverallSha1 || computedRawSha1 != sha1)
            throw new ArgumentException("CHD integrity must bind complete measured identity, parameters and matching stored/computed hashes.");
        Id = id;
        VerificationId = verificationId;
        ArtifactSha256 = artifactSha256;
        ArtifactSize = artifactSize;
        ContentSha256 = contentSha256;
        ContentSize = contentSize;
        Sha1 = sha1;
        Md5 = md5;
        Crc32 = crc32;
        Parameters = parameters;
        StoredRawSha1 = storedRawSha1;
        ComputedRawSha1 = computedRawSha1;
        StoredOverallSha1 = storedOverallSha1;
        ComputedOverallSha1 = computedOverallSha1;
        MeasurementProfile = measurementProfile;
        VerifierBuild = verifierBuild;
        MeasurementVerifiedAt = measurementVerifiedAt.ToUniversalTime();
        Decoder = decoder;
        Profile = profile;
        ObservedAt = observedAt.ToUniversalTime();
    }

    public Guid Id { get; }
    public Guid VerificationId { get; }
    public Sha256 ArtifactSha256 { get; }
    public long ArtifactSize { get; }
    public Sha256 ContentSha256 { get; }
    public long ContentSize { get; }
    public Sha1 Sha1 { get; }
    public Md5 Md5 { get; }
    public Crc32 Crc32 { get; }
    public RomChdContainerParameters Parameters { get; }
    public Sha1 StoredRawSha1 { get; }
    public Sha1 ComputedRawSha1 { get; }
    public Sha1 StoredOverallSha1 { get; }
    public Sha1 ComputedOverallSha1 { get; }
    public RomChdDecoderBuild Decoder { get; }
    public string MeasurementProfile { get; }
    public string VerifierBuild { get; }
    /// <summary>The bound measurement's UTC timestamp; separate from this container observation.</summary>
    public DateTimeOffset MeasurementVerifiedAt { get; }
    public string Profile { get; }
    public DateTimeOffset ObservedAt { get; }

    public static bool TryCreate(RomVerification? independentMeasurement, RomChdContainerParameters? parameters,
        Sha1 storedRawSha1, Sha1 computedRawSha1, Sha1 storedOverallSha1, Sha1 computedOverallSha1,
        RomChdDecoderBuild? decoder, string? profile, DateTimeOffset observedAt,
        [NotNullWhen(true)] out RomChdIntegrity? integrity)
    {
        integrity = null;
        if (independentMeasurement is not { Encoding: RomRepresentationEncoding.Chd } measurement || parameters is null || decoder is null ||
            measurement.ArtifactSize is < 124 or > RomChdDvdLimits.MaximumArtifactBytes || measurement.Crc32.IsEmpty ||
            measurement.ContentSize != parameters.LogicalBytes || measurement.CapturedEncodingProfile != parameters.CapturedEncodingProfile ||
            measurement.DecoderBuild != decoder.BuildLabel || profile != ProfileVersion ||
            !ValidLabel(measurement.Profile, 128) || !ValidLabel(measurement.VerifierBuild, 256) ||
            storedRawSha1.IsEmpty || computedRawSha1.IsEmpty || storedOverallSha1.IsEmpty || computedOverallSha1.IsEmpty ||
            storedRawSha1 != computedRawSha1 || storedOverallSha1 != computedOverallSha1 || computedRawSha1 != measurement.Sha1)
            return false;
        integrity = new(Guid.NewGuid(), measurement.Id, measurement.ArtifactSha256, measurement.ArtifactSize,
            measurement.ContentSha256, measurement.ContentSize, measurement.Sha1, measurement.Md5, measurement.Crc32,
            parameters, storedRawSha1, computedRawSha1, storedOverallSha1, computedOverallSha1, decoder,
            measurement.Profile, measurement.VerifierBuild, measurement.VerifiedAt, profile, observedAt);
        return true;
    }

    public bool MatchesMeasurement(RomVerification? measurement) => measurement is { Encoding: RomRepresentationEncoding.Chd } &&
        measurement.Id == VerificationId && measurement.ArtifactSha256 == ArtifactSha256 && measurement.ArtifactSize == ArtifactSize &&
        measurement.ContentSha256 == ContentSha256 && measurement.ContentSize == ContentSize && measurement.Sha1 == Sha1 &&
        measurement.Md5 == Md5 && measurement.Crc32 == Crc32 && measurement.DecoderBuild == Decoder.BuildLabel &&
        measurement.Profile == MeasurementProfile && measurement.VerifierBuild == VerifierBuild && measurement.VerifiedAt == MeasurementVerifiedAt &&
        measurement.CapturedEncodingProfile == Parameters.CapturedEncodingProfile;

    public static RomChdIntegrity Rehydrate(Guid id, Guid verificationId, Sha256 artifactSha256, long artifactSize,
        Sha256 contentSha256, long contentSize, Sha1 sha1, Md5 md5, Crc32 crc32, RomChdContainerParameters parameters,
        Sha1 storedRawSha1, Sha1 computedRawSha1, Sha1 storedOverallSha1, Sha1 computedOverallSha1,
        RomChdDecoderBuild decoder, string measurementProfile, string verifierBuild, DateTimeOffset measurementVerifiedAt,
        string profile, DateTimeOffset observedAt) =>
        new(id, verificationId, artifactSha256, artifactSize, contentSha256, contentSize, sha1, md5, crc32,
            parameters, storedRawSha1, computedRawSha1, storedOverallSha1, computedOverallSha1, decoder,
            measurementProfile, verifierBuild, measurementVerifiedAt, profile, observedAt);

    private static bool ValidLabel(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);
}
