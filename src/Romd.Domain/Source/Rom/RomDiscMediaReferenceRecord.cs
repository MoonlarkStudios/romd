using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

public enum RomDiscReferenceSystem { PlayStation1, PlayStation2 }
public enum RomDiscReferenceMedia { Cd, Dvd5, Dvd9 }

/// <summary>Immutable published assertions and retained raw provenance from a trusted local import.</summary>
/// <remarks>Construction validates shape only. The importer, outside the media lease, decides whether
/// the source is trusted and retains its exact raw bytes. No filename, DAT label or checksum grants trust.</remarks>
public sealed record RomDiscMediaReferenceRecord
{
    public const int MaximumFileCount = 128;
    public const long MaximumRawReferenceBytes = 1 << 20;

    public RomDiscMediaReferenceRecord(Guid id, RomDiscReferenceSystem system, RomDiscReferenceMedia media,
        int fileCount, long logicalBytes, Sha1 sha1, Md5 md5, Crc32 crc32, long rawReferenceBytes,
        Sha256 rawReferenceSha256, string referenceSource, string parserProfile,
        string acceptedSourceProvenance, DateTimeOffset retrievedAt)
    {
        if (id == Guid.Empty || !Enum.IsDefined(system) || !Enum.IsDefined(media) ||
            fileCount is < 1 or > MaximumFileCount || rawReferenceBytes is <= 0 or > MaximumRawReferenceBytes ||
            rawReferenceSha256.IsEmpty)
            throw new ArgumentException("A disc reference requires bounded explicit media, record identity and raw provenance.");
        _ = new RomDiscMediaReferenceQuery(logicalBytes, sha1, md5, crc32);
        ValidateLabel(referenceSource, 256);
        ValidateLabel(parserProfile, 128);
        ValidateLabel(acceptedSourceProvenance, 256);
        Id = id;
        System = system;
        Media = media;
        FileCount = fileCount;
        LogicalBytes = logicalBytes;
        Sha1 = sha1;
        Md5 = md5;
        Crc32 = crc32;
        RawReferenceBytes = rawReferenceBytes;
        RawReferenceSha256 = rawReferenceSha256;
        ReferenceSource = referenceSource;
        ParserProfile = parserProfile;
        AcceptedSourceProvenance = acceptedSourceProvenance;
        RetrievedAt = retrievedAt.ToUniversalTime();
    }

    public Guid Id { get; }
    public RomDiscReferenceSystem System { get; }
    public RomDiscReferenceMedia Media { get; }
    /// <summary>Multiplicity of the published original. Multiple files cannot enter the singleton DVD route.</summary>
    public int FileCount { get; }
    public long LogicalBytes { get; }
    public Sha1 Sha1 { get; }
    public Md5 Md5 { get; }
    public Crc32 Crc32 { get; }
    public long RawReferenceBytes { get; }
    public Sha256 RawReferenceSha256 { get; }
    public string ReferenceSource { get; }
    public string ParserProfile { get; }
    public string AcceptedSourceProvenance { get; }
    public DateTimeOffset RetrievedAt { get; }

    public bool Matches(RomDiscMediaReferenceQuery query) => LogicalBytes == query.LogicalBytes &&
        Sha1 == query.Sha1 && Md5 == query.Md5 && Crc32 == query.Crc32;

    private static void ValidateLabel(string value, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > limit || value.Any(char.IsControl))
            throw new ArgumentException("Invalid bounded disc reference provenance.");
    }
}

/// <summary>The complete published quartet. SHA256 is measured separately and is never a published lookup field.</summary>
public sealed record RomDiscMediaReferenceQuery
{
    public RomDiscMediaReferenceQuery(long logicalBytes, Sha1 sha1, Md5 md5, Crc32 crc32)
    {
        if (logicalBytes is <= 0 or > RomPs2BootLayoutFacts.MaximumLogicalBytes ||
            sha1.IsEmpty || md5.IsEmpty || crc32.IsEmpty)
            throw new ArgumentException("A disc reference query requires the complete bounded published identity.");
        LogicalBytes = logicalBytes;
        Sha1 = sha1;
        Md5 = md5;
        Crc32 = crc32;
    }

    public long LogicalBytes { get; }
    public Sha1 Sha1 { get; }
    public Md5 Md5 { get; }
    public Crc32 Crc32 { get; }
}

