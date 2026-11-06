using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>Current registered policy over captured parameters and independent creation witnesses.</summary>
public sealed class MediaReconciliationPolicy
{
    private readonly Func<RomContainerFacts, bool> _matchesParameters;
    private readonly Func<RomContentDescriptor, CancellationToken, bool> _isEncodingEligible;
    private readonly Func<RomRepresentationFacts, bool>? _matchesRetainedLayout;
    public MediaReconciliationPolicy(MediaKindDescriptor kind, Func<RomContainerFacts, bool> matchesParameters,
        MediaRecipeRebuild rebuild, Func<RomContentDescriptor, CancellationToken, bool> isEncodingEligible,
        Func<RomRepresentationFacts, bool>? matchesRetainedLayout, string? adoptionProfile)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(matchesParameters);
        ArgumentNullException.ThrowIfNull(rebuild);
        ArgumentNullException.ThrowIfNull(isEncodingEligible);
        if ((kind.ManifestType == MediaManifestType.CdTracks) != (matchesRetainedLayout is not null && !string.IsNullOrWhiteSpace(adoptionProfile)) ||
            kind.ManifestType == MediaManifestType.WholeFile && (matchesRetainedLayout is not null || adoptionProfile is not null) ||
            adoptionProfile is not null && !kind.ProfileVersions.Contains(adoptionProfile, StringComparer.Ordinal))
            throw new ArgumentException("Only CD layout policies supply their graph matcher and registered adoption profile.");
        if (!kind.StoredEncoding.IsEncoded() || !RecipeId.TryParse(kind.CurrentRecipeId, out var recipe) ||
            !Enum.IsDefined(kind.ManifestType) || !Enum.IsDefined(kind.ConversionMode))
            throw new ArgumentException("Reconciliation needs an encoded kind and its registered recipe.", nameof(kind));
        Kind = kind with
        {
            PlatformKeys = Array.AsReadOnly(kind.PlatformKeys.ToArray()),
            NativeRuntimes = Array.AsReadOnly(kind.NativeRuntimes.ToArray()),
            ProfileVersions = Array.AsReadOnly(kind.ProfileVersions.ToArray())
        };
        Recipe = recipe;
        Rebuild = rebuild;
        _matchesParameters = matchesParameters;
        _isEncodingEligible = isEncodingEligible;
        _matchesRetainedLayout = matchesRetainedLayout;
        AdoptionProfile = adoptionProfile;
    }
    public string? AdoptionProfile { get; }
    public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct) => _isEncodingEligible(content, ct);
    /// <summary>Matches observed layout facts even when every logical owner has been withdrawn.</summary>
    public bool MatchesRetainedLayout(RomRepresentationFacts graph) => _matchesRetainedLayout?.Invoke(graph) == true;
    public MediaKindDescriptor Kind { get; }
    public RecipeId Recipe { get; }
    public MediaRecipeRebuild Rebuild { get; }
    public bool AppliesTo(RomRepresentationFacts representation) => representation.Container.Encoding == Kind.StoredEncoding &&
        (Kind.ManifestType == MediaManifestType.WholeFile ? representation.Manifest is RomReconstructionManifest.WholeFile
            : representation.Manifest is RomReconstructionManifest.CdTracks);
    public bool MatchesParameters(RomContainerFacts container) => _matchesParameters(container);
    public bool IsCanonical(RomRepresentationFacts representation) => AppliesTo(representation) && _matchesParameters(representation.Container) &&
        (Rebuild.Reason is null || representation.Creations.Any(creation => creation.Recipe == Recipe));
}
