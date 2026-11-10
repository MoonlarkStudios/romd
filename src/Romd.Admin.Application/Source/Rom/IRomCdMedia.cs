using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

public sealed class RomCdStagingLimitException : Exception
{
    public RomCdStagingLimitException() : base("The complete CD attempt exceeds the configured staging file or byte limits.") { }
}

/// <summary>
///     The fixed deployed recipe of the CD track media kind, derived from its registration, and the platforms whose catalog
///     releases a disc of that kind may match; no per-upload codec or processor choices.
/// </summary>
public sealed class RomCdStorageRecipe
{
    public RomCdStorageRecipe(string kind, string id, uint hunkBytes, IReadOnlyList<uint> codecs, IReadOnlyList<string> platformKeys,
        string bootProfile = RomPs1CdBoot.CurrentProfile)
    {
        RecipeId.Parse(id);
        if (string.IsNullOrWhiteSpace(kind) || hunkBytes is < 2448 or > 626688 || hunkBytes % 2448 != 0 || codecs.Count != 4 ||
            codecs.Any(codec => codec != 0 && !IsFourCc(codec)) || platformKeys.Count == 0 || platformKeys.Any(string.IsNullOrWhiteSpace) ||
            bootProfile is not (RomPs1CdBoot.CurrentProfile or RomSaturnCdBoot.CurrentProfile))
            throw new ArgumentException("Bounded CD recipe required.");
        Kind = kind;
        Id = id;
        HunkBytes = hunkBytes;
        Codecs = Array.AsReadOnly(codecs.ToArray());
        PlatformKeys = Array.AsReadOnly(platformKeys.ToArray());
        BootProfile = bootProfile;
        CodecNames = Array.AsReadOnly(Codecs.Where(codec => codec != 0).Select(FourCc).ToArray());
    }
    /// <summary>The media kind this recipe belongs to, which host capabilities answer for.</summary>
    public string Kind { get; }
    public string Id { get; }
    public uint HunkBytes { get; }
    public IReadOnlyList<uint> Codecs { get; }
    /// <summary>The codecs in use, named by their CHD FourCCs in header order.</summary>
    public IReadOnlyList<string> CodecNames { get; }
    public IReadOnlyList<string> PlatformKeys { get; }
    public string BootProfile { get; }
    public bool Matches(RomMeasuredRepresentation representation) => representation.Manifest is RomReconstructionManifest.CdTracks &&
        MatchesParameters(representation.Container);
    public bool Matches(RomRepresentationFacts representation) => representation.Manifest is RomReconstructionManifest.CdTracks &&
        MatchesParameters(representation.Container);
    public bool MatchesParameters(RomContainerFacts container) => container is RomContainerFacts.Chd chd &&
        chd.HunkBytes == HunkBytes && chd.Codecs.SequenceEqual(Codecs);
    private static bool IsFourCc(uint codec) => FourCc(codec).All(char.IsAsciiLetterOrDigit);
    private static string FourCc(uint codec) => string.Create(4, codec, static (name, value) =>
    {
        for (int index = 0; index < name.Length; index++)
            name[index] = (char)(byte)(value >> (24 - 8 * index));
    });
}

/// <summary>Unambiguous immutable routing from a catalog platform or captured media kind to its own CD recipe.</summary>
public sealed class RomCdStorageRecipes
{
    public RomCdStorageRecipes(IEnumerable<RomCdStorageRecipe> recipes, string? defaultKind = null)
    {
        var captured = recipes.ToArray();
        if (captured.Any(recipe => recipe is null) || captured.Select(recipe => recipe.Kind).Distinct(StringComparer.Ordinal).Count() != captured.Length ||
            captured.SelectMany(recipe => recipe.PlatformKeys).Distinct(StringComparer.Ordinal).Count() != captured.Sum(recipe => recipe.PlatformKeys.Count))
            throw new ArgumentException("CD recipes need unique kinds and non-overlapping catalog platform scopes.", nameof(recipes));
        All = Array.AsReadOnly(captured);
        PlatformKeys = Array.AsReadOnly(captured.SelectMany(recipe => recipe.PlatformKeys).ToArray());
        Default = defaultKind is null ? captured.Length == 1 ? captured[0] : null : ForKind(defaultKind);
    }
    public IReadOnlyList<RomCdStorageRecipe> All { get; }
    public IReadOnlyList<string> PlatformKeys { get; }
    /// <summary>Compatibility default for clients that predate the system-specific recipe query.</summary>
    public RomCdStorageRecipe? Default { get; }
    public RomCdStorageRecipe? ForKind(string kind) => All.SingleOrDefault(recipe => recipe.Kind == kind);
    public RomCdStorageRecipe? ForPlatform(string platform) => All.SingleOrDefault(recipe => recipe.PlatformKeys.Contains(platform, StringComparer.Ordinal));
    public IReadOnlyList<string> PlatformsFor(RomCdBoot? boot) => boot is null ? PlatformKeys :
        All.Where(recipe => recipe.BootProfile == boot.Profile).SelectMany(recipe => recipe.PlatformKeys).ToArray();
}

/// <summary>Bounded original descriptor hashing before an immutable source snapshot is captured. No disc or catalog inference.</summary>
public interface IRomCdDescriptorIdentityReader
{
    Task<RomCdFileIdentity?> ReadAsync(int fileId, Sha256 expectedSha256, long expectedSize, CancellationToken ct);
}
