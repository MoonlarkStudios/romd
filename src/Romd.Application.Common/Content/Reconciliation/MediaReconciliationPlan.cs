using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

public enum MediaReconciliationNoWork { Canonical, ConversionNotRequested, Ineligible }
public enum MediaReconciliationUnavailable { Decoder, Encoder }

/// <summary>One next operation, without file/ownership authority or media side effects.</summary>
public abstract class MediaReconciliationPlan
{
    private MediaReconciliationPlan() { }
    public sealed class Verify : MediaReconciliationPlan
    {
        internal Verify(int representationId, RomDecoderIdentity decoder) => (RepresentationId, Decoder) = (representationId, decoder);
        public int RepresentationId { get; }
        public RomDecoderIdentity Decoder { get; }
    }
    public sealed class Encode : MediaReconciliationPlan
    {
        internal Encode(RecipeId recipe) => Recipe = recipe;
        public RecipeId Recipe { get; }
    }
    /// <summary>Canonical proof anchor for releasing this subject's redundant ownership; never a graph/CAS deletion grant.</summary>
    public sealed class Release : MediaReconciliationPlan
    {
        internal Release(int canonicalRepresentationId, Guid verificationId, RomDecoderIdentity decoder) =>
            (CanonicalRepresentationId, VerificationId, Decoder) = (canonicalRepresentationId, verificationId, decoder);
        public int CanonicalRepresentationId { get; }
        public Guid VerificationId { get; }
        public RomDecoderIdentity Decoder { get; }
    }
    public sealed class None : MediaReconciliationPlan
    {
        internal None(MediaReconciliationNoWork reason) => Reason = reason;
        public MediaReconciliationNoWork Reason { get; }
    }
    public sealed class Unavailable : MediaReconciliationPlan
    {
        internal Unavailable(MediaReconciliationUnavailable reason) => Reason = reason;
        public MediaReconciliationUnavailable Reason { get; }
    }
}
