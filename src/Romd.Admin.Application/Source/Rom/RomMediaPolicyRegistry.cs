using System.Collections.Frozen;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>Immutable whole-file profiles used to rank independently measured imports.</summary>
public sealed class RomMediaPolicyRegistry
{
    private readonly FrozenDictionary<RomIsoDiscKind, IRomMediaTypePolicy> _byKind;
    public RomMediaPolicyRegistry(IEnumerable<IRomMediaTypePolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var declared = policies.ToArray();
        if (declared.Length == 0 || declared.Any(policy => policy is null))
            throw new ArgumentException("At least one non-null media policy is required.", nameof(policies));
        var registered = declared.Select(policy => new RegisteredPolicy(policy)).ToArray();
        if (registered.Any(policy => !Enum.IsDefined(policy.DiscKind) ||
            !policy.EncodingProfile.StoredEncoding.IsEncoded() || !RecipeId.TryParse(policy.EncodingProfile.RecipeVersion, out _) ||
            string.IsNullOrWhiteSpace(policy.PlatformKey) || policy.PlatformKey.Length > 64 || policy.PlatformKey.Any(char.IsControl)) ||
            registered.GroupBy(policy => policy.DiscKind).Any(group => group.Count() != 1) ||
            registered.GroupBy(policy => policy.EncodingProfile.RecipeVersion, StringComparer.Ordinal)
                .Any(group => group.Select(policy => policy.EncodingProfile.StoredEncoding).Distinct().Count() != 1))
            throw new ArgumentException("Media policies must have unique disc kinds and unambiguous encoded recipes.", nameof(policies));
        Policies = Array.AsReadOnly<IRomMediaTypePolicy>(registered);
        _byKind = registered.ToFrozenDictionary(policy => policy.DiscKind, policy => (IRomMediaTypePolicy)policy);

    }
    /// <summary>Resolves observed whole-file kind through the registered policies, never a persisted kind cache.</summary>
    public RomIsoDiscKind? ResolveObservedKind(RomContentDescriptor content)
    {
        var candidates = Policies.Where(policy => policy.MatchesFacts(content)).Select(policy => policy.DiscKind).Distinct().ToArray();
        return candidates is [var kind] ? kind : null;
    }
    public IReadOnlyList<IRomMediaTypePolicy> Policies { get; }
    public IRomMediaTypePolicy? ForDisc(RomIsoDiscKind kind) => _byKind.GetValueOrDefault(kind);
    public static bool IsCompatible(RomRepresentationEncoding encoding, IRomMediaTypePolicy policy) =>
        encoding == RomRepresentationEncoding.Identity || encoding.IsEncoded() && encoding == policy.EncodingProfile.StoredEncoding;
    public string? ResolveRecipe(RomRepresentationEncoding encoding, RomIsoDiscKind? kind)
    {
        if (!Enum.IsDefined(encoding) || kind is { } value && !Enum.IsDefined(value))
            return null;
        if (encoding == RomRepresentationEncoding.Identity && kind is null)
            return null;
        return UniqueRecipe(Compatible(encoding, kind, null));
    }
    /// <summary>Known kinds use their exact policy; unknown kinds require every encoding-compatible policy to agree.</summary>
    public RomRepresentationProfileClass Classify(RomContainerFacts container, RomIsoDiscKind? kind)
    {
        var encoding = container.Encoding;
        if (!encoding.IsEncoded() || kind is { } value && !Enum.IsDefined(value))
            return RomRepresentationProfileClass.Unknown;
        if (kind is { } known)
            return ForDisc(known) is { } policy && policy.EncodingProfile.StoredEncoding == encoding
                ? DefinedClassification(policy.EncodingProfile.Classify(container)) : RomRepresentationProfileClass.Unknown;
        RomRepresentationProfileClass? result = null;
        foreach (var policy in Policies)
        {
            if (policy.EncodingProfile.StoredEncoding != encoding)
                continue;
            var classification = DefinedClassification(policy.EncodingProfile.Classify(container));
            if (result is { } prior && prior != classification)
                return RomRepresentationProfileClass.Unknown;
            result = classification;
        }
        return result ?? RomRepresentationProfileClass.Unknown;
    }
    private static RomRepresentationProfileClass DefinedClassification(RomRepresentationProfileClass value) =>
        Enum.IsDefined(value) ? value : RomRepresentationProfileClass.Unknown;
    private IEnumerable<IRomMediaTypePolicy> Compatible(RomRepresentationEncoding encoding, RomIsoDiscKind? kind, string? recipe) =>
        Policies.Where(policy => (kind is null || policy.DiscKind == kind) && IsCompatible(encoding, policy) &&
            (recipe is null || policy.EncodingProfile.RecipeVersion == recipe));
    private static string? UniqueRecipe(IEnumerable<IRomMediaTypePolicy> policies)
    {
        string[] versions = policies.Select(policy => policy.EncodingProfile.RecipeVersion).Distinct(StringComparer.Ordinal).ToArray();
        return versions.Length == 1 ? versions[0] : null;
    }
    private sealed class RegisteredPolicy : IRomMediaTypePolicy
    {
        private readonly IRomMediaTypePolicy _strategy;
        public RegisteredPolicy(IRomMediaTypePolicy strategy)
        {
            _strategy = strategy;
            DiscKind = strategy.DiscKind;
            PlatformKey = strategy.PlatformKey;
            EncodingProfile = strategy.EncodingProfile is { } profile ? new RegisteredEncodingProfile(profile)
                : throw new ArgumentException("Every media policy needs an encoding profile.", nameof(strategy));
        }
        public RomIsoDiscKind DiscKind { get; }
        public string PlatformKey { get; }
        public IRomMediaEncodingProfile EncodingProfile { get; }
        public bool MatchesFacts(RomContentDescriptor content) => _strategy.MatchesFacts(content);
        public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct) => _strategy.IsEncodingEligible(content, ct);
        public bool ObservesEncodingLayout(RomLogicalInspection inspection) => _strategy.ObservesEncodingLayout(inspection);
    }

    private sealed class RegisteredEncodingProfile : IRomMediaEncodingProfile
    {
        private readonly IRomMediaEncodingProfile _strategy;
        public RegisteredEncodingProfile(IRomMediaEncodingProfile strategy)
        {
            _strategy = strategy;
            StoredEncoding = strategy.StoredEncoding;
            RecipeVersion = strategy.RecipeVersion;
        }
        public RomRepresentationEncoding StoredEncoding { get; }
        public string RecipeVersion { get; }
        public RomRepresentationProfileClass Classify(RomContainerFacts container) => _strategy.Classify(container);
        public long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) =>
            _strategy.GetMaximumOutputBytes(logicalBytes, maxLogicalBytes, ct);
    }
}
