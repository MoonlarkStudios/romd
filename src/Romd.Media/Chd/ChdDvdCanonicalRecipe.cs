using System.Globalization;
using Moonlark.Libchdr;

namespace Romd.Media.Chd;

/// <summary>The fixed, versioned recipe for parent-free PlayStation 2 DVD CHD artifacts.</summary>
/// <remarks>Canonical shape does not qualify a tool, decoder, deployment or recipe for production activation.</remarks>
public static class ChdDvdCanonicalRecipe
{
    public const string Id = "chd-dvd-canonical/v2";
    public const string EncoderProfile = "dvd;unit=2048;hunk=16384;codec=zstd;processors=1";
    public const uint UnitBytes = ChdDvdLimits.UnitBytes;
    public const uint HunkBytes = 16384;
    public const ChdCodec Codec = ChdCodec.Zstd;
    public const int Processors = 1;
    private const string CodecName = "zstd";

    /// <summary>The chdman command line after the tool path; the caller owns any launcher prefix.</summary>
    public static string[] ChdmanArguments(string sourcePath, string outputPath) =>
    [
        "createdvd", "-i", sourcePath, "-o", outputPath,
        "-hs", HunkBytes.ToString(CultureInfo.InvariantCulture), "-c", CodecName,
        "-np", Processors.ToString(CultureInfo.InvariantCulture)
    ];

    /// <summary>Compares observed header geometry and codecs, independent of producer, size bounds or artifact identity.</summary>
    public static bool Matches(ChdHeaderFacts header) =>
        header.UnitBytes == UnitBytes && header.HunkBytes == HunkBytes &&
        header.HunkCount == header.LogicalBytes / HunkBytes + (header.LogicalBytes % HunkBytes == 0 ? 0UL : 1UL) &&
        header.Codec0 == (uint)Codec && header.Codec1 == (uint)ChdCodec.None &&
        header.Codec2 == (uint)ChdCodec.None && header.Codec3 == (uint)ChdCodec.None;
}
