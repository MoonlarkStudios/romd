using Romd.Domain.Source.Rom;

namespace Romd.Application.Common.Content;

/// <summary>
/// Already scoped holdings and current derived eligibility for one logical subject. This is neither persisted policy nor
/// authorization. The policy must already resolve uniquely for this subject. Execution must capture exact sources and
/// recheck scope, ownership and policy under publication locks.
/// A complete original CD set can contain several whole-file input representations before its first CD archive exists.
/// </summary>
public sealed class MediaReconciliationState
{
    public const int MaximumRepresentations = 256;
    public MediaReconciliationState(IReadOnlyList<RomRepresentationFacts> representations, int? preferredRepresentationId,
        bool encodingEligible, bool ownershipNeedsAlignment)
    {
        ArgumentNullException.ThrowIfNull(representations);
        var captured = representations.ToArray();
        if (captured.Length > MaximumRepresentations || captured.Any(representation => representation is null) ||
            captured.Select(representation => representation.Id).Distinct().Count() != captured.Length ||
            preferredRepresentationId is { } preferred && !captured.Any(representation => representation.Id == preferred))
            throw new ArgumentException("Reconciliation needs distinct retained holdings and an owned preference.", nameof(representations));
        Representations = Array.AsReadOnly(captured);
        PreferredRepresentationId = preferredRepresentationId;
        EncodingEligible = encodingEligible;
        OwnershipNeedsAlignment = ownershipNeedsAlignment;
    }
    public IReadOnlyList<RomRepresentationFacts> Representations { get; }
    public int? PreferredRepresentationId { get; }
    public bool EncodingEligible { get; }
    public bool OwnershipNeedsAlignment { get; }
}
