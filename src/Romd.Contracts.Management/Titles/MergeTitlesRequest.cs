namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Request to merge a source title into the title named in the route. The source's games move to the
///     target, metadata is merged, and the source is deleted.
/// </summary>
public sealed record MergeTitlesRequest
{
    /// <summary>
    ///     The Sqid of the title to merge FROM (will be deleted).
    /// </summary>
    public required string SourceTitleId { get; init; }

    /// <summary>
    ///     The source title's revision as the client read it. The target is guarded by <c>If-Match</c>; this
    ///     second resource travels as a batch revision, and a stale one is a 409 with per-item errors
    ///     (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency").
    /// </summary>
    public required string SourceTitleRevision { get; init; }
}
