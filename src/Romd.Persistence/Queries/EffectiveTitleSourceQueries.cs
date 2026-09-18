using Romd.Domain.Catalog;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Queries;

/// <summary>
///     The one place <c>Status == Active</c> is encoded (design rule 2,
///     docs/decisions/neutral-source-identity.md): effective reads — ownership, search,
///     export, enrichment evidence, materialization, catalog matching, projection mapping —
///     compose these instead of the raw sets, so disabling a source hides its references
///     everywhere at once and re-enabling restores them with no re-derivation.
///     Truth-level reads stay on the raw sets by design: curation commands, the assignment
///     store, provenance listings, and orphan checks (a title backed only by non-Active
///     sources is dormant, never orphaned).
/// </summary>
public static class EffectiveTitleSourceQueries
{
    /// <summary>Entries whose catalog source is Active.</summary>
    public static IQueryable<SourceEntryEntity> EffectiveSourceEntries(this RomdDbContext context) =>
        context.SourceEntries.Where(e => context.CatalogSources.Any(
            c => c.Id == e.CatalogSourceId && c.Status == nameof(CatalogSourceStatus.Active)));

    /// <summary>
    ///     Title links whose backing entry's catalog source is Active. Composes
    ///     <see cref="EffectiveSourceEntries" /> so Active has exactly one definition.
    /// </summary>
    public static IQueryable<TitleSourceLinkEntity> EffectiveTitleSourceLinks(this RomdDbContext context) =>
        context.TitleSourceLinks.Where(l =>
            context.EffectiveSourceEntries().Any(e => e.Id == l.SourceEntryId));
}
