namespace Romd.Application.Common.Concurrency;

/// <summary>
///     The validators a client echoed in <c>If-Match</c>, unquoted. The host builds it and answers an absent or
///     wildcard header itself; the write's handler compares it with the current revision inside its own
///     transaction (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency"). Weak validators never
///     reach it, so an empty precondition is satisfied by nothing.
/// </summary>
public sealed class RevisionPrecondition
{
    private readonly string[] _revisions;

    public RevisionPrecondition(IEnumerable<string> revisions) => _revisions = [.. revisions];

    /// <summary>A precondition on exactly one revision.</summary>
    public static RevisionPrecondition For(string revision) => new([revision]);

    /// <summary>Strong comparison: one echoed validator equals the current revision exactly.</summary>
    public bool IsSatisfiedBy(string currentRevision) => _revisions.Contains(currentRevision, StringComparer.Ordinal);

    public bool IsSatisfiedBy(Guid currentRevision) => IsSatisfiedBy(currentRevision.ToString());
}
