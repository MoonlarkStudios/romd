using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Media.PlayStation;

namespace Romd.Infrastructure.Storage;

/// <summary>Serves stored DVD/CD content only through the libchdr build deployed and freshly verified in this process.</summary>
/// <remarks>
/// The stored decoder evidence (RID, upstream source pin, recipe version) must equal the tuple of the freshly
/// re-verified resident build, so rebuilding an image keeps stored CHDs servable while a different pin or recipe
/// is refused until it is verified. The host must also decode every kind the evidence is consistent with. No decision
/// or native verification is cached; a failed verification invalidates readiness. It admits bytes only: runtime claims
/// come from the content's resolved media kind.
/// </remarks>
public sealed class ChdDeliveryAdmission : IRomChdDeliveryAdmission
{
    private readonly Func<bool> _isReady;
    private readonly Func<ChdNativeCandidate?> _native;
    private readonly Action _invalidate;
    private readonly Func<ChdNativeCandidate, ChdNativeIdentity> _verify;
    private readonly IMediaHostCapabilities _capabilities;
    private readonly string[] _dvdKinds;
    private readonly MediaKindDescriptor[] _cdKinds;
    private readonly string[] _managedKinds;

    /// <param name="runtime">The deployed native build, verified again on every assessment.</param>
    /// <param name="capabilities">This host's media capabilities; only decode capability is consulted.</param>
    /// <param name="kinds">The media kinds whose CHD evidence this admits: each layout's kinds must all be decodable here.</param>
    public ChdDeliveryAdmission(ChdDeploymentRuntime runtime, IMediaHostCapabilities capabilities, MediaKindCatalog kinds)
        : this(() => runtime.IsReady, () => runtime.Assets?.Native,
            (runtime ?? throw new ArgumentNullException(nameof(runtime))).Invalidate,
            native => native.VerifyAndLoad(), capabilities, kinds)
    {
    }

    // Native-free observers supply synthetic resident facts. Production always uses the runtime's real assets and loader.
    // Without explicit capabilities, the synthetic resident build decodes under the production media registration.
    internal ChdDeliveryAdmission(Func<bool> isReady, Func<ChdNativeCandidate?> native, Action invalidate,
        Func<ChdNativeCandidate, ChdNativeIdentity> verify)
        : this(isReady, native, invalidate, verify, Synthetic(isReady, native, out var kinds), kinds)
    {
    }

    internal ChdDeliveryAdmission(Func<bool> isReady, Func<ChdNativeCandidate?> native, Action invalidate,
        Func<ChdNativeCandidate, ChdNativeIdentity> verify, IMediaHostCapabilities capabilities, MediaKindCatalog kinds)
    {
        ArgumentNullException.ThrowIfNull(isReady);
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(invalidate);
        ArgumentNullException.ThrowIfNull(verify);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(kinds);
        _isReady = isReady;
        _native = native;
        _invalidate = invalidate;
        _verify = verify;
        _capabilities = capabilities;
        _dvdKinds = kinds.Kinds.Where(kind => kind.StoredEncoding == Romd.Domain.Source.Rom.RomRepresentationEncoding.Chd && kind.ManifestType == MediaManifestType.WholeFile).Select(kind => kind.Kind).ToArray();
        _cdKinds = kinds.Kinds.Where(kind => kind.StoredEncoding == Romd.Domain.Source.Rom.RomRepresentationEncoding.Chd && kind.ManifestType == MediaManifestType.CdTracks).ToArray();
        _managedKinds = kinds.Kinds.Where(kind => kind.ManifestType == MediaManifestType.WholeFile && kind.StoredEncoding == RomRepresentationEncoding.Rvz)
            .Select(kind => kind.Kind).ToArray();
    }

    private static MediaHostCapabilities Synthetic(Func<bool> isReady, Func<ChdNativeCandidate?> native, out MediaKindCatalog kinds)
    {
        ArgumentNullException.ThrowIfNull(isReady);
        ArgumentNullException.ThrowIfNull(native);
        var media = RomMediaPolicyComposition.Create();
        kinds = media.Kinds;
        return new(() => isReady() ? native()?.ExpectedIdentity : null, () => null, media, encodes: false);
    }

    public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Container is not RomContainerFacts.Rvz || artifact.Facts.Manifest is not RomReconstructionManifest.WholeFile ||
            !Decodes(_managedKinds) || !artifact.Facts.Verifications.Any(proof => proof.Decoder is RomDecoderIdentity.Managed managed &&
                managed.Build == _capabilities.ManagedDecoderBuild))
            return null;
        return artifact.BindManaged(_capabilities.ManagedDecoderBuild);
    }

    public bool AdmitsCd(RomReconstruction.CdMember member) => AssessCd(member) is not null;

    public Romd.Domain.Source.Rom.RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        // Decode capability gates the member's kind before any native work; encode capability is never consulted.
        var matching = _cdKinds.Where(kind => kind.MatchesLayout?.Invoke(member.Facts) ?? true).Select(kind => kind.Kind).ToArray();
        if (!Decodes(matching) || !_isReady() || _native() is not { } native ||
            !member.HasDecoder(ChdBuildEvidence.Decoder(native.ExpectedIdentity)))
            return null;
        try
        {
            var actual = _verify(native);
            if (actual is not null && ChdBuildEvidence.Decoder(actual) == ChdBuildEvidence.Decoder(native.ExpectedIdentity) && _isReady())
                return ChdBuildEvidence.Decoder(actual);
            _invalidate();
            return null;
        }
        catch (ChdNativeCandidateException) { _invalidate(); return null; }
        catch { _invalidate(); throw; }
    }

    public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.RepresentationId <= 0)
            return null;
        // Decode capability gates every kind the evidence is consistent with before any native work; encode capability is
        // never consulted, and a host that cannot decode one of them leaves readiness alone.
        if (!Decodes(_dvdKinds) || !_isReady() || _native() is not { } native)
            return null;
        // Evidence from another build tuple is simply not servable here; it says nothing about this deployment.
        if (ChdBuildEvidence.Decoder(native.ExpectedIdentity) != evidence.Decoder)
            return null;

        ChdNativeIdentity actual;
        try
        { actual = _verify(native); }
        catch (ChdNativeCandidateException)
        {
            _invalidate();
            return null;
        }
        catch
        {
            _invalidate();
            throw;
        }
        if (actual is null || ChdBuildEvidence.Decoder(actual) != evidence.Decoder)
        {
            _invalidate();
            return null;
        }
        // A shutdown or failed concurrent verification must not leave an earlier positive decision usable.
        if (!_isReady())
            return null;
        return new RomChdDeliveryAssessment(evidence.Decoder);
    }

    /// <summary>A layout with no registered kind is never admitted, and every kind it could be must be decodable here.</summary>
    private bool Decodes(string[] kinds) => kinds.Length > 0 && kinds.All(_capabilities.CanDecode);
}
