using System.Globalization;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Classification of complete, independently observed container parameters against the canonical DVD geometry.</summary>
/// <remarks>Romd.Media's ChdDvdCanonicalRecipe owns the recipe; these layer-local constants are pinned to it by a cross-layer test.</remarks>
public static class ChdEncodingRecipe
{
    public const uint UnitBytes = 2048;
    public const uint HunkBytes = 16384;
    public const uint Codec0 = RomChdCodecs.Zstd;
    private const string _metadataProfile = "chd-dvd-metadata/v1:1:44564420:1:1:0:c3ee3c942f2cbc35b3a4bec95ec700c2b9d21d5e90c7016b261504710490b406";

    public static RomRepresentationProfileClass Classify(string? profile)
    {
        if (string.IsNullOrEmpty(profile) || profile.Length > 512 || profile.Any(char.IsControl))
            return RomRepresentationProfileClass.Unknown;
        string[] parts = profile.Split(';');
        if (parts.Length != 9 || parts[0] != "chd-observed/v1")
            return RomRepresentationProfileClass.Unknown;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in parts.Skip(1))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1 || part.IndexOf('=', separator + 1) >= 0 ||
                !fields.TryAdd(part[..separator], part[(separator + 1)..]))
                return RomRepresentationProfileClass.Unknown;
        }
        if (!fields.TryGetValue("version", out string? versionText) || !Number(versionText, out uint version) ||
            !fields.TryGetValue("logical", out string? logicalText) ||
            !long.TryParse(logicalText, NumberStyles.None, CultureInfo.InvariantCulture, out long logical) ||
            !fields.TryGetValue("hunk", out string? hunkText) || !Number(hunkText, out uint hunk) ||
            !fields.TryGetValue("unit", out string? unitText) || !Number(unitText, out uint unit) ||
            !fields.TryGetValue("count", out string? countText) || !Number(countText, out uint count) ||
            !fields.TryGetValue("codecs", out string? codecText) ||
            !fields.TryGetValue("parent", out string? parent) || parent != "none" ||
            !fields.TryGetValue("metadata", out string? metadata) || metadata != _metadataProfile)
            return RomRepresentationProfileClass.Unknown;
        string[] codecs = codecText.Split(',');
        if (codecs.Length != 4 || !Codec(codecs[0], out uint codec0) || !Codec(codecs[1], out uint codec1) ||
            !Codec(codecs[2], out uint codec2) || !Codec(codecs[3], out uint codec3))
            return RomRepresentationProfileClass.Unknown;
        try
        {
            // The domain constructor validates every geometry/codec field, not the encoder provenance.
            var parameters = new RomChdContainerParameters(version, logical, hunk, unit, count,
                codec0, codec1, codec2, codec3, false,
                new(1, 0x44564420, 1, 1, 0, RomChdDvdMetadataFacts.ProfileVersion, RomChdDvdMetadataFacts.SingletonFingerprint), profile);
            return parameters.UnitBytes == UnitBytes && parameters.HunkBytes == HunkBytes &&
                parameters.Codec0 == Codec0 && parameters.Codec1 == 0 && parameters.Codec2 == 0 && parameters.Codec3 == 0
                ? RomRepresentationProfileClass.Canonical : RomRepresentationProfileClass.NonCanonical;
        }
        catch (ArgumentException) { return RomRepresentationProfileClass.Unknown; }
    }

    private static bool Number(string value, out uint number) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static bool Codec(string value, out uint codec)
    {
        codec = 0;
        return value.Length == 8 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out codec);
    }
}
