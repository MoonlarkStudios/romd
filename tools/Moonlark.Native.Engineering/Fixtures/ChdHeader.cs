using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Fixtures;

internal sealed record ChdMetadata(string Tag, int Flags, int Length, string Sha1, string ValueHex)
{
    internal JsonObject ToJson() => new()
    {
        ["tag"] = Tag,
        ["flags"] = Flags,
        ["length"] = Length,
        ["sha1"] = Sha1,
        ["valueHex"] = ValueHex,
    };
}

/// <summary>A CHD v5 header and metadata chain parsed directly from fixture bytes, independent of either native reader.</summary>
internal sealed record ChdHeader(ulong LogicalBytes, uint HunkBytes, uint UnitBytes, string RawSha1, string OverallSha1, string ParentSha1,
    ImmutableArray<ChdMetadata> Metadata)
{
    private const int HeaderBytes = 124;
    private const int MetadataHeaderBytes = 16;
    private const int MaximumMetadataEntries = 128;
    private const int ChecksumFlag = 1;

    internal static Result<ChdHeader> Read(string path) => Parse(File.ReadAllBytes(path));

    internal static Result<ChdHeader> Parse(ReadOnlySpan<byte> data)
    {
        if (Check.That(data.Length >= HeaderBytes && data[..8].SequenceEqual("MComprHD"u8)
            && BinaryPrimitives.ReadUInt32BigEndian(data[8..]) == HeaderBytes && BinaryPrimitives.ReadUInt32BigEndian(data[12..]) == 5,
            "Expected CHD v5 fixture") is { } version)
            return version;
        ImmutableArray<ChdMetadata>.Builder entries = ImmutableArray.CreateBuilder<ChdMetadata>();
        var visited = new HashSet<ulong>();
        ulong length = (ulong)data.Length;
        for (ulong offset = BinaryPrimitives.ReadUInt64BigEndian(data[48..]); offset != 0;)
        {
            if (Check.That(!visited.Contains(offset) && visited.Count < MaximumMetadataEntries, "Cyclic/excessive metadata") is { } cycle)
                return cycle;
            visited.Add(offset);
            // Bounds are compared by subtraction: Python integers cannot overflow, unsigned offsets can.
            if (Check.That(offset <= length && length - offset >= MetadataHeaderBytes, "Metadata header outside fixture") is { } header)
                return header;
            ReadOnlySpan<byte> entry = data[(int)offset..];
            uint flagsLength = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            int valueLength = (int)(flagsLength & 0xFFFFFF);
            if (Check.That((ulong)valueLength <= length - offset - MetadataHeaderBytes, "Metadata value outside fixture") is { } value)
                return value;
            ReadOnlySpan<byte> tag = entry[..4];
            if (Check.That(Ascii.IsValid(tag), "Metadata tag is not ASCII") is { } ascii)
                return ascii;
            ReadOnlySpan<byte> bytes = entry.Slice(MetadataHeaderBytes, valueLength);
            entries.Add(new ChdMetadata(Encoding.ASCII.GetString(tag), (int)(flagsLength >> 24), valueLength, Sha1(bytes), Convert.ToHexStringLower(bytes)));
            offset = BinaryPrimitives.ReadUInt64BigEndian(entry[8..]);
        }
        return new ChdHeader(BinaryPrimitives.ReadUInt64BigEndian(data[32..]), BinaryPrimitives.ReadUInt32BigEndian(data[56..]),
            BinaryPrimitives.ReadUInt32BigEndian(data[60..]), Convert.ToHexStringLower(data[64..84]), Convert.ToHexStringLower(data[84..104]),
            Convert.ToHexStringLower(data[104..124]), entries.ToImmutable());
    }

    /// <summary>SHA-1 of the raw SHA-1 followed by tag+SHA-1 of each checksummed metadata entry, sorted bytewise.</summary>
    internal static string ComputeOverallSha1(string rawSha1, IEnumerable<ChdMetadata> metadata)
    {
        IEnumerable<byte[]> values = metadata.Where(entry => (entry.Flags & ChecksumFlag) != 0)
            .Select(entry => (byte[])[.. Encoding.ASCII.GetBytes(entry.Tag), .. Convert.FromHexString(entry.Sha1)])
            .Order(Comparer<byte[]>.Create((left, right) => left.AsSpan().SequenceCompareTo(right)));
        return Sha1([.. Convert.FromHexString(rawSha1), .. values.SelectMany(value => value)]);
    }

    internal static string Sha1(ReadOnlySpan<byte> data)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(data);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal JsonObject ToJson() => new()
    {
        ["logicalBytes"] = LogicalBytes,
        ["hunkBytes"] = HunkBytes,
        ["unitBytes"] = UnitBytes,
        ["rawSha1"] = RawSha1,
        ["overallSha1"] = OverallSha1,
        ["parentSha1"] = ParentSha1,
        ["metadata"] = new JsonArray([.. Metadata.Select(entry => (JsonNode?)entry.ToJson())]),
    };
}
