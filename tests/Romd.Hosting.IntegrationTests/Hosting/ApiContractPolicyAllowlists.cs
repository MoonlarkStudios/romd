namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>The API alignment slice that removes an allowlisted violation.</summary>
internal enum OwningSlice
{
    S1,
    S2,
    S3,
    S4,
    S5,
    S6
}

internal enum RevisionFieldClassification
{
    Batch,
    SnapshotBinding
}

internal sealed record ReviewedRevisionField(RevisionFieldClassification Classification, string Rationale);

/// <summary>
///     Today's violations of the OpenAPI shape rules in <c>docs/decisions/api-contract-policy.md</c>, keyed as
///     <see cref="ApiContractPolicyRules" /> describes and annotated with the slice that removes them. Each slice
///     deletes its own entries, so the migration dictionaries R1 to R15 only shrink. A slice that moves or renames
///     an operation that still violates a rule re-keys its entry in the same commit; net additions to the
///     migration dictionaries are forbidden. The reviewed ledgers <see cref="ReviewedRequestRevisionFields" />
///     and <see cref="ReviewedPathParameterNotFoundExemptions" /> may grow, but only with a rationale. The R11
///     match covers names ending in <c>revision</c> or the plural <c>revisions</c>, in request bodies, query
///     parameters and headers.
/// </summary>
internal static class ApiContractPolicyAllowlists
{
    /// <summary>Permanent R11 entries: request revisions reviewed as batch revisions or snapshot bindings.</summary>
    public static readonly IReadOnlyDictionary<string, ReviewedRevisionField> ReviewedRequestRevisionFields =
        new Dictionary<string, ReviewedRevisionField>(StringComparer.Ordinal)
        {
            {
                "admin MergeTitlesRequest.sourceTitleRevision",
                new(RevisionFieldClassification.Batch,
                    "A merge writes two titles: the target is the If-Match resource of POST /api/titles/{titleId}/merge, " +
                    "and the absorbed source, which the merge deletes, is the second item. A stale source is a 409 " +
                    "with the failure keyed by this field.")
            },
            {
                "admin OriginalDiscLayoutDecisionRequest.replacesRevision",
                new(RevisionFieldClassification.Batch,
                    "Replacing a layout writes the selected layout under If-Match and independently replaces another " +
                    "layout under this observed revision. Both participants must still match; this is a batch binding.")
            },
            {
                "consumer ConsumerDiscSetAccessRequest.revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "The revision is the subject of the access question, and a stale revision answers allowed=false " +
                    "(200). It is not a lost-update guard.")
            },
            {
                "consumer ConsumerDiscSetLayoutVersionDto.layoutRevision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins each selected release of ConsumerDiscSetAccessRequest.layoutVersions to the observed layout.")
            },
            {
                "consumer ConsumerDiscSetSelectionRequest.revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins the selection manifest to the disc-set revision the client observed.")
            },
            {
                "admin GET /api/catalog-snapshot/systems ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this systems page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "admin GET /api/catalog-snapshot/companies ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this companies page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "admin GET /api/catalog-snapshot/regions ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this regions page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "admin GET /api/catalog-snapshot/languages ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this languages page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "admin GET /api/catalog-snapshot/rating-boards ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this rating-boards page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "admin GET /api/catalog-snapshot/ratings ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this ratings page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "consumer GET /api/catalog-snapshot/systems ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this systems page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "consumer GET /api/catalog-snapshot/companies ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this companies page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "consumer GET /api/catalog-snapshot/regions ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this regions page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "consumer GET /api/catalog-snapshot/languages ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this languages page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "consumer GET /api/catalog-snapshot/rating-boards ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this rating-boards page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "consumer GET /api/catalog-snapshot/ratings ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins this ratings page to the complete catalogRevision advertised by the snapshot descriptor. " +
                    "The page is selected from one stored publication, not a mutable resource being written; " +
                    "a stale current-only catalog pin returns 409 ReferenceCatalog.SnapshotChanged.")
            },
            {
                "consumer POST /api/disc-sets/{discSetId}/manifest ?revision",
                new(RevisionFieldClassification.SnapshotBinding,
                    "Pins manifest issuance to the observed published disc-set revision.")
            },
        };

    /// <summary>
    ///     Permanent R9 entries, keyed <c>"host METHOD /route"</c> to their rationale: operations whose unresolved
    ///     path identity is reviewed as not an error. An entry is stale once its route documents 404.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ReviewedPathParameterNotFoundExemptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            {
                "consumer GET /api/releases/{releaseId}/access",
                "A release outside the caller's live Library projection is an authoritative revoke, not an error: " +
                "the repository answers Found(Revoke) and the operation returns 200 allowed=false " +
                "(docs/decisions/consumer-runtime-boundary.md, \"Backend Single-Release Access Boundary\"). " +
                "A malformed id is 400; an unavailable Library is 409."
            },
            {
                "consumer POST /api/disc-sets/{discSetId}/access/preview",
                "A check that answers for any id: an unknown or unpublished disc set, or a stale revision or " +
                "selection, returns 200 allowed=false. A 409 comes from an unavailable Library, a malformed request, " +
                "a server-instance mismatch or discs that are not ready."
            },
            {
                "consumer DELETE /api/me/activity/play-sessions/{sessionId}",
                "An idempotent delete scoped to the caller: deleting a session that does not exist succeeds with 204."
            },
        };

    public static readonly IReadOnlyDictionary<string, OwningSlice> R1 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R2 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R3 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R4 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R5 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R6 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R7 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R8 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R9 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R10 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R11 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R12 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static readonly IReadOnlyDictionary<string, OwningSlice> R13 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    /// <summary>
    ///     R14 started from the violations of the day it was added, as the policy's Ratchets bullet allows a new
    ///     rule to.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, OwningSlice> R14 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    /// <summary>
    ///     R15 started from the violations of the day it was added, as the policy's Ratchets bullet allows a new
    ///     rule to.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, OwningSlice> R15 =
        new Dictionary<string, OwningSlice>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> AllowedViolations(string ruleId) => ruleId switch
    {
        "R1" => HeldBy((nameof(R1), R1.Keys)),
        "R2" => HeldBy((nameof(R2), R2.Keys)),
        "R3" => HeldBy((nameof(R3), R3.Keys)),
        "R4" => HeldBy((nameof(R4), R4.Keys)),
        "R5" => HeldBy((nameof(R5), R5.Keys)),
        "R6" => HeldBy((nameof(R6), R6.Keys)),
        "R7" => HeldBy((nameof(R7), R7.Keys)),
        "R8" => HeldBy((nameof(R8), R8.Keys)),
        "R9" => HeldBy(
            (nameof(R9), R9.Keys),
            (nameof(ReviewedPathParameterNotFoundExemptions), ReviewedPathParameterNotFoundExemptions.Keys)),
        "R10" => HeldBy((nameof(R10), R10.Keys)),
        "R11" => HeldBy(
            (nameof(R11), R11.Keys),
            (nameof(ReviewedRequestRevisionFields), ReviewedRequestRevisionFields.Keys)),
        "R12" => HeldBy((nameof(R12), R12.Keys)),
        "R13" => HeldBy((nameof(R13), R13.Keys)),
        "R14" => HeldBy((nameof(R14), R14.Keys)),
        "R15" => HeldBy((nameof(R15), R15.Keys)),
        _ => throw new ArgumentOutOfRangeException(nameof(ruleId), ruleId, "Unknown API contract policy rule.")
    };

    private static IReadOnlyDictionary<string, string> HeldBy(params (string Dictionary, IEnumerable<string> Keys)[] sources) =>
        sources
            .SelectMany(source => source.Keys.Select(key =>
                (Key: key, Holder: $"{nameof(ApiContractPolicyAllowlists)}.{source.Dictionary}")))
            .ToDictionary(entry => entry.Key, entry => entry.Holder, StringComparer.Ordinal);
}
