using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>Pure desired/actual diff. A capability change never rewrites retained decoder evidence or encoding provenance.</summary>
public static class MediaReconciliationPlanner
{
    public static MediaReconciliationPlan Decide(MediaReconciliationPolicy policy, MediaReconciliationState actual,
        bool conversionRequested, IMediaHostCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(capabilities);
        var encoded = actual.Representations.Where(policy.AppliesTo).ToArray();
        if (encoded.Length != 0)
        {
            var decoder = ResidentDecoder(policy, capabilities);
            if (decoder is null)
                return new MediaReconciliationPlan.Unavailable(MediaReconciliationUnavailable.Decoder);
            var canonical = encoded.Where(policy.IsCanonical)
                .OrderByDescending(representation => HasProof(representation, decoder))
                .ThenByDescending(representation => representation.Id == actual.PreferredRepresentationId)
                .ThenBy(representation => representation.Id).FirstOrDefault();
            if (canonical is not null)
            {
                var proof = canonical.Verifications.SingleOrDefault(proof => proof.Decoder.Equals(decoder));
                if (proof is null)
                    return new MediaReconciliationPlan.Verify(canonical.Id, decoder);
                return actual.OwnershipNeedsAlignment || actual.Representations.Count > 1
                    ? new MediaReconciliationPlan.Release(canonical.Id, proof.Id, decoder)
                    : new MediaReconciliationPlan.None(MediaReconciliationNoWork.Canonical);
            }
            var missing = encoded.Where(representation => !HasProof(representation, decoder))
                .OrderByDescending(representation => representation.Id == actual.PreferredRepresentationId)
                .ThenBy(representation => representation.Id).FirstOrDefault();
            if (missing is not null)
                return new MediaReconciliationPlan.Verify(missing.Id, decoder);
        }
        if (policy.Kind.ConversionMode == ConversionMode.OnRequest && !conversionRequested)
            return new MediaReconciliationPlan.None(MediaReconciliationNoWork.ConversionNotRequested);
        if (!actual.EncodingEligible)
            return new MediaReconciliationPlan.None(MediaReconciliationNoWork.Ineligible);
        if (ResidentDecoder(policy, capabilities) is null)
            return new MediaReconciliationPlan.Unavailable(MediaReconciliationUnavailable.Decoder);
        return capabilities.CanEncode(policy.Kind.Kind)
            ? new MediaReconciliationPlan.Encode(policy.Recipe)
            : new MediaReconciliationPlan.Unavailable(MediaReconciliationUnavailable.Encoder);
    }

    private static bool HasProof(RomRepresentationFacts representation, RomDecoderIdentity decoder) =>
        representation.Verifications.Any(proof => proof.Decoder.Equals(decoder));

    private static RomDecoderIdentity? ResidentDecoder(MediaReconciliationPolicy policy, IMediaHostCapabilities capabilities)
    {
        if (!capabilities.CanDecode(policy.Kind.Kind))
            return null;
        if (policy.Kind.StoredEncoding == RomRepresentationEncoding.Chd)
            return capabilities.Decoder(policy.Kind.StoredEncoding) is { } native && capabilities.Qualifies(policy.Kind.Kind, native.Rid)
                ? new RomDecoderIdentity.Native(native) : null;
        string managed = capabilities.ManagedDecoderBuild;
        return string.IsNullOrWhiteSpace(managed) || managed.Length > 256 || managed.Any(char.IsControl)
            ? null : new RomDecoderIdentity.Managed(managed);
    }
}
