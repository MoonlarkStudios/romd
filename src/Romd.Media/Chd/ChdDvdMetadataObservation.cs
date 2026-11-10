using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Moonlark.Libchdr;

namespace Romd.Media.Chd;

/// <summary>Observed closed CHD container marker; independent original DVD provenance is still required.</summary>
public sealed record ChdDvdMetadataObservation(string Profile, uint EntryCount,
    uint Tag, byte Flags, uint PayloadBytes, byte PayloadByte, string NormalizedSha256);

internal static class ChdDvdMetadataObserver
{
    internal const string Profile = "chd-dvd-metadata/v1";
    internal const string ExpectedNormalizedSha256 =
        "c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406";

    internal static bool TryObserve(ChdFile file, [NotNullWhen(true)] out ChdDvdMetadataObservation? observation)
    {
        observation = null;
        ChdMetadataEnumerator entries = file.EnumerateMetadata();
        if (!entries.MoveNext())
            return false;
        ChdMetadataInfo descriptor = entries.Current;
        // Extra unchecksummed entries must also reject; overall SHA1 would not expose them.
        if (entries.MoveNext() || descriptor.Tag != ChdMetadataTag.Dvd ||
            descriptor.Flags != 1 || descriptor.Length != 1)
            return false;
        Span<byte> payload = stackalloc byte[1];
        if (!file.TryGetMetadata(ChdMetadataTag.Dvd, 0, payload, out ChdMetadataInfo read) ||
            read != descriptor || payload[0] != 0)
            return false;

        Span<byte> normalized = stackalloc byte[10];
        BinaryPrimitives.WriteUInt32BigEndian(normalized, descriptor.Tag.Value);
        normalized[4] = descriptor.Flags;
        BinaryPrimitives.WriteUInt32BigEndian(normalized[5..], descriptor.Length);
        normalized[9] = payload[0];
        observation = new(Profile, 1, descriptor.Tag.Value, descriptor.Flags,
            descriptor.Length, payload[0], Convert.ToHexStringLower(SHA256.HashData(normalized)));
        return true;
    }
}
