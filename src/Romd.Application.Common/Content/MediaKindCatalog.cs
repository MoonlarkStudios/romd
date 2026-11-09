using System.Collections.Frozen;
using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>How one stored artifact of a kind rebuilds its originals.</summary>
public enum MediaManifestType
{
    /// <summary>One artifact rebuilds one file.</summary>
    WholeFile,

    /// <summary>One artifact rebuilds the tracks of a CUE sheet, whose members keep their own names.</summary>
    CdTracks
}

/// <summary>When content of a kind converges to its current recipe.</summary>
public enum ConversionMode
{
    /// <summary>
    ///     Convergence is staged automatically for content that is not canonical, when it is published or when later facts
    ///     resolve its kind.
    /// </summary>
    Automatic,

    /// <summary>
    ///     Convergence is never staged automatically: only an explicit request starts it, and an accepted intent carries
    ///     over to the source that supersedes its own.
    /// </summary>
    OnRequest
}

/// <summary>Pure policy for one kind of media: what it stores, which recipe is current and how delivery names it.</summary>
/// <param name="Kind">A stable lowercase id, such as <c>gamecube-disc</c>.</param>
/// <param name="PlatformKeys">
///     The platforms whose entries this kind may claim. A whole-file kind has exactly one, because its stored delivery
///     and bulk reconciliation scope are per platform.
/// </param>
/// <param name="CurrentRecipeId">The canonical recipe; null only for the opaque fallback, which stores originals.</param>
/// <param name="ConversionMode">
///     Whether reconciliation is staged automatically or only on an explicit request. The opaque fallback never converts.
/// </param>
/// <param name="NativeRuntimes">The runtimes that read the stored format directly.</param>
/// <param name="OriginalExtension">
///     The extension of a whole-file original rebuilt from its stored artifact. Delivery names originals by encoding, because
///     content whose kind does not resolve uniquely still delivers its original, so whole-file kinds of one encoding share it.
///     Members of other layouts keep their names.
/// </param>
/// <remarks>
/// The composition supplies a pure matcher over already loaded facts. It captures only immutable adapter configuration,
/// never services, I/O or request state. The catalog retains that delegate when freezing descriptors; resolver equality
/// compares descriptors from that same catalog, not independently constructed delegates across compositions.
/// </remarks>
/// <param name="ProfileVersions">The encoding profile versions this kind's artifacts report; delivery reports any other as unknown.</param>
public sealed record MediaKindDescriptor(string Kind, IReadOnlyList<string> PlatformKeys, RomRepresentationEncoding StoredEncoding,
    string? CurrentRecipeId, MediaManifestType ManifestType, ConversionMode ConversionMode, IReadOnlyList<string> NativeRuntimes,
    string? OriginalExtension, IReadOnlyList<string> ProfileVersions, Func<RomContentDescriptor, bool>? MatchesFacts = null,
    Func<RomRepresentationFacts, bool>? MatchesLayout = null, string? CdBootProfile = null);

/// <summary>
///     Every registered media kind plus the opaque fallback, which claims everything else and stores originals.
///     Registration is rejected, naming the rule, when it is incomplete or when a platform, recipe or profile would be ambiguous.
/// </summary>
public sealed class MediaKindCatalog
{
    private readonly FrozenDictionary<string, MediaKindDescriptor> _byKind;

    public MediaKindCatalog(IEnumerable<MediaKindDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var declared = descriptors.ToArray();
        if (Problem(declared) is { } problem)
            throw new ArgumentException(problem, nameof(descriptors));
        Fallback = Copy(declared.Single(IsFallback));
        Kinds = Array.AsReadOnly(declared.Where(descriptor => !IsFallback(descriptor)).Select(Copy).ToArray());
        _byKind = Kinds.Append(Fallback).ToFrozenDictionary(descriptor => descriptor.Kind, StringComparer.Ordinal);
    }

    /// <summary>Encoded kinds in registration order.</summary>
    public IReadOnlyList<MediaKindDescriptor> Kinds { get; }

    /// <summary>The kind for content no registration claims: original bytes, delivered only as <c>original</c>.</summary>
    public MediaKindDescriptor Fallback { get; }

    public MediaKindDescriptor? Find(string kind) => _byKind.GetValueOrDefault(kind);

    private static bool IsFallback(MediaKindDescriptor descriptor) => descriptor.StoredEncoding == RomRepresentationEncoding.Identity;

    private static string? Problem(IReadOnlyList<MediaKindDescriptor> declared)
    {
        if (declared.Any(descriptor => descriptor is null))
            return "A media kind registration is null.";
        if (declared.Select(KindProblem).OfType<string>().FirstOrDefault() is { } invalid)
            return invalid;
        if (declared.Count(IsFallback) != 1)
            return "Media kinds need exactly one opaque fallback.";
        if (declared.GroupBy(descriptor => descriptor.Kind, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } kind)
            return $"Media kind '{kind.Key}' is registered more than once.";
        var encoded = declared.Where(descriptor => !IsFallback(descriptor)).ToArray();
        if (encoded.SelectMany(descriptor => descriptor.PlatformKeys.Select(key => (Key: key, descriptor.ManifestType)))
                .GroupBy(claim => claim).FirstOrDefault(group => group.Count() > 1) is { } platform)
            return $"Platform '{platform.Key.Key}' is claimed by two {platform.Key.ManifestType} kinds.";
        if (encoded.GroupBy(descriptor => descriptor.CurrentRecipeId!, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Select(descriptor => (descriptor.StoredEncoding, descriptor.ManifestType)).Distinct().Count() > 1)
            is { } recipe)
            return $"Recipe '{recipe.Key}' is registered for kinds of different encodings or layouts.";
        if (declared.SelectMany(descriptor => descriptor.ProfileVersions.Select(profile => (Profile: profile, descriptor.StoredEncoding)))
                .GroupBy(claim => claim.Profile, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Select(claim => claim.StoredEncoding).Distinct().Count() > 1) is { } profile)
            return $"Profile version '{profile.Key}' is claimed by kinds of different encodings.";
        if (encoded.Where(descriptor => descriptor.ManifestType == MediaManifestType.WholeFile).GroupBy(descriptor => descriptor.StoredEncoding)
                .FirstOrDefault(group => group.Select(descriptor => descriptor.OriginalExtension).Distinct(StringComparer.Ordinal).Count() > 1)
            is { } extension)
            return $"Whole-file {extension.Key} kinds name their originals differently.";
        return null;
    }

    private static string? KindProblem(MediaKindDescriptor descriptor)
    {
        string name = $"Media kind '{descriptor.Kind}'";
        if (!IsKindId(descriptor.Kind))
            return $"{name} needs a lowercase id of letters, digits and hyphens.";
        if (!Enum.IsDefined(descriptor.StoredEncoding) || !Enum.IsDefined(descriptor.ManifestType) ||
            !Enum.IsDefined(descriptor.ConversionMode))
            return $"{name} has an undefined encoding, layout or conversion mode.";
        if (descriptor.PlatformKeys is null || descriptor.NativeRuntimes is null || descriptor.ProfileVersions is null ||
            !descriptor.PlatformKeys.All(IsKey) || !descriptor.NativeRuntimes.All(IsKey) ||
            !descriptor.ProfileVersions.All(profile => IsKey(profile) && !profile.Contains(';')) ||
            Duplicated(descriptor.PlatformKeys) || Duplicated(descriptor.NativeRuntimes) || Duplicated(descriptor.ProfileVersions))
            return $"{name} needs distinct, non-blank platform keys, runtimes and profile versions.";
        if (IsFallback(descriptor))
            return descriptor.MatchesFacts is null && descriptor.CurrentRecipeId is null && descriptor.PlatformKeys.Count == 0 && descriptor.NativeRuntimes.Count == 0 &&
                   descriptor.OriginalExtension is null && descriptor.ManifestType == MediaManifestType.WholeFile &&
                   descriptor.ConversionMode == ConversionMode.OnRequest
                ? null
                : $"{name} is the opaque fallback, so it stores whole originals with no recipe, platform, runtime or original " +
                  "extension, and never converts automatically.";
        if (descriptor.MatchesFacts is null)
            return $"{name} needs its registered physical fact matcher.";
        if (!descriptor.StoredEncoding.IsEncoded() || !RecipeId.TryParse(descriptor.CurrentRecipeId, out _))
            return $"{name} needs an encoded stored format and its current recipe.";
        if (descriptor.PlatformKeys.Count == 0 || descriptor.ProfileVersions.Count == 0)
            return $"{name} needs at least one platform and one profile version.";
        return descriptor.ManifestType == MediaManifestType.WholeFile
            ? descriptor.PlatformKeys.Count == 1 && IsExtension(descriptor.OriginalExtension) ? null
                : $"{name} is whole-file, so it needs exactly one platform and its original extension."
            : descriptor.OriginalExtension is not null
                ? $"{name} rebuilds members that keep their names, so it has no original extension."
                : null;
    }

    private static bool Duplicated(IReadOnlyList<string> values) => values.Distinct(StringComparer.Ordinal).Count() != values.Count;

    private static bool IsKindId(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    private static bool IsKey(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && !value.Any(char.IsControl) &&
        value.Trim() == value;

    private static bool IsExtension(string? value) => value is { Length: > 1 and <= 16 } && value[0] == '.' &&
        value[1..].All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character));

    private static MediaKindDescriptor Copy(MediaKindDescriptor descriptor) => descriptor with
    {
        PlatformKeys = Array.AsReadOnly(descriptor.PlatformKeys.ToArray()),
        NativeRuntimes = Array.AsReadOnly(descriptor.NativeRuntimes.ToArray()),
        ProfileVersions = Array.AsReadOnly(descriptor.ProfileVersions.ToArray())
    };
}
