using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Media.Chd;

namespace Romd.Infrastructure.Storage;

/// <summary>
///     This host's media capabilities, derived on every call from the CHD builds verified at startup and the runtime each
///     media kind's registration qualifies. RVZ and original bytes need no native build, so every host decodes them; only a
///     host that runs media encoding encodes. Per-read native verification stays with read admission.
/// </summary>
public sealed class MediaHostCapabilities : IMediaHostCapabilities
{
    private readonly Func<ChdNativeIdentity?> _decoder;
    private readonly Func<ChdmanToolIdentity?> _encoder;
    private readonly MediaKindCatalog _kinds;
    private readonly IReadOnlyDictionary<string, string> _qualifiedRuntimes;
    private readonly IReadOnlyDictionary<string, string> _qualifiedEncodingRuntimes;
    private readonly bool _encodes;

    /// <param name="deployment">The configured CHD builds; a build answers only while the deployment is verified.</param>
    /// <param name="media">The media kinds and the runtime each qualified kind names.</param>
    /// <param name="encodes">Whether this host runs media encoding; only the worker does.</param>
    public MediaHostCapabilities(ChdDeploymentRuntime deployment, RomMediaComposition media, bool encodes)
        : this(Resident(deployment, assets => assets.Native.ExpectedIdentity),
            Resident(deployment, assets => assets.Tool?.ExpectedIdentity), media, encodes)
    {
    }

    // Native-free observers supply synthetic resident identities. Production always reads the verified deployment.
    internal MediaHostCapabilities(Func<ChdNativeIdentity?> decoder, Func<ChdmanToolIdentity?> encoder, RomMediaComposition media,
        bool encodes)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(media);
        _decoder = decoder;
        _encoder = encoder;
        _kinds = media.Kinds;
        _qualifiedRuntimes = media.QualifiedRuntimes;
        _qualifiedEncodingRuntimes = media.QualifiedEncodingRuntimes;
        _encodes = encodes;
    }

    public RomChdDecoderBuild? Decoder(RomRepresentationEncoding encoding) =>
        encoding == RomRepresentationEncoding.Chd && _decoder() is { } identity ? ChdBuildEvidence.Decoder(identity) : null;

    public string ManagedDecoderBuild => Romd.Media.Inspection.MediaInspectionService.DecoderBuild;

    public bool CanDecode(string kind) => _kinds.Find(kind) is { } descriptor && Decodes(descriptor);

    public bool CanEncode(string kind) =>
        _encodes && _kinds.Find(kind) is { CurrentRecipeId: not null } descriptor && Decodes(descriptor) &&
        (descriptor.StoredEncoding != RomRepresentationEncoding.Chd || _encoder() is { } tool && Qualifies(kind, tool.Rid) &&
            (!_qualifiedEncodingRuntimes.TryGetValue(kind, out var runtime) ||
                tool.Rid == runtime && Decoder(RomRepresentationEncoding.Chd)?.Rid == runtime));

    public string? QualifiedRuntime(string kind) => _qualifiedRuntimes.GetValueOrDefault(kind);

    public bool Qualifies(string kind, string runtimeIdentifier) =>
        _kinds.Find(kind) is not null && !string.IsNullOrWhiteSpace(runtimeIdentifier) &&
        (!_qualifiedRuntimes.TryGetValue(kind, out var runtime) || runtime == runtimeIdentifier);

    /// <summary>Only CHD reads through a native build; that build must be ready and qualified for the kind.</summary>
    private bool Decodes(MediaKindDescriptor descriptor) => descriptor.StoredEncoding != RomRepresentationEncoding.Chd ||
        Decoder(RomRepresentationEncoding.Chd) is { } build && Qualifies(descriptor.Kind, build.Rid);

    private static Func<T?> Resident<T>(ChdDeploymentRuntime deployment, Func<ChdDeploymentAssets, T?> identity) where T : class
    {
        ArgumentNullException.ThrowIfNull(deployment);
        return () => deployment.IsReady && deployment.Assets is { } assets ? identity(assets) : null;
    }
}
