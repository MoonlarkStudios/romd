using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>
/// An immutable observation of a complete output. This alone claims neither DAT membership,
/// container integrity, source-set equivalence nor emulator compatibility.
/// </summary>
public sealed record RomVerification
{
    public RomVerification(Sha256 artifactSha256, long artifactSize, RomRepresentationEncoding encoding,
        Sha256 contentSha256, long contentSize, Sha1 sha1, Md5 md5, Crc32 crc32,
        string profile, string decoderBuild, string verifierBuild, DateTimeOffset verifiedAt,
        string? capturedEncodingProfile = null)
        : this(Guid.NewGuid(), artifactSha256, artifactSize, encoding, contentSha256, contentSize,
            sha1, md5, crc32, profile, decoderBuild, verifierBuild, verifiedAt, capturedEncodingProfile)
    {
    }

    private RomVerification(Guid id, Sha256 artifactSha256, long artifactSize, RomRepresentationEncoding encoding,
        Sha256 contentSha256, long contentSize, Sha1 sha1, Md5 md5, Crc32 crc32,
        string profile, string decoderBuild, string verifierBuild, DateTimeOffset verifiedAt,
        string? capturedEncodingProfile)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Verification requires its retained observation identity.", nameof(id));
        if (artifactSha256.IsEmpty || contentSha256.IsEmpty || sha1.IsEmpty || md5.IsEmpty)
            throw new ArgumentException("Verification requires complete artifact and output identities.");
        ArgumentOutOfRangeException.ThrowIfNegative(artifactSize);
        ArgumentOutOfRangeException.ThrowIfNegative(contentSize);
        if (!Enum.IsDefined(encoding))
            throw new ArgumentOutOfRangeException(nameof(encoding));
        if (encoding == RomRepresentationEncoding.Identity && (artifactSha256 != contentSha256 || artifactSize != contentSize))
            throw new ArgumentException("Identity verification requires identical artifact and output bytes.");
        ValidateLabel(profile, 128);
        ValidateLabel(decoderBuild, 256);
        ValidateLabel(verifierBuild, 256);
        if (capturedEncodingProfile is not null)
        {
            ValidateLabel(capturedEncodingProfile, 512);
            if (encoding == RomRepresentationEncoding.Identity && capturedEncodingProfile != RomEncodingProfiles.Identity)
                throw new ArgumentException("Identity evidence requires the identity encoding profile.", nameof(capturedEncodingProfile));
        }
        Id = id;
        ArtifactSha256 = artifactSha256;
        ArtifactSize = artifactSize;
        Encoding = encoding;
        ContentSha256 = contentSha256;
        ContentSize = contentSize;
        Sha1 = sha1;
        Md5 = md5;
        Crc32 = crc32;
        Profile = profile;
        DecoderBuild = decoderBuild;
        VerifierBuild = verifierBuild;
        VerifiedAt = verifiedAt.ToUniversalTime();
        CapturedEncodingProfile = capturedEncodingProfile;
    }

    public RomLogicalIdentity Identity => new(ContentSize, ContentSha256, Sha1, Md5, Crc32);
    public Guid Id { get; }
    public const string Claim = "measured-output/v1";
    public Sha256 ArtifactSha256 { get; }
    public long ArtifactSize { get; }
    public RomRepresentationEncoding Encoding { get; }
    public Sha256 ContentSha256 { get; }
    public long ContentSize { get; }
    public Sha1 Sha1 { get; }
    public Md5 Md5 { get; }
    public Crc32 Crc32 { get; }
    public string Profile { get; }
    public string DecoderBuild { get; }
    public string VerifierBuild { get; }
    public DateTimeOffset VerifiedAt { get; }
    /// <summary>Parameters observed during this measurement; null preserves unknown historical facts.</summary>
    public string? CapturedEncodingProfile { get; }

    /// <summary>Restore the exact retained identity and UTC instant; never replace a missing measurement ID or precision.</summary>
    public static RomVerification Rehydrate(Guid id, Sha256 artifactSha256, long artifactSize, RomRepresentationEncoding encoding,
        Sha256 contentSha256, long contentSize, Sha1 sha1, Md5 md5, Crc32 crc32,
        string profile, string decoderBuild, string verifierBuild, DateTimeOffset verifiedAt,
        string? capturedEncodingProfile = null) =>
        new(id, artifactSha256, artifactSize, encoding, contentSha256, contentSize, sha1, md5, crc32,
            profile, decoderBuild, verifierBuild, verifiedAt, capturedEncodingProfile);

    private static void ValidateLabel(string value, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > limit)
            throw new ArgumentException("Verification identity is too long.");
    }
}
