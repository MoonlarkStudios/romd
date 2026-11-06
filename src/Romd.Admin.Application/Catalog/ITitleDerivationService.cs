using ErrorOr;

namespace Romd.Admin.Application.Catalog;

/// <summary>
///     The single door through which any source turns claims into canonical titles and
///     entry-to-title links. Providers push claims; the catalog owns source-entry identity,
///     title match-or-create, and link writes. See docs/decisions/neutral-source-identity.md.
///     Contract:
///     <list type="bullet">
///         <item>Exactly one assignment is yielded per claim, in claim order.</item>
///         <item>
///             The service flushes its writes before yielding a batch's assignments and clears
///             the change tracker only at batch boundaries; callers must not hold staged,
///             unflushed entities across a pull.
///         </item>
///         <item>
///             Duplicate entry keys are canonicalized stream-wide: the first claim for a key
///             in the invocation carries the entry's facts; every later duplicate — same
///             batch or not — projects its result and changes nothing.
///         </item>
///         <item>
///             A claim whose platform disagrees with the entry's established platform is
///             answered with a validation error: claims cannot move an entry's platform.
///         </item>
///         <item>
///             Derivation never overwrites an existing link — curation is durable. A claim
///             whose entry is already linked reports the existing title and skips derivation.
///         </item>
///         <item>
///             BIOS claims and claims without a platform get entry identity, never a link.
///             BIOS classification outranks curation: reclassifying an entry as BIOS removes
///             its existing link.
///         </item>
///         <item>
///             Claims stamp a null entry platform and refresh the entry's display name;
///             established platforms never move.
///         </item>
///     </list>
/// </summary>
public interface ITitleDerivationService
{
    /// <summary>
    ///     Full-snapshot reconcile: upserts claimed entries, derives and links titles, and on
    ///     successful completion of the stream deletes this source's entries that were absent
    ///     from it (purely stamp-based; links cascade). Providers whose payload rows reference
    ///     entries must retire those payloads before reconciling — or use
    ///     <see cref="UpsertAsync" /> and prune provider-side, as DAT does via its
    ///     version-retention grace policy.
    /// </summary>
    IAsyncEnumerable<ErrorOr<ClaimAssignment>> ReconcileAsync(
        int catalogSourceId,
        IAsyncEnumerable<TitleClaim> claims,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Incremental derivation: upserts claimed entries and links without deleting unseen
    ///     entries. DAT ingest, routing, and partial imports use this.
    /// </summary>
    IAsyncEnumerable<ErrorOr<ClaimAssignment>> UpsertAsync(
        int catalogSourceId,
        IAsyncEnumerable<TitleClaim> claims,
        CancellationToken cancellationToken = default);
}

/// <summary>One provider claim: an entry key, a display name, and derivation facts.</summary>
public sealed record TitleClaim(string EntryKey, string Name, int? PlatformId, bool IsBios);

/// <summary>
///     The catalog's answer to one claim: the entry's identity and the title it is linked to
///     after the call (null for BIOS or unrouted claims). <see cref="TitleWasCreated" /> is
///     true only on the single claim that created the title, so summing it counts distinct
///     new titles.
/// </summary>
public sealed record ClaimAssignment(
    string EntryKey,
    int SourceEntryId,
    int? TitleId,
    bool TitleWasCreated);
