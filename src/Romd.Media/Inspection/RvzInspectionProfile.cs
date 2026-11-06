using System.Globalization;
using Romd.Media.Rvz;

namespace Romd.Media.Inspection;

/// <summary>Actual readable container settings; producer identity remains unknown.</summary>
public static class RvzInspectionProfile
{
    public static string Capture(RvzDescriptor descriptor) => FormattableString.Invariant(
        $"rvz-import/v1;codec={descriptor.Compression};level={descriptor.CompressionLevel};block={descriptor.ChunkSize};format={descriptor.Version:x8};encoder=unknown");

    public static bool IsValid(string profile)
    {
        if (profile.Length > 128)
            return false;
        string[] fields = profile.Split(';');
        if (fields.Length != 6 || fields[0] != "rvz-import/v1" || !fields[1].StartsWith("codec=", StringComparison.Ordinal) ||
            !fields[2].StartsWith("level=", StringComparison.Ordinal) || !fields[3].StartsWith("block=", StringComparison.Ordinal) ||
            !fields[4].StartsWith("format=", StringComparison.Ordinal) || fields[5] != "encoder=unknown" ||
            !uint.TryParse(fields[1].AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out uint codec) || codec is not (0 or 2 or 3 or 4 or 5) ||
            !int.TryParse(fields[2].AsSpan(6), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int level) ||
            !int.TryParse(fields[3].AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int block) || block is < 32768 or > (8 << 20) ||
            (block < 2097152 ? (block & (block - 1)) != 0 : block % 2097152 != 0) || fields[4].Length != 15 ||
            !uint.TryParse(fields[4].AsSpan(7), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint version) || version < 0x00030000)
            return false;
        return profile == Capture(new(version, 0, codec, level, block, false));
    }
}
