using System.Collections.Frozen;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Media.PlayStation;
using Romd.Media.Rvz;

namespace Romd.Infrastructure.Storage;

/// <summary>
///     A recipe as its media kinds register it: one id, its encoding and the container parameters its constants fix, so two
///     registrations of one id cannot define it differently.
/// </summary>
internal sealed record RomMediaRecipe(string Id, RomRepresentationEncoding Encoding, string Parameters, MediaRecipeRebuild Rebuild);

/// <summary>
///     One encoded media kind as production registers it. A whole-file kind reconciles through the policy its factory
///     builds for the registered platform; a CD track kind reconciles on its own path, under the recipe its factory builds
///     for the registered kind and platforms. A kind qualified on one runtime names it; host capabilities apply it to every
///     native build that encodes, verifies or reads the kind.
/// </summary>
internal sealed record RomMediaKindRegistration(string Kind, MediaManifestType ManifestType, ConversionMode ConversionMode,
    IReadOnlyList<string> PlatformKeys, RomMediaRecipe Recipe, Func<string, IRomMediaTypePolicy>? WholeFilePolicy,
    IReadOnlyList<string> NativeRuntimes, string? OriginalExtension, IReadOnlyList<string> ProfileVersions,
    Func<string, IReadOnlyList<string>, RomCdStorageRecipe>? CdRecipe = null, string? QualifiedRuntime = null,
    Func<RomContentDescriptor, bool>? MatchesFacts = null, string? QualifiedEncodingRuntime = null,
    Func<RomRepresentationFacts, bool>? MatchesLayout = null)
{
    public MediaKindDescriptor Describe(IRomMediaTypePolicy? policy, string? bootProfile)
    {
        var matcher = MatchesFacts;
        if (matcher is null && policy is not null)
            matcher = policy.MatchesFacts;
        return new(Kind, PlatformKeys, Recipe.Encoding, Recipe.Id, ManifestType, ConversionMode, NativeRuntimes, OriginalExtension, ProfileVersions, matcher, MatchesLayout, bootProfile);
    }
}

/// <param name="ObservesEncodingLayout">The kind's own source-layout rule; encoding never infers it from <paramref name="DiscKind" />.</param>
public sealed record RomWholeEncodingProfile(RomIsoDiscKind DiscKind, Func<long, long, CancellationToken, long> MaximumOutputBytes,
    Func<RomLogicalInspection, bool> ObservesEncodingLayout);


/// <summary>
///     The media kinds, their reconciliation policies, the CD track kind's recipe, their delivery view and the runtime each
///     qualified kind names, built and validated together. A kind absent from <paramref name="QualifiedRuntimes" /> qualifies
///     every verified build.
/// </summary>
public sealed record RomMediaComposition(MediaKindCatalog Kinds, RomMediaPolicyRegistry Registry, RomDeliveryPolicy DeliveryPolicy,
    RomCdStorageRecipes CdRecipes, IReadOnlyDictionary<string, string> QualifiedRuntimes,
    IReadOnlyDictionary<string, MediaReconciliationPolicy> Reconciliation, IReadOnlyDictionary<string, RomWholeEncodingProfile> WholeEncoding)
{
    /// <summary>Additional encoding-only qualification; shared codec read admission keeps its existing rules.</summary>
    public IReadOnlyDictionary<string, string> QualifiedEncodingRuntimes { get; init; } = FrozenDictionary<string, string>.Empty;
}

/// <summary>The production media policy registration shared by hosts and qualification probes.</summary>
public static class RomMediaPolicyComposition
{
    private const string _discOriginalExtension = ".iso";
    private const string _qualifiedX64Runtime = "linux-x64";
    private static readonly IReadOnlyList<string> _rvzNativeRuntimes = Array.AsReadOnly(new[] { "dolphin" });

    /// <summary>The only runtime qualified to consume served PS2 DVD CHDs.</summary>
    private static readonly IReadOnlyList<string> _ps2DvdNativeRuntimes = Array.AsReadOnly(new[] { "pcsx2" });
    private static readonly IReadOnlyList<string> _rvzProfiles = Array.AsReadOnly(new[] { "rvz-import/v1", "rvz-canonical/v1" });
    private static readonly IReadOnlyList<string> _chdDvdProfiles = Array.AsReadOnly(new[] { "chd-observed/v1" });

    private static readonly RomMediaRecipe _rvzCanonical = new(RvzCanonicalRecipe.Id, RomRepresentationEncoding.Rvz,
        $"codec={RvzCanonicalRecipe.Compression};level={RvzCanonicalRecipe.CompressionLevel};chunk={RvzCanonicalRecipe.ChunkSize}",
        MediaRecipeRebuild.MatchingParameters);

    private static readonly RomMediaRecipe _chdDvdCanonical = new(ChdDvdCanonicalRecipe.Id, RomRepresentationEncoding.Chd,
        ChdDvdCanonicalRecipe.EncoderProfile, MediaRecipeRebuild.MatchingParameters);

    private static readonly IReadOnlyList<uint> _chdCdCodecs =
        Array.AsReadOnly(new[] { ChdCdEncodingRecipe.Codec, ChdCdEncodingRecipe.Codec1, 0u, 0u });

    private static readonly RomMediaRecipe _chdCdCanonical = new(ChdCdEncodingRecipe.Id, RomRepresentationEncoding.Chd,
        CdParameters(ChdCdEncodingRecipe.HunkBytes, _chdCdCodecs), MediaRecipeRebuild.MatchingParameters);

    private static readonly RomMediaRecipe _saturnCanonical = _chdCdCanonical with { Id = ChdCdEncodingRecipe.SaturnId };

    /// <summary>Unclaimed content makes no runtime or conversion claim; admitted holdings remain deliverable.</summary>
    private static readonly MediaKindDescriptor _opaque = new("opaque", [], RomRepresentationEncoding.Identity, null,
        MediaManifestType.WholeFile, ConversionMode.OnRequest, [], null, [RomEncodingProfiles.Identity]);

    internal static IReadOnlyList<RomMediaKindRegistration> Registrations { get; } =
    [
        WholeFile("gamecube-disc", "gc", _rvzCanonical, platform => new RvzMediaTypePolicy(RvzDiscFamily.GameCube, platform),
            _rvzNativeRuntimes, _rvzProfiles),
        WholeFile("wii-disc", "wii", _rvzCanonical, platform => new RvzMediaTypePolicy(RvzDiscFamily.Wii, platform),
            _rvzNativeRuntimes, _rvzProfiles),
        WholeFile("ps2-dvd", "ps2", _chdDvdCanonical, platform => new ChdDvdMediaTypePolicy(platform),
            _ps2DvdNativeRuntimes, _chdDvdProfiles),
        new("psp-umd", MediaManifestType.WholeFile, ConversionMode.Automatic, ["psp"], _chdDvdCanonical,
            platform => new PspUmdMediaTypePolicy(platform), [], _discOriginalExtension, _chdDvdProfiles, QualifiedEncodingRuntime: _qualifiedX64Runtime),
        // Complete eligible sets reconcile automatically after verified input publication.
        // Its encoder, verification and member reads are qualified on Linux x64 only.
        new("ps1-cd-set", MediaManifestType.CdTracks, ConversionMode.Automatic, ["psx"], _chdCdCanonical, null, [], null,
            [ChdCdVerification.CurrentProfile, ChdCdRetainedRequest.CurrentProfile, ChdCdInspectionService.Profile],
            (kind, platforms) => new RomCdStorageRecipe(kind, _chdCdCanonical.Id, ChdCdEncodingRecipe.HunkBytes, _chdCdCodecs, platforms),
            _qualifiedX64Runtime, content => ChdCdMediaTypePolicy.MatchesFacts(content, RomPs1CdBoot.CurrentProfile),
            MatchesLayout: graph => ChdCdMediaTypePolicy.MatchesBoot(graph, RomPs1CdBoot.CurrentProfile)),
        // Explicit conversion until native real-disc storage and load qualification accepts Automatic.
        new("saturn-cd-set", MediaManifestType.CdTracks, ConversionMode.OnRequest, ["saturn"], _saturnCanonical, null, [], null,
            [ChdCdVerification.CurrentProfile, ChdCdRetainedRequest.CurrentProfile, ChdCdInspectionService.Profile],
            (kind, platforms) => new RomCdStorageRecipe(kind, _saturnCanonical.Id, ChdCdEncodingRecipe.HunkBytes, _chdCdCodecs, platforms, RomSaturnCdBoot.CurrentProfile),
            _qualifiedX64Runtime, content => ChdCdMediaTypePolicy.MatchesFacts(content, RomSaturnCdBoot.CurrentProfile),
            MatchesLayout: graph => ChdCdMediaTypePolicy.MatchesBoot(graph, RomSaturnCdBoot.CurrentProfile))
    ];

    public static RomMediaComposition Create() => Create(Registrations);

    public static RomMediaPolicyRegistry CreateRegistry() => Create().Registry;

    /// <summary>Delivery derives each platform's stored format from the registered media kinds, never from platform literals.</summary>
    public static RomDeliveryPolicy CreateDeliveryPolicy() => Create().DeliveryPolicy;

    /// <summary>
    ///     Rejects a recipe id defined twice differently, a reconciliation policy or CD recipe that disagrees with its kind, and a
    ///     CD track kind that names no qualified runtime.
    /// </summary>
    internal static RomMediaComposition Create(IReadOnlyList<RomMediaKindRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        if (registrations.Any(registration => registration?.Recipe is null))
            throw new ArgumentException("Every media kind registration needs its recipe.", nameof(registrations));
        if (registrations.GroupBy(registration => registration.Recipe.Id, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Select(registration => registration.Recipe).Distinct().Count() > 1) is { } recipe)
            throw new ArgumentException($"Recipe '{recipe.Key}' is defined more than one way.", nameof(registrations));
        if (registrations.FirstOrDefault(registration =>
                (registration.ManifestType == MediaManifestType.WholeFile) != (registration.WholeFilePolicy is not null)) is { } layout)
            throw new ArgumentException($"Media kind '{layout.Kind}' needs a reconciliation policy exactly when it is whole-file.",
                nameof(registrations));
        if (registrations.FirstOrDefault(registration =>
                (registration.ManifestType == MediaManifestType.CdTracks) != (registration.CdRecipe is not null)) is { } cdLayout)
            throw new ArgumentException($"Media kind '{cdLayout.Kind}' needs a CD recipe exactly when it rebuilds CD tracks.",
                nameof(registrations));
        if (registrations.FirstOrDefault(registration => registration.QualifiedRuntime is { } runtime && !IsRuntime(runtime)) is { } runtimeDefect)
            throw new ArgumentException($"Media kind '{runtimeDefect.Kind}' names a qualified runtime that is not a runtime identifier.",
                nameof(registrations));
        if (registrations.FirstOrDefault(registration => registration.QualifiedEncodingRuntime is { } runtime && !IsRuntime(runtime)) is { } encodingRuntimeDefect)
            throw new ArgumentException($"Media kind '{encodingRuntimeDefect.Kind}' names a qualified encoding runtime that is not a runtime identifier.",
                nameof(registrations));
        // CD archives are verified, and CD compression reports its availability, for one qualified runtime.
        if (registrations.FirstOrDefault(registration =>
                registration.ManifestType == MediaManifestType.CdTracks && registration.QualifiedRuntime is null) is { } unqualified)
            throw new ArgumentException($"Media kind '{unqualified.Kind}' rebuilds CD tracks, so it names the runtime it is qualified on.",
                nameof(registrations));
        var declaredPolicies = registrations.Where(registration => registration.WholeFilePolicy is not null)
            .Select(registration => (Registration: registration,
                Policy: registration.WholeFilePolicy!(registration.PlatformKeys.Single())))
            .ToArray();
        if (declaredPolicies.FirstOrDefault(pair => pair.Policy is null || pair.Policy.EncodingProfile is null || pair.Policy.PlatformKey != pair.Registration.PlatformKeys.Single() ||
                pair.Policy.EncodingProfile.StoredEncoding != pair.Registration.Recipe.Encoding ||
                pair.Policy.EncodingProfile.RecipeVersion != pair.Registration.Recipe.Id) is { Registration: { } mismatched })
            throw new ArgumentException($"Media kind '{mismatched.Kind}' has a reconciliation policy for another platform, encoding or recipe.",
                nameof(registrations));
        // Every view uses the same captured policy/profile metadata, including the originally registered profile instance.
        var registry = new RomMediaPolicyRegistry(declaredPolicies.Select(pair => pair.Policy));
        var policies = declaredPolicies.Select(pair => (pair.Registration, Policy: registry.ForDisc(pair.Policy.DiscKind)!)).ToArray();
        var cdRecipes = new RomCdStorageRecipes(registrations.Where(registration => registration.CdRecipe is not null).Select(cd =>
        {
            var recipe = cd.CdRecipe!(cd.Kind, cd.PlatformKeys);
            if (recipe is null || recipe.Kind != cd.Kind || recipe.Id != cd.Recipe.Id ||
                !recipe.PlatformKeys.SequenceEqual(cd.PlatformKeys) || CdParameters(recipe.HunkBytes, recipe.Codecs) != cd.Recipe.Parameters)
                throw new ArgumentException($"Media kind '{cd.Kind}' has a CD recipe for another kind, recipe, platform or parameters.", nameof(registrations));
            return recipe;
        }), "ps1-cd-set");
        var kinds = new MediaKindCatalog(registrations.Select(registration =>
            registration.Describe(policies.FirstOrDefault(pair => ReferenceEquals(pair.Registration, registration)).Policy,
                cdRecipes.ForKind(registration.Kind)?.BootProfile)).Append(_opaque));
        var qualified = registrations.Where(registration => registration.QualifiedRuntime is not null)
            .ToFrozenDictionary(registration => registration.Kind, registration => registration.QualifiedRuntime!, StringComparer.Ordinal);
        var reconciliation = registrations.ToFrozenDictionary(registration => registration.Kind, registration =>
        {
            var policy = policies.FirstOrDefault(pair => ReferenceEquals(pair.Registration, registration)).Policy;
            Func<RomContainerFacts, bool> parameters = policy is not null
                ? container => policy.EncodingProfile.Classify(container) == RomRepresentationProfileClass.Canonical
                : cdRecipes.ForKind(registration.Kind)!.MatchesParameters;
            return new MediaReconciliationPolicy(kinds.Find(registration.Kind)!, parameters, registration.Recipe.Rebuild,
                policy is not null ? policy.IsEncodingEligible : ChdCdMediaTypePolicy.IsEncodingEligible,
                policy is null ? registration.MatchesLayout ?? ChdCdMediaTypePolicy.MatchesGraph : null,
                policy is null ? ChdCdVerification.CurrentProfile : null);
        }, StringComparer.Ordinal);
        return new(kinds, registry,
            new RomDeliveryPolicy(CreateFormats(), kinds, policies.Where(pair => pair.Policy.EncodingProfile.StoredEncoding == RomRepresentationEncoding.Chd)
                .ToDictionary(pair => pair.Registration.Kind,
                    pair => (Func<RomContainerFacts.Chd, bool>)(container => pair.Policy.EncodingProfile.Classify(container) == RomRepresentationProfileClass.Canonical),
                    StringComparer.Ordinal)), cdRecipes, qualified, reconciliation, policies.ToFrozenDictionary(pair => pair.Registration.Kind,
                pair => new RomWholeEncodingProfile(pair.Policy.DiscKind, pair.Policy.EncodingProfile.GetMaximumOutputBytes, pair.Policy.ObservesEncodingLayout), StringComparer.Ordinal))
        {
            QualifiedEncodingRuntimes = registrations.Where(registration => registration.QualifiedEncodingRuntime is not null)
                .ToFrozenDictionary(registration => registration.Kind, registration => registration.QualifiedEncodingRuntime!, StringComparer.Ordinal)
        };
    }

    private static RomMediaKindRegistration WholeFile(string kind, string platform, RomMediaRecipe recipe,
        Func<string, IRomMediaTypePolicy> reconciliation, IReadOnlyList<string> runtimes, IReadOnlyList<string> profiles) =>
        new(kind, MediaManifestType.WholeFile, ConversionMode.Automatic, [platform], recipe, reconciliation, runtimes,
            _discOriginalExtension, profiles);

    private static bool IsRuntime(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '.' or '_');

    /// <summary>The registered parameters of a CD recipe: the hunk size and codec slots its encoder and verifier use.</summary>
    private static string CdParameters(uint hunkBytes, IReadOnlyList<uint> codecs) =>
        $"cd;hunk={hunkBytes};codecs={string.Join(',', codecs.Select(codec => codec.ToString("x8")))}";

    /// <summary>Every delivery surface labels a format by this label and names an encoded format's file by this extension.</summary>
    private static RomDeliveryFormatCatalog CreateFormats() => new([
        new(RomDeliveryFormat.Original, "Original", null), new(RomDeliveryFormat.Rvz, "RVZ", ".rvz"),
        new(RomDeliveryFormat.Chd, "CHD", ".chd")]);
}
