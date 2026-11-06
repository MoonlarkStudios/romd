using ErrorOr;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Enrichment;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Contracts.Common.Models;
using Romd.Application.Common.Artwork;
using Romd.Contracts.Management.Enrichment;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Search;
namespace Romd.Persistence.Enrichment;

public sealed class EnrichmentWorkbench(RomdDbContext db, TimeProvider clock, IArtworkReader artworkReader, IOptions<EnrichmentOptions>? options = null, IEnumerable<Romd.Admin.Application.MetadataProviders.IProviderIdentityAdapter>? adapters = null) : IEnrichmentWorkbench
{
    public async Task<ErrorOr<Page<EnrichmentAssessmentDto>>> ReadAsync(string view, string? cursor, int limit, string? query, CancellationToken ct)
    {
        if (view is not ("NeedsReview" or "Automatic" or "Processing" or "Blocked" or "All") || limit is < 1 or > IEnrichmentWorkbench.MaxLimit || query?.Length > 200)
            return Error.Validation("Enrichment.InvalidQuery", $"Choose a supported view, a limit between 1 and {IEnrichmentWorkbench.MaxLimit}, and a search of at most 200 characters.");
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var fingerprint = KeysetCursor.Fingerprint("enrichment-workbench", "manager-tracked-titles", view, query, "id", "asc");
        var position = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, value => value.IsValid);
        if (position.IsError)
            return position.Errors;
        var titles = TitleScope(query);
        var providers = await EnrichmentProviderAvailability.ReadAsync(adapters ?? [], ct);
        var page = await ReadIdsAsync(titles, providers, view, position.Value?.Id ?? 0, limit, ct);
        var hasMore = page.Count > limit;
        var ids = page.Take(limit).ToArray();
        var details = await DetailsAsync(ids, ct, providers);
        return new Page<EnrichmentAssessmentDto>
        {
            Items = details,
            HasNextPage = hasMore,
            NextCursor = hasMore ? KeysetCursor.Encode(new IntegerPosition(ids[^1]), fingerprint) : null
        };
    }

    public async Task<ErrorOr<EnrichmentWorkbenchCountsDto>> ReadCountsAsync(string? query, CancellationToken ct)
    {
        if (query?.Length > 200)
            return Error.Validation("Enrichment.InvalidQuery", "Use a search of at most 200 characters.");
        var providers = await EnrichmentProviderAvailability.ReadAsync(adapters ?? [], ct);
        var summaries = new EnrichmentAssessmentQuery(db, TitleScope(query), options?.Value, providers).SummariesForCounts();
        // One statement counts the canonical flags without hydrating queue items or details.
        var counts = await summaries.GroupBy(_ => 1).Select(group => new EnrichmentWorkbenchCountsDto(
            group.Count(), group.Count(x => x.NeedsReview), group.Count(x => x.CanEnrichAutomatically),
            group.Count(x => x.HasActiveWork), group.Count(x => x.HasOperationalBlocker))).SingleOrDefaultAsync(ct);
        return counts ?? new EnrichmentWorkbenchCountsDto(0, 0, 0, 0, 0);
    }

    // The catalog's full-text predicate, so a launch from this search selects exactly the titles counted here.
    private IQueryable<TitleEntity> TitleScope(string? query)
    {
        var titles = db.Titles.Where(x => db.TrackedTitles.Any(tracked => tracked.TitleId == x.Id));
        return string.IsNullOrWhiteSpace(query) ? titles : SearchQuery.MatchTitles(titles, query);
    }

    private async Task<List<int>> ReadIdsAsync(IQueryable<TitleEntity> titles,
        EnrichmentProviderAvailability providers, string view, int after, int limit, CancellationToken ct)
    {
        if (view == "All")
            return await titles.Where(x => x.Id > after).OrderBy(x => x.Id)
                .Select(x => x.Id).Take(limit + 1).ToListAsync(ct);

        // Assess bounded candidate batches, not the entire remaining catalog. Sparse queues may
        // need several batches; never take matching rows until all preceding candidates were checked.
        var batchSize = 256;
        var ids = new List<int>(limit + 1);
        while (ids.Count <= limit)
        {
            var candidates = await titles.Where(x => x.Id > after).OrderBy(x => x.Id)
                .Select(x => x.Id).Take(batchSize).ToArrayAsync(ct);
            if (candidates.Length == 0)
                break;
            var summaries = new EnrichmentAssessmentQuery(db, titles.Where(x => candidates.Contains(x.Id)), options?.Value, providers).Summaries();
            var matching = view switch
            {
                "NeedsReview" => summaries.Where(x => x.NeedsReview),
                "Automatic" => summaries.Where(x => x.CanEnrichAutomatically),
                "Processing" => summaries.Where(x => x.HasActiveWork),
                "Blocked" => summaries.Where(x => x.HasOperationalBlocker),
                _ => summaries
            };
            ids.AddRange(await matching.OrderBy(x => x.TitleId).Select(x => x.TitleId)
                .Take(limit + 1 - ids.Count).ToListAsync(ct));
            if (candidates.Length < batchSize)
                break;
            after = candidates[^1];
            // Grow only after exhausting a candidate batch so sparse queues do not incur
            // one database round trip per small batch across the whole catalog.
            batchSize = Math.Min(batchSize * 2, 2048);
        }
        return ids;
    }

    public async Task<ErrorOr<EnrichmentAssessmentDto>> GetAsync(int titleId, CancellationToken ct)
    {
        var rows = await DetailsAsync([titleId], ct);
        return rows.Count == 0 ? CatalogErrors.TitleNotFound() : rows[0];
    }

    public async Task<ErrorOr<Updated>> SetOmissionAsync(int titleId, string field, bool accepted, RevisionPrecondition ifMatch, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Omission writes require a caller-owned transaction.");
        if (!EnrichmentAssessmentPolicy.CoreMetadataFields.Contains(field))
            return Error.Validation("Enrichment.InvalidField", "Only Description, Genre and ReleaseDate can be accepted as absent.");
        var current = await db.Titles.Where(x => x.Id == titleId).Select(x => (Guid?)x.Revision).SingleOrDefaultAsync(ct);
        if (current is null)
            return CatalogErrors.TitleNotFound();
        // Compare-and-rotate: a writer that rotated the revision after the read leaves no row to update.
        if (!ifMatch.IsSatisfiedBy(current.Value) || await db.Titles.Where(x => x.Id == titleId && x.Revision == current)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, Guid.NewGuid()), ct) != 1)
            return CatalogErrors.TitlePreconditionFailed();
        var title = await db.Titles.AsTracking().SingleOrDefaultAsync(x => x.Id == titleId, ct);
        if (title is null)
            return CatalogErrors.TitleNotFound();
        var populated = field switch { "Description" => !string.IsNullOrEmpty(title.Description), "Genre" => !string.IsNullOrEmpty(title.Genre), _ => title.ReleaseDate is not null };
        if (accepted && populated)
            return Error.Conflict("Enrichment.FieldPresent", "This field now has a value. Refresh to review the current title.");
        var existing = await db.Set<EnrichmentOmissionEntity>().AsTracking().SingleOrDefaultAsync(x => x.TitleId == titleId && x.Field == field, ct);
        if (accepted && existing is null)
            db.Add(new EnrichmentOmissionEntity { TitleId = titleId, Field = field, AcceptedAt = clock.GetUtcNow() });
        if (!accepted && existing is not null)
            db.Remove(existing);
        return Result.Updated;
    }

    private async Task<IReadOnlyList<EnrichmentAssessmentDto>> DetailsAsync(int[] ids, CancellationToken ct, EnrichmentProviderAvailability? providers = null)
    {
        if (ids.Length == 0)
            return [];
        var rows = await (from title in db.Titles
                          where ids.Contains(title.Id)
                          join platform in db.Platforms on title.PlatformId equals platform.Id
                          orderby title.Id
                          select new
                          {
                              title.Id,
                              title.Name,
                              title.Revision,
                              Platform = platform.Name,
                              CompactLabel = platform.CompactLabelOverride ?? platform.BaseCompactLabel ?? platform.ShortName
                          }).ToListAsync(ct);
        // Resolve only this bounded page through the same curation rules as the catalog.
        // Counts never load artwork, and clients do not need one detail request per row.
        var artwork = await artworkReader.ResolveAsync(rows.Select(title => title.Id).ToArray(), ct);
        providers ??= await EnrichmentProviderAvailability.ReadAsync(adapters ?? [], ct);
        var requirements = await new EnrichmentAssessmentQuery(db, db.Titles.Where(x => ids.Contains(x.Id)), options?.Value, providers).Requirements().ToListAsync(ct);
        var omissions = await db.Set<EnrichmentOmissionEntity>().Where(x => ids.Contains(x.TitleId)).ToListAsync(ct);
        return rows.Select(title =>
        {
            var current = requirements.Where(x => x.TitleId == title.Id).ToArray();
            var accepted = omissions.Where(x => x.TitleId == title.Id).Select(x => x.Field).Order().ToArray();
            var poster = artwork.GetValueOrDefault(title.Id)?.FirstOrDefault(item => item.Role == ArtworkRole.Poster)?.ToContract();
            var thumbnail = poster?.Variants.OrderBy(item => (long)item.Width * item.Height).ThenBy(item => item.Name, StringComparer.Ordinal).FirstOrDefault()?.Url ?? poster?.Url;
            return new EnrichmentAssessmentDto(IdCoder.Encode(title.Id), title.Name, title.Platform, title.Revision,
                current.Any(x => x.State == EnrichmentRequirementState.NeedsReview), current.Any(x => x.State == EnrichmentRequirementState.Automatic),
                current.Any(x => x.State == EnrichmentRequirementState.Processing), current.Any(x => x.State == EnrichmentRequirementState.Blocked),
                EnrichmentRequirementMapping.ToContract(current), accepted, thumbnail, title.CompactLabel);
        }).ToArray();
    }
}
