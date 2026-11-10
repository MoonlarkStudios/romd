using ErrorOr;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Storage;

/// <summary>Admits exactly the captured deployed codec and complete retained input vector, before processing or payload access.</summary>
public sealed class MediaReconciliationEncoding : IMediaReconciliationEncoding
{
    private readonly RomMediaComposition _composition;
    private readonly IMediaHostCapabilities _capabilities;
    private readonly IMediaReconciliationMediaProfile _profile;
    private readonly IMediaFactReader _facts;
    private readonly RomWholeReconciliationEncoding _whole;
    private readonly RomCdReconciliationEncoding _cd;
    public MediaReconciliationEncoding(RomMediaComposition composition,
        IMediaHostCapabilities capabilities,
        IMediaReconciliationMediaProfile profile,
        IMediaFactReader facts,
        RomWholeReconciliationEncoding whole,
        RomCdReconciliationEncoding cd)
    {
        this._composition = composition;
        this._capabilities = capabilities;
        this._profile = profile;
        this._facts = facts;
        this._whole = whole;
        this._cd = cd;
    }

    public async Task<ErrorOr<IMediaReconciliationEncodingLease>> AcquireAsync(MediaReconciliationAttempt attempt, CancellationToken ct = default)
    {
        var capture = attempt.Capture;
        if (capture.Identity.Operation != MediaReconciliationOperation.Encode || capture.KindId is not { } kind ||
            !_composition.Reconciliation.TryGetValue(kind, out var policy) || capture.Identity.Recipe != policy.Recipe ||
            capture.Identity.LimitsFingerprint != _profile.LimitsFingerprint || _profile.Encoder(kind) is not { } encoder ||
            encoder.Build != capture.EncoderBuild || encoder.WholeDiscKind != capture.DiscKind)
            return MediaReconciliationErrors.Changed;
        if (!_capabilities.CanEncode(kind) || !_capabilities.CanDecode(kind))
            return RomMediaVerificationErrors.Unavailable;
        RomDecoderIdentity? decoder = policy.Kind.StoredEncoding == RomRepresentationEncoding.Chd
            ? _capabilities.Decoder(RomRepresentationEncoding.Chd) is { } native && _capabilities.Qualifies(kind, native.Rid) ? new RomDecoderIdentity.Native(native) : null
            : string.IsNullOrWhiteSpace(_capabilities.ManagedDecoderBuild) ? null : new RomDecoderIdentity.Managed(_capabilities.ManagedDecoderBuild);
        if (decoder is null || !decoder.Equals(capture.Decoder))
            return RomMediaVerificationErrors.Unavailable;
        var inputs = capture.Sources.Inputs;
        int[] ids = inputs.Select(input => input switch { MediaReconciliationInput.Logical logical => logical.Graph.Id, MediaReconciliationInput.Artifact artifact => artifact.Graph.Id, _ => 0 })
            .Where(id => id > 0).Distinct().ToArray();
        var current = await _facts.ReadAsync(ids, ct);
        List<MediaReconciliationInput> refreshed = [];
        foreach (var input in inputs)
        {
            if (input is MediaReconciliationInput.Logical logical)
            {
                if (!current.TryGetValue(logical.Graph.Id, out var graph) || !graph.Outputs.Any(output => output == logical.Output) ||
                    !graph.Verifications.Any(proof => proof.Id == logical.VerificationId))
                    return MediaReconciliationErrors.Changed;
                refreshed.Add(new MediaReconciliationInput.Logical(logical.Role, logical.Path!, graph, logical.Output.Id, logical.VerificationId));
            }
            else if (input is MediaReconciliationInput.Artifact artifact)
            {
                if (!current.TryGetValue(artifact.Graph.Id, out var graph) || artifact.VerificationId is { } proof && !graph.Verifications.Any(row => row.Id == proof))
                    return MediaReconciliationErrors.Changed;
                refreshed.Add(new MediaReconciliationInput.Artifact(graph, artifact.VerificationId, null));
            }
            else
                refreshed.Add(input);
        }
        if (new MediaReconciliationSources(refreshed, capture.Sources.Layout, capture.Sources.LogicalInspectionIds, capture.Sources.ReferenceFingerprint).Fingerprint != capture.Sources.Fingerprint)
            return MediaReconciliationErrors.Changed;
        if (policy.Kind.ManifestType == MediaManifestType.CdTracks)
            return _composition.CdRecipes.ForKind(kind) is { } recipe ? await _cd.AcquireAsync(attempt, policy, recipe, ct) : RomMediaVerificationErrors.Unavailable;
        return _composition.WholeEncoding.TryGetValue(kind, out var settings) ? await _whole.AcquireAsync(attempt, policy, settings, ct) : RomMediaVerificationErrors.Unavailable;
    }
}
