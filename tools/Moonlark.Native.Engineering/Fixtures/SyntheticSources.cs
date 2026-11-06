using System.Buffers.Binary;

namespace Moonlark.Native.Engineering.Fixtures;

/// <summary>Deterministic v1 synthetic sources. They contain no filesystem or game content.</summary>
internal static class SyntheticSources
{
    internal const int DvdSectorBytes = 2048;
    internal const int CdFrameBytes = 2352;
    internal const int SubcodeBytes = 96;

    internal const string CdCue = "FILE \"cd-source.bin\" BINARY\n"
        + "  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n"
        + "  TRACK 02 AUDIO\n    INDEX 00 00:00:16\n    INDEX 01 00:00:20\n";

    internal const string CdSubcodeToc = "CD_ROM\nTRACK MODE1_RAW RW_RAW\nDATAFILE \"cd-subcode-source.bin\" 00:00:00 00:00:16\n";

    /// <summary>512 sectors including repeated zero/pattern hunks.</summary>
    internal static byte[] Dvd()
    {
        byte[] data = new byte[512 * DvdSectorBytes];
        for (int sector = 0; sector < 512; sector++)
        {
            if (sector / 8 % 4 == 0)
                continue;
            Span<byte> bytes = data.AsSpan(sector * DvdSectorBytes, DvdSectorBytes);
            for (int index = 0; index < bytes.Length; index++)
                bytes[index] = (byte)((sector % 8 * 17 + index * 13 + (index >> 4)) & 255);
        }
        return data;
    }

    /// <summary>16 synthetic MODE1/2352 sectors followed by 16 little-endian stereo audio frames.</summary>
    internal static byte[] Cd()
    {
        byte[] data = new byte[32 * CdFrameBytes];
        for (int frame = 0; frame < 16; frame++)
        {
            Span<byte> sector = data.AsSpan(frame * CdFrameBytes, CdFrameBytes);
            for (int index = 0; index < sector.Length; index++)
                sector[index] = (byte)((frame * 17 + index * 13 + (index >> 4)) & 255);
            sector[0] = 0;
            sector[1..11].Fill(0xFF);
            sector[11] = 0;
            ReadOnlySpan<byte> address = [0, 2, (byte)(((frame / 10) << 4) | frame % 10), 1];
            address.CopyTo(sector[12..]);
        }
        for (int frame = 0; frame < 16; frame++)
        {
            Span<byte> audio = data.AsSpan((16 + frame) * CdFrameBytes, CdFrameBytes);
            for (int sample = 0; sample < CdFrameBytes / 4; sample++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(audio[(sample * 4)..], (short)((frame * 701 + sample * 31) % 65536 - 32768));
                BinaryPrimitives.WriteInt16LittleEndian(audio[(sample * 4 + 2)..], (short)((frame * 1103 + sample * 43) % 65536 - 32768));
            }
        }
        return data;
    }

    /// <summary>The 16 data frames, each followed by deterministic nonzero 96-byte raw subcode.</summary>
    internal static byte[] CdSubcode()
    {
        byte[] cd = Cd();
        byte[] data = new byte[16 * (CdFrameBytes + SubcodeBytes)];
        for (int frame = 0; frame < 16; frame++)
        {
            Span<byte> output = data.AsSpan(frame * (CdFrameBytes + SubcodeBytes), CdFrameBytes + SubcodeBytes);
            cd.AsSpan(frame * CdFrameBytes, CdFrameBytes).CopyTo(output);
            for (int index = 0; index < SubcodeBytes; index++)
                output[CdFrameBytes + index] = (byte)((frame * 11 + index * 7) & 255);
        }
        return data;
    }
}
