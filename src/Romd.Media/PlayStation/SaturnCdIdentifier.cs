using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Moonlark.Libchdr;
using Romd.Storage;

namespace Romd.Media.PlayStation;

public sealed record SaturnCdObservedFacts(string Profile, long FirstTrackIndex01, string ProductCode,
    string AreaSymbols, uint InitialProgramBytes, string HeaderSha256);

/// <summary>Bounded retail Saturn IP recognition in the first MODE1 raw track. No catalog or emulator inference.</summary>
public static class SaturnCdIdentifier
{
    public const string Profile = "saturn-cd-ip/v1";
    public static SaturnCdObservedFacts? Inspect(ISynchronousRawContent bin, ChdCdTrackType type, long index01, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bin);
        ct.ThrowIfCancellationRequested();
        if (type != ChdCdTrackType.Mode1Raw || index01 is < 0 or > 450000 || bin.Length % 2352 != 0 ||
            bin.Length / 2352 > 450000 || index01 >= bin.Length / 2352)
            return null;
        Span<byte> sector = stackalloc byte[2352];
        int read = 0;
        while (read < sector.Length)
        {
            ct.ThrowIfCancellationRequested();
            int count = bin.ReadAt(index01 * 2352 + read, sector[read..]);
            if (count <= 0 || count > sector.Length - read)
                return null;
            read += count;
        }
        if (sector[0] != 0 || sector[11] != 0 ||
            sector.Slice(1, 10).IndexOfAnyExcept((byte)255) >= 0 || sector[15] != 1)
            return null;
        var header = sector.Slice(16, 256);
        if (!header[..16].SequenceEqual("SEGA SEGASATURN "u8))
            return null;
        uint bytes = BinaryPrimitives.ReadUInt32BigEndian(header[0xe0..]);
        if (header.Slice(0x20, 10).ContainsAnyExceptInRange((byte)32, (byte)126) ||
            header.Slice(0x40, 16).ContainsAnyExceptInRange((byte)32, (byte)126))
            return null;
        var facts = new SaturnCdObservedFacts(Profile, index01, Encoding.ASCII.GetString(header.Slice(0x20, 10)).TrimEnd(' '),
            Encoding.ASCII.GetString(header.Slice(0x40, 16)).TrimEnd(' '), bytes, Convert.ToHexStringLower(SHA256.HashData(header)));
        return Valid(facts) && index01 + (bytes + 2047L) / 2048 <= bin.Length / 2352 ? facts : null;
    }
    public static bool Valid(SaturnCdObservedFacts facts) => facts.Profile == Profile && facts.FirstTrackIndex01 is >= 0 and <= 450000 &&
        facts.ProductCode is { Length: > 0 and <= 10 } && facts.ProductCode.All(c => c is >= '!' and <= '~') &&
        facts.AreaSymbols is { Length: > 0 and <= 16 } && facts.AreaSymbols.Any(c => c != ' ') &&
        facts.AreaSymbols.All(c => "JTUBKAEL ".Contains(c)) && facts.InitialProgramBytes is >= 256 and <= 32768 &&
        ChdCdJson.Hash(facts.HeaderSha256, 64);
}
