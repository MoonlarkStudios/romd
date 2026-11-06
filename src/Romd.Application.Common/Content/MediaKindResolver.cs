using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>How content's media kind resolved under the platforms it is reached through.</summary>
public enum MediaKindOutcome
{
    /// <summary>Exactly one kind; its descriptor supplies runtime claims and the rules that read a kind.</summary>
    Unique,

    /// <summary>Several kinds remain, or the matched entries disagree or one of them contradicts the evidence.</summary>
    Ambiguous,

    /// <summary>The platform contradicts the evidence: no kind the measured facts allow claims it.</summary>
    Contradicted,

    /// <summary>Nothing claims the content, so it is the opaque fallback's.</summary>
    Fallback
}

/// <summary>
///     A resolved media kind. It decides runtime claims and the rules that read a kind's descriptor, never which bytes are
///     admitted: byte admission rests on retained evidence, the decoder tuple and host decode capability alone.
/// </summary>
public sealed record MediaKindResolution
{
    private MediaKindResolution(MediaKindOutcome outcome, MediaKindDescriptor? kind) => (Outcome, Kind) = (outcome, kind);

    public static MediaKindResolution Ambiguous { get; } = new(MediaKindOutcome.Ambiguous, null);

    public static MediaKindResolution Contradicted { get; } = new(MediaKindOutcome.Contradicted, null);

    public MediaKindOutcome Outcome { get; }

    /// <summary>The unique kind or the opaque fallback; null when the result is ambiguous or contradicted.</summary>
    public MediaKindDescriptor? Kind { get; }

    /// <summary>The uniquely resolved encoded kind; null for every other result, including the fallback.</summary>
    public MediaKindDescriptor? Unique => Outcome == MediaKindOutcome.Unique ? Kind : null;

    public static MediaKindResolution Of(MediaKindDescriptor kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!kind.StoredEncoding.IsEncoded())
            throw new ArgumentException("Only an encoded kind resolves uniquely; the fallback has its own result.", nameof(kind));
        return new(MediaKindOutcome.Unique, kind);
    }

    public static MediaKindResolution FallbackOf(MediaKindDescriptor fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (fallback.StoredEncoding.IsEncoded())
            throw new ArgumentException("The opaque fallback stores originals.", nameof(fallback));
        return new(MediaKindOutcome.Fallback, fallback);
    }

    /// <summary>The runtimes that read a delivery format natively: a unique kind's, for its own stored format only.</summary>
    public IReadOnlyList<string> NativeRuntimes(RomDeliveryFormat format) =>
        Unique is { } kind && RomDeliveryPolicy.StoredFormat(kind.StoredEncoding) == format ? kind.NativeRuntimes : [];
}

/// <summary>
///     Resolves content's media kind from loaded facts, with platform as a filter. It reads only the content's
///     reconstruction and retained artifact evidence, never a profile string or recipe id, and persists nothing.
/// </summary>
public static class MediaKindResolver
{
    /// <summary>
    ///     Resolves under the platforms content is reached through: one for a surface that carries a platform, otherwise the
    ///     platform of each matched DAT entry. An empty platform collection resolves from evidence alone; a null key is an
    ///     explicit platform context that no kind claims. Neither case infers a platform from a filename.
    /// </summary>
    /// <remarks>
    ///     Each platform intersects the evidence candidates with the kinds that claim it: one left is unique, several are
    ///     ambiguous and none contradicts the evidence. Content with no matching observations uses the opaque fallback.
    ///     Several platforms resolve to a
    ///     kind only when each resolves to that same kind, or all fall back.
    /// </remarks>
    public static MediaKindResolution Resolve(MediaKindCatalog kinds, RomContentDescriptor content,
        IReadOnlyCollection<string?> platformKeys)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(platformKeys);
        var evidence = Candidates(kinds, content);
        if (platformKeys.Count == 0)
            return Narrow(evidence, kinds.Fallback);
        string?[] platforms = platformKeys.Distinct(StringComparer.Ordinal).ToArray();
        var resolved = platforms.Select(key => evidence.Count > 0
                ? Narrow(evidence.Where(kind => Claims(kind, key)).ToArray(), null)
                : MediaKindResolution.FallbackOf(kinds.Fallback))
            .Distinct().ToArray();
        if (platforms.Length == 1)
            return resolved[0];
        return resolved is [{ Outcome: MediaKindOutcome.Unique or MediaKindOutcome.Fallback } agreed] ? agreed : MediaKindResolution.Ambiguous;
    }

    /// <summary>Matches raw observations and immutable graphs through the current registration's pure rules.</summary>
    public static IReadOnlyList<MediaKindDescriptor> Candidates(MediaKindCatalog kinds, RomContentDescriptor content)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(content);
        return kinds.Kinds.Where(kind => kind.MatchesFacts!(content)).ToArray();
    }

    private static MediaKindResolution Narrow(IReadOnlyList<MediaKindDescriptor> left, MediaKindDescriptor? fallback) => left switch
    {
        [var kind] => MediaKindResolution.Of(kind),
        [] => fallback is null ? MediaKindResolution.Contradicted : MediaKindResolution.FallbackOf(fallback),
        _ => MediaKindResolution.Ambiguous
    };

    private static bool Claims(MediaKindDescriptor kind, string? platformKey) =>
        platformKey is not null && kind.PlatformKeys.Contains(platformKey, StringComparer.Ordinal);

}
