using System.Buffers.Binary;
using System.Security.Cryptography;
using Romd.Domain.Hashing;

namespace Romd.Domain.Source.Rom;

/// <summary>Captured container values. Recipe matching and decoder admission are separate policy.</summary>
public abstract class RomContainerFacts
{
    private RomContainerFacts() { }
    public abstract RomRepresentationEncoding Encoding { get; }
    public abstract Sha256? Fingerprint { get; }

    public sealed class Identity : RomContainerFacts
    {
        public static Identity Instance { get; } = new();
        private Identity() { }
        public override RomRepresentationEncoding Encoding => RomRepresentationEncoding.Identity;
        public override Sha256? Fingerprint => null;
    }

    public sealed class Rvz : RomContainerFacts
    {
        public Rvz(uint version, uint discType, uint compression, int compressionLevel, int blockBytes,
            bool hasWiiPartitions, long logicalBytes)
        {
            if (version < 0x00030000 || compression is not (0 or 2 or 3 or 4 or 5) ||
                blockBytes is < 32768 or > 8388608 ||
                (blockBytes < 2097152 ? (blockBytes & (blockBytes - 1)) != 0 : blockBytes % 2097152 != 0) ||
                logicalBytes is < 1 or > MaximumLogicalBytes)
                throw new ArgumentException("RVZ facts require bounded captured format parameters.");
            Version = version;
            DiscType = discType;
            Compression = compression;
            CompressionLevel = compressionLevel;
            BlockBytes = blockBytes;
            HasWiiPartitions = hasWiiPartitions;
            LogicalBytes = logicalBytes;
            Fingerprint = FingerprintRvz(this);
        }
        public override RomRepresentationEncoding Encoding => RomRepresentationEncoding.Rvz;
        public override Sha256? Fingerprint { get; }
        public uint Version { get; }
        public uint DiscType { get; }
        public uint Compression { get; }
        public int CompressionLevel { get; }
        public int BlockBytes { get; }
        public bool HasWiiPartitions { get; }
        public long LogicalBytes { get; }
    }

    public sealed class Chd : RomContainerFacts
    {
        public Chd(uint version, long logicalBytes, uint hunkBytes, uint unitBytes, uint hunkCount,
            IReadOnlyList<uint> codecs, bool hasParent, Sha1 storedRawSha1, Sha1 storedOverallSha1,
            IReadOnlyList<RomChdMetadataEntry> metadata)
        {
            ArgumentNullException.ThrowIfNull(codecs);
            ArgumentNullException.ThrowIfNull(metadata);
            var capturedCodecs = codecs.ToArray();
            var capturedMetadata = metadata.ToArray();
            if (version != 5 || logicalBytes is < 1 or > MaximumLogicalBytes || hasParent ||
                hunkBytes is < 1 or > 1048576 || unitBytes is < 1 or > 2448 ||
                logicalBytes % unitBytes != 0 || hunkBytes % unitBytes != 0 || hunkCount == 0 ||
                hunkCount != (ulong)logicalBytes / hunkBytes + ((ulong)logicalBytes % hunkBytes == 0 ? 0UL : 1UL) ||
                capturedCodecs.Length != 4 || storedRawSha1.IsEmpty || storedOverallSha1.IsEmpty ||
                capturedMetadata.Length > MaximumMetadataEntries || capturedMetadata.Any(entry => entry is null) ||
                capturedMetadata.Sum(entry => (long)entry.Payload.Length) > 524288)
                throw new ArgumentException("CHD facts require bounded v5 geometry, exact header hashes and ordered metadata.");
            Version = version;
            LogicalBytes = logicalBytes;
            HunkBytes = hunkBytes;
            UnitBytes = unitBytes;
            HunkCount = hunkCount;
            Codecs = Array.AsReadOnly(capturedCodecs);
            StoredRawSha1 = storedRawSha1;
            StoredOverallSha1 = storedOverallSha1;
            Metadata = Array.AsReadOnly(capturedMetadata);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> header = stackalloc byte[9];
            foreach (var entry in capturedMetadata)
            {
                BinaryPrimitives.WriteUInt32BigEndian(header, entry.Tag);
                header[4] = entry.Flags;
                BinaryPrimitives.WriteUInt32BigEndian(header[5..], (uint)entry.Payload.Length);
                hash.AppendData(header);
                hash.AppendData(entry.Payload);
            }
            MetadataSha256 = Sha256.FromSpan(hash.GetHashAndReset());
            Fingerprint = FingerprintChd(this);
        }
        public override RomRepresentationEncoding Encoding => RomRepresentationEncoding.Chd;
        public override Sha256? Fingerprint { get; }
        public uint Version { get; }
        public long LogicalBytes { get; }
        public uint HunkBytes { get; }
        public uint UnitBytes { get; }
        public uint HunkCount { get; }
        public IReadOnlyList<uint> Codecs { get; }
        public bool HasParent => false;
        public Sha1 StoredRawSha1 { get; }
        public Sha1 StoredOverallSha1 { get; }
        public IReadOnlyList<RomChdMetadataEntry> Metadata { get; }
        public Sha256 MetadataSha256 { get; }
    }

    private static Sha256 FingerprintRvz(Rvz facts)
    {
        Span<byte> fields = stackalloc byte[29];
        BinaryPrimitives.WriteUInt32BigEndian(fields, facts.Version);
        BinaryPrimitives.WriteUInt32BigEndian(fields[4..], facts.DiscType);
        BinaryPrimitives.WriteUInt32BigEndian(fields[8..], facts.Compression);
        BinaryPrimitives.WriteInt32BigEndian(fields[12..], facts.CompressionLevel);
        BinaryPrimitives.WriteInt32BigEndian(fields[16..], facts.BlockBytes);
        fields[20] = facts.HasWiiPartitions ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(fields[21..], facts.LogicalBytes);
        return FingerprintFields("rvz-parameters/v1\0"u8, fields);
    }

    private static Sha256 FingerprintChd(Chd facts)
    {
        Span<byte> fields = stackalloc byte[113];
        BinaryPrimitives.WriteUInt32BigEndian(fields, facts.Version);
        BinaryPrimitives.WriteInt64BigEndian(fields[4..], facts.LogicalBytes);
        BinaryPrimitives.WriteUInt32BigEndian(fields[12..], facts.HunkBytes);
        BinaryPrimitives.WriteUInt32BigEndian(fields[16..], facts.UnitBytes);
        BinaryPrimitives.WriteUInt32BigEndian(fields[20..], facts.HunkCount);
        for (int index = 0; index < 4; index++)
            BinaryPrimitives.WriteUInt32BigEndian(fields[(24 + index * 4)..], facts.Codecs[index]);
        fields[40] = facts.HasParent ? (byte)1 : (byte)0;
        facts.StoredRawSha1.AsSpan().CopyTo(fields[41..]);
        facts.StoredOverallSha1.AsSpan().CopyTo(fields[61..]);
        facts.MetadataSha256.AsSpan().CopyTo(fields[81..]);
        return FingerprintFields("chd-parameters/v1\0"u8, fields);
    }

    private static Sha256 FingerprintFields(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix);
        hash.AppendData(fields);
        return Sha256.FromSpan(hash.GetHashAndReset());
    }

    public const int MaximumMetadataEntries = 99;
    public const long MaximumLogicalBytes = 10L << 30;
}

/// <summary>Exact ordered CHD metadata bytes, not a producer or runtime claim.</summary>
public sealed class RomChdMetadataEntry
{
    private readonly byte[] _payload;
    public RomChdMetadataEntry(uint tag, byte flags, ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > 262144)
            throw new ArgumentException("CHD metadata payload is outside bounds.", nameof(payload));
        Tag = tag;
        Flags = flags;
        _payload = payload.ToArray();
    }
    public uint Tag { get; }
    public byte Flags { get; }
    public ReadOnlySpan<byte> Payload => _payload;
}
