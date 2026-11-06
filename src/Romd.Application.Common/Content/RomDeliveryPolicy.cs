using System.Collections.Frozen;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>A platform's stored delivery format and the runtimes that read it natively.</summary>
public sealed record RomStoredDelivery(string PlatformKey, RomDeliveryFormat Format, IReadOnlyList<string> NativeRuntimes);

/// <summary>
///     One delivery format's facts: the label clients display for it and, only for an encoded format, the extension that
///     replaces the original member's.
/// </summary>
public sealed record RomDeliveryFormatEntry(RomDeliveryFormat Format, string Label, string? FileExtension);

/// <summary>
///     The one registration of delivery format labels and file extensions, so surfaces and clients read them instead of
///     naming them.
/// </summary>
public sealed class RomDeliveryFormatCatalog
{
    public const int MaximumLabelLength = 32;

    private readonly FrozenDictionary<RomDeliveryFormat, RomDeliveryFormatEntry> _byFormat;

    public RomDeliveryFormatCatalog(IEnumerable<RomDeliveryFormatEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var declared = entries.ToArray();
        string[] extensions = declared.Select(entry => entry?.FileExtension).OfType<string>().ToArray();
        if (declared.Any(entry => entry is null || !Enum.IsDefined(entry.Format) || !IsLabel(entry.Label) ||
                (entry.Format == RomDeliveryFormat.Original) != (entry.FileExtension is null) ||
                entry.FileExtension is { } extension && !IsExtension(extension)) ||
            declared.DistinctBy(entry => entry.Format).Count() != declared.Length ||
            declared.Length != Enum.GetValues<RomDeliveryFormat>().Length ||
            declared.DistinctBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase).Count() != declared.Length ||
            extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != extensions.Length)
            throw new ArgumentException(
                "Every delivery format needs one entry with a unique label, and each encoded format a unique extension.",
                nameof(entries));
        _byFormat = declared.ToFrozenDictionary(entry => entry.Format);
    }

    /// <summary>The registered label and extension of a delivery format.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The format is undefined.</exception>
    public RomDeliveryFormatEntry Entry(RomDeliveryFormat format) =>
        _byFormat.GetValueOrDefault(format) ??
        throw new ArgumentOutOfRangeException(nameof(format), format, "The delivery format is not registered.");

    /// <summary>The extension that replaces an original member's extension in this encoded format's filename.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The format is original or undefined.</exception>
    public string FileExtension(RomDeliveryFormat format) =>
        _byFormat.GetValueOrDefault(format)?.FileExtension ??
        throw new ArgumentOutOfRangeException(nameof(format), format, "Only an encoded delivery format has a file extension.");

    private static bool IsLabel(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumLabelLength &&
        value.Trim() == value && !value.Any(char.IsControl);

    private static bool IsExtension(string value) => value.Length is > 1 and <= 16 && value[0] == '.' &&
        value[1..].All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character));
}

/// <summary>
///     The delivery view of the media kinds: the format catalog, each whole-file kind's stored delivery for its platform,
///     the original extension of each encoding and the profile versions delivery reports. Kinds whose artifacts rebuild
///     several files keep their members' names and expose no stored format per platform.
/// </summary>
public sealed class RomDeliveryPolicy
{
    private readonly FrozenDictionary<string, RomStoredDelivery> _byPlatform;
    private readonly FrozenDictionary<RomRepresentationEncoding, string> _originalExtensions;
    private readonly FrozenSet<string> _profileVersions;
    private readonly FrozenDictionary<string, Func<RomContainerFacts.Chd, bool>> _canonicalChdParameters;

    public RomDeliveryPolicy(RomDeliveryFormatCatalog formats, MediaKindCatalog kinds,
        IReadOnlyDictionary<string, Func<RomContainerFacts.Chd, bool>>? canonicalChdParameters = null)
    {
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(kinds);
        var wholeFile = kinds.Kinds.Where(kind => kind.ManifestType == MediaManifestType.WholeFile).ToArray();
        _originalExtensions = wholeFile.DistinctBy(kind => kind.StoredEncoding)
            .ToFrozenDictionary(kind => kind.StoredEncoding, kind => kind.OriginalExtension!);
        // A stored-format name that equals an original's would make a renamed original indistinguishable from it.
        if (_originalExtensions.Values.Any(extension => wholeFile.Any(kind =>
                formats.FileExtension(StoredFormat(kind.StoredEncoding)).Equals(extension, StringComparison.Ordinal))))
            throw new ArgumentException("A stored format's extension cannot be an original's extension.", nameof(formats));
        if (canonicalChdParameters is not null && canonicalChdParameters.Any(pair => pair.Value is null ||
                !wholeFile.Any(kind => kind.Kind == pair.Key && kind.StoredEncoding == RomRepresentationEncoding.Chd)))
            throw new ArgumentException("Canonical CHD parameter matchers must name registered whole-file CHD kinds.", nameof(canonicalChdParameters));
        _canonicalChdParameters = canonicalChdParameters?.ToFrozenDictionary(StringComparer.Ordinal)
            ?? FrozenDictionary<string, Func<RomContainerFacts.Chd, bool>>.Empty;
        Formats = formats;
        Kinds = kinds;
        _byPlatform = wholeFile.ToFrozenDictionary(kind => kind.PlatformKeys.Single(),
            kind => new RomStoredDelivery(kind.PlatformKeys.Single(), StoredFormat(kind.StoredEncoding), kind.NativeRuntimes),
            StringComparer.Ordinal);
        _profileVersions = kinds.Kinds.Append(kinds.Fallback).SelectMany(kind => kind.ProfileVersions).ToFrozenSet(StringComparer.Ordinal);
    }

    public RomDeliveryFormatCatalog Formats { get; }

    /// <summary>The media kinds delivery resolves each ROM's kind against.</summary>
    public MediaKindCatalog Kinds { get; }

    /// <summary>
    ///     Whether the kind's current registered recipe accepts the complete observed CHD parameters and captured profile.
    ///     An absent matcher makes no canonical claim; the composition supplies each whole-file CHD kind's adapter rule.
    /// </summary>
    public bool IsCanonicalChd(string kindId, RomContainerFacts.Chd parameters) =>
        _canonicalChdParameters.GetValueOrDefault(kindId)?.Invoke(parameters) == true;

    public IEnumerable<string> PlatformKeys => _byPlatform.Keys;

    public RomStoredDelivery? For(string? platformKey) =>
        platformKey is null ? null : _byPlatform.GetValueOrDefault(platformKey);

    /// <summary>The extension of a whole-file original rebuilt from an artifact in this encoding; null when none is registered.</summary>
    public string? OriginalExtension(RomRepresentationEncoding encoding) => _originalExtensions.GetValueOrDefault(encoding);

    /// <summary>The registered profile version an encoding profile reports, or <c>unknown</c>.</summary>
    public string ProfileVersion(string? profile) =>
        profile?.Split(';')[0] is { } version && _profileVersions.Contains(version) ? version : "unknown";

    /// <summary>The delivery format that serves an artifact in this stored encoding as it is stored.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No delivery format stores the encoding.</exception>
    public static RomDeliveryFormat StoredFormat(RomRepresentationEncoding encoding) => encoding switch
    {
        RomRepresentationEncoding.Rvz => RomDeliveryFormat.Rvz,
        RomRepresentationEncoding.Chd => RomDeliveryFormat.Chd,
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "No delivery format stores this encoding.")
    };
}

/// <summary>
///     System ids of the platforms a media kind claims, keyed as kind resolution filters platforms. Every other platform
///     resolves to no key, which no kind claims, so it resolves exactly as its own key would.
/// </summary>
public sealed class RomDeliverySystems
{
    private readonly IReadOnlyDictionary<int, string> _keys;

    private RomDeliverySystems(IReadOnlyDictionary<int, string> keys) => _keys = keys;

    public string? KeyFor(int? platformId) => platformId is { } id ? _keys.GetValueOrDefault(id) : null;

    public static async Task<RomDeliverySystems> ResolveAsync(RomDeliveryPolicy policy,
        IReferenceCatalogService referenceCatalog, CancellationToken ct)
    {
        var keys = new Dictionary<int, string>();
        foreach (string key in policy.Kinds.Kinds.SelectMany(kind => kind.PlatformKeys).Distinct(StringComparer.Ordinal))
            if (await referenceCatalog.ResolveSystemIdAsync(key, ct) is int id and > 0)
                keys.Add(id, key);
        return new(keys);
    }
}
