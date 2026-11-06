using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace Moonlark.Native.Engineering.Fixtures;

/// <summary>Four v2 recipes with fixed geometry and new deterministic source identities.</summary>
internal static class CdEdgeFixtures
{
    internal static readonly ImmutableArray<string> Stems = ["cd-edge-multi", "cd-edge-single", "cd-edge-partial", "cd-edge-subcode"];
    private const string First = "FILE \"cd-edge-track1.bin\" BINARY\n  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n";
    private const string Second = "FILE \"cd-edge-track2.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 00 00:00:00\n    INDEX 01 00:00:02\n";

    internal static Dictionary<string, byte[]> Sources()
    {
        byte[] first = Track(17, 1, audio: false, subcode: false, mode: 2);
        byte[] second = Track(13, 2, audio: true, subcode: false);
        byte[] third = Track(9, 3, audio: true, subcode: false);
        return new(StringComparer.Ordinal)
        {
            ["cd-edge-track1.bin"] = first,
            ["cd-edge-track2.bin"] = second,
            ["cd-edge-track3.bin"] = third,
            ["cd-edge-single.bin"] = [.. first, .. second, .. third],
            ["cd-edge-sub1.bin"] = Track(8, 4, audio: false, subcode: true),
            ["cd-edge-sub2.bin"] = Track(6, 5, audio: true, subcode: true),
            ["cd-edge-multi.cue"] = Encoding.ASCII.GetBytes(First + Second
                + "FILE \"cd-edge-track3.bin\" BINARY\n  TRACK 03 AUDIO\n    PREGAP 00:00:02\n    INDEX 01 00:00:00\n"),
            ["cd-edge-partial.cue"] = Encoding.ASCII.GetBytes(First + Second),
            ["cd-edge-single.cue"] = Encoding.ASCII.GetBytes("FILE \"cd-edge-single.bin\" BINARY\n"
                + "  TRACK 01 MODE2/2352\n    INDEX 01 00:00:00\n"
                + "  TRACK 02 AUDIO\n    INDEX 00 00:00:17\n    INDEX 01 00:00:19\n"
                + "  TRACK 03 AUDIO\n    PREGAP 00:00:02\n    INDEX 01 00:00:30\n"),
            ["cd-edge-subcode.cue"] = Encoding.ASCII.GetBytes("FILE \"cd-edge-sub1.bin\" BINARY\n"
                + "  TRACK 01 MODE1/2352 RW_RAW\n    INDEX 01 00:00:00\nFILE \"cd-edge-sub2.bin\" BINARY\n"
                + "  TRACK 02 AUDIO RW_RAW\n    INDEX 00 00:00:00\n    INDEX 01 00:00:02\n"),
        };
    }

    internal static byte[] Source(string stem)
    {
        Dictionary<string, byte[]> sources = Sources();
        return stem == "cd-edge-subcode" ? [.. sources["cd-edge-sub1.bin"], .. sources["cd-edge-sub2.bin"]]
            : stem == "cd-edge-partial" ? [.. sources["cd-edge-track1.bin"], .. sources["cd-edge-track2.bin"]]
            : sources["cd-edge-single.bin"];
    }

    /// <summary>CDRDAO output preserves subcode and native big-endian audio; CUE input audio is little-endian.</summary>
    internal static void SwapSubcodeAudio(Span<byte> source)
    {
        for (int frame = 8; frame < 14; frame++)
            for (int index = frame * 2448; index < frame * 2448 + 2352; index += 2)
                (source[index], source[index + 1]) = (source[index + 1], source[index]);
    }

    internal static byte[] Logical(string stem)
    {
        Dictionary<string, byte[]> sources = Sources();
        if (stem == "cd-edge-subcode")
            return Project([(sources["cd-edge-sub1.bin"], false, 2448), (sources["cd-edge-sub2.bin"], true, 2448)]);
        (byte[] Bytes, bool Audio, int Stride)[] tracks = [(sources["cd-edge-track1.bin"], false, 2352), (sources["cd-edge-track2.bin"], true, 2352), (sources["cd-edge-track3.bin"], true, 2352)];
        return Project(stem == "cd-edge-partial" ? tracks[..2] : tracks);
    }

    /// <summary>Each source track starts on a four-frame boundary; only audio sample bytes are swapped.</summary>
    internal static byte[] Project((byte[] Bytes, bool Audio, int Stride)[] tracks)
    {
        byte[] logical = new byte[tracks.Sum(track => (track.Bytes.Length / track.Stride + 3) / 4 * 4) * 2448];
        int start = 0;
        foreach ((byte[] source, bool audio, int stride) in tracks)
        {
            int frames = source.Length / stride;
            for (int frame = 0; frame < frames; frame++)
            {
                Span<byte> target = logical.AsSpan((start + frame) * 2448, 2448);
                source.AsSpan(frame * stride, stride).CopyTo(target);
                if (audio)
                    for (int index = 0; index < 2352; index += 2)
                        (target[index], target[index + 1]) = (target[index + 1], target[index]);
            }
            start += (frames + 3) / 4 * 4;
        }
        return logical;
    }

    internal static string[] Metadata(string stem)
    {
        if (stem == "cd-edge-subcode")
            return
        [
            "TRACK:1 TYPE:MODE1_RAW SUBTYPE:RW_RAW FRAMES:8 PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0",
            "TRACK:2 TYPE:AUDIO SUBTYPE:RW_RAW FRAMES:6 PREGAP:2 PGTYPE:VAUDIO PGSUB:NONE POSTGAP:0",
        ];
        string[] tracks =
        [
            "TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:17 PREGAP:0 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0",
            "TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:13 PREGAP:2 PGTYPE:VAUDIO PGSUB:NONE POSTGAP:0",
            "TRACK:3 TYPE:AUDIO SUBTYPE:NONE FRAMES:9 PREGAP:2 PGTYPE:MODE1 PGSUB:NONE POSTGAP:0",
        ];
        return stem == "cd-edge-partial" ? tracks[..2] : tracks;
    }

    internal static bool MetadataMatches(string stem, ChdHeader header) => header.Metadata.SequenceEqual(Metadata(stem).Select(text =>
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text + '\0');
        return new ChdMetadata("CHT2", 1, bytes.Length, ChdHeader.Sha1(bytes), Convert.ToHexStringLower(bytes));
    }));

    // The v1 arithmetic patterns are extended with an explicit track discriminator; no PRNG or historical byte reconstruction.
    private static byte[] Track(int frames, int track, bool audio, bool subcode, int mode = 1)
    {
        int stride = subcode ? 2448 : 2352;
        byte[] bytes = new byte[frames * stride];
        for (int frame = 0; frame < frames; frame++)
        {
            Span<byte> sector = bytes.AsSpan(frame * stride, stride);
            if (audio)
            {
                for (int sample = 0; sample < 2352 / 4; sample++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(sector[(sample * 4)..], (short)((track * 211 + frame * 701 + sample * 31) % 65536 - 32768));
                    BinaryPrimitives.WriteInt16LittleEndian(sector[(sample * 4 + 2)..], (short)((track * 307 + frame * 1103 + sample * 43) % 65536 - 32768));
                }
            }
            else
            {
                for (int index = 0; index < 2352; index++)
                    sector[index] = (byte)((track * 19 + frame * 17 + index * 13 + (index >> 4)) & 255);
                sector[0] = sector[11] = sector[12] = 0;
                sector[1..11].Fill(255);
                sector[13] = 2;
                sector[14] = (byte)(((frame / 10) << 4) | frame % 10);
                sector[15] = (byte)mode;
            }
            if (subcode)
                for (int index = 0; index < 96; index++)
                    sector[2352 + index] = (byte)((track * 23 + frame * 11 + index * 7) & 255);
        }
        return bytes;
    }
}
