using System.Buffers.Binary;

namespace Romd.Media.TestSupport;

/// <summary>Generated Saturn IP header and raw MODE1 frames. No game, firmware or private media bytes.</summary>
public static class SyntheticSaturnCd
{
    public static byte[] Image()
    {
        byte[] image = new byte[64 * 2048];
        image.AsSpan(0, 0xe0).Fill((byte)' ');
        "SEGA SEGASATURN "u8.CopyTo(image);
        "SEGA ENTERPRISES "u8.CopyTo(image.AsSpan(0x10));
        "T-00001"u8.CopyTo(image.AsSpan(0x20));
        "V1.000"u8.CopyTo(image.AsSpan(0x2a));
        "19950101CD-1/2  "u8.CopyTo(image.AsSpan(0x30));
        "JUE"u8.CopyTo(image.AsSpan(0x40));
        "GENERATED SATURN DISC"u8.CopyTo(image.AsSpan(0x60));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0xe0), 4096);
        return image;
    }
    public static byte[] Raw(int pregap = 0) => SyntheticPs1Cd.ToRaw(Image(), pregap: pregap);
}
