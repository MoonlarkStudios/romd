using ErrorOr;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Enrichment;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Search;
using Romd.Application.Common.Ids;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
namespace Romd.Persistence.Artwork;

public sealed partial class ArtworkCompletionStore(RomdDbContext db, TimeProvider clock, IReferenceCatalogService? referenceCatalog = null,
    Microsoft.Extensions.Options.IOptions<Romd.Admin.Application.Configuration.EnrichmentOptions>? enrichmentOptions = null,
    IEnumerable<Romd.Admin.Application.MetadataProviders.IProviderIdentityAdapter>? identityAdapters = null) : IArtworkCompletionStore, IArtworkCompletionAcceptance
{
    private static readonly string[] Active = ["Pending", "Enriching"];
    public Task<ErrorOr<ArtworkCompletionPreviewDto>> PreviewAsync(IReadOnlyList<int> ids, CancellationToken ct) =>
        PreviewScopeAsync(new(Guid.NewGuid(), ids.Select(IdCoder.Encode).ToArray()), ct);

    private async Task<ErrorOr<IQueryable<TitleEntity>>> ScopeAsync(ArtworkCompletionRequest request, CancellationToken ct, EnrichmentProviderAvailability? availability = null)
    {
        if (request.Scope == "Automatic")
        {
            if (request.Mode != "Enrich")
                return ArtworkCurationErrors.InvalidRequest();
            availability ??= await EnrichmentProviderAvailability.ReadAsync(identityAdapters ?? [], ct);
            IQueryable<TitleEntity> titles = db.Titles;
            if (request.Filters is not null)
            {
                var filtered = await ScopeAsync(request with { Scope = "Filtered" }, ct, availability);
                if (filtered.IsError)
                    return filtered.Errors;
                titles = filtered.Value;
            }
            var eligibleIds = new Romd.Persistence.Enrichment.EnrichmentAssessmentQuery(db, titles, enrichmentOptions?.Value, availability).AutomaticTitleIds();
            return ErrorOrFactory.From(titles.Where(x => eligibleIds.Contains(x.Id)));
        }
        if (request.Scope == "AllTracked")
            return ErrorOrFactory.From(db.Titles.Where(x => db.TrackedTitles.Any(t => t.TitleId == x.Id)));
        if (request.Scope == "Selected")
        {
            if (request.TitleIds is null || request.TitleIds.Count == 0 || request.TitleIds.Count > 10000)
                return ArtworkCurationErrors.InvalidRequest();
            var ids = new List<int>();
            foreach (var value in request.TitleIds)
            { if (!Sqid.TryParse(value, null, out var id)) return ArtworkCurationErrors.InvalidRequest(); ids.Add(id.Value); }
            var query = db.Titles.Where(x => ids.Contains(x.Id));
            if (await query.CountAsync(ct) != ids.Distinct().Count())
                return ArtworkCurationErrors.TitleNotFound();
            return ErrorOrFactory.From(query);
        }
        if (request.Scope == "Retry" && request.RetryJobId is { } prior && request.RetryStatus is { } status and ("Failed" or "Unfinished"))
        {
            if (!await db.Set<ArtworkCompletionJobEntity>().AnyAsync(x => x.Id == prior, ct))
                return ArtworkCurationErrors.TitleNotFound();
            var ids = await Projection().MatchingTitleIdsAsync(prior, status, ct);
            return ErrorOrFactory.From(db.Titles.Where(x => ids.Contains(x.Id)));
        }
        if (request.Scope != "Filtered" || request.Filters is not { } f ||
            !Enum.TryParse<TrackedFilter>(f.Tracked, true, out var tracked) || !Enum.IsDefined(tracked) ||
            !Enum.TryParse<ReleaseCompletenessFilter>(f.ReleaseCompleteness, true, out var completeness) || !Enum.IsDefined(completeness) ||
            f.Query?.Length > 200)
            return ArtworkCurationErrors.InvalidRequest();
        int? platformId = null;
        if (f.SystemKey is not null)
        {
            platformId = referenceCatalog is null ? null : await referenceCatalog.ResolveSystemIdAsync(f.SystemKey, ct);
            if (platformId is null)
                return ArtworkCurationErrors.InvalidRequest();
        }
        EnrichmentFlagFilter[]? enrichment = null;
        if (f.Enrichment is not null)
        {
            var parsed = EnrichmentFilterParser.ParseFlags(f.Enrichment);
            if (parsed.IsError)
                return ArtworkCurationErrors.InvalidRequest();
            enrichment = parsed.Value;
        }
        availability ??= await EnrichmentProviderAvailability.ReadAsync(identityAdapters ?? [], ct);
        return ErrorOrFactory.From(new SearchRepository(db, new ArtworkReader(db), enrichmentOptions, identityAdapters)
            .ScopeTitles(f.Query, new(platformId, f.Genre, completeness, f.EnrichmentStatus, tracked, f.ArtworkStatus, enrichment), availability));
    }
    public async Task<ErrorOr<ArtworkCompletionPreviewDto>> PreviewScopeAsync(ArtworkCompletionRequest request, CancellationToken ct)
    {
        var scope = await ScopeAsync(request, ct);
        if (scope.IsError)
            return scope.Errors;
        // Aggregate all roles over the same scope so automatic eligibility is evaluated once.
        var counts = await scope.Value.GroupBy(_ => 1).Select(group => new
        {
            Total = group.Count(),
            Poster = group.Count(t => !db.TitleMedia.Any(m => m.TitleId == t.Id && m.Type == "Cover") &&
                !db.ArtworkAssets.Any(x => x.TitleId == t.Id && x.Role == ArtworkRole.Poster && x.IsEligible && !x.IsRejected) &&
                !db.ArtworkSelections.Any(x => x.TitleId == t.Id && x.Role == ArtworkRole.Poster &&
                    (x.Mode != ArtworkSelectionMode.Automatic || x.PendingRequestId != null))),
            Hero = group.Count(t =>
                !db.ArtworkAssets.Any(x => x.TitleId == t.Id && x.Role == ArtworkRole.Hero && x.IsEligible && !x.IsRejected) &&
                !db.ArtworkSelections.Any(x => x.TitleId == t.Id && x.Role == ArtworkRole.Hero &&
                    (x.Mode != ArtworkSelectionMode.Automatic || x.PendingRequestId != null))),
            Logo = group.Count(t =>
                !db.ArtworkAssets.Any(x => x.TitleId == t.Id && x.Role == ArtworkRole.Logo && x.IsEligible && !x.IsRejected) &&
                !db.ArtworkSelections.Any(x => x.TitleId == t.Id && x.Role == ArtworkRole.Logo &&
                    (x.Mode != ArtworkSelectionMode.Automatic || x.PendingRequestId != null))),
            Backdrop = group.Count(t =>
                !db.ArtworkAssets.Any(x => x.TitleId == t.Id && x.Role == ArtworkRole.Backdrop && x.IsEligible && !x.IsRejected) &&
                !db.ArtworkSelections.Any(x => x.TitleId == t.Id && x.Role == ArtworkRole.Backdrop &&
                    (x.Mode != ArtworkSelectionMode.Automatic || x.PendingRequestId != null)))
        }).SingleOrDefaultAsync(ct);
        return new ArtworkCompletionPreviewDto(counts?.Total ?? 0,
            [new("Poster", "Missing", counts?.Poster ?? 0), new("Hero", "Missing", counts?.Hero ?? 0),
                new("Logo", "Missing", counts?.Logo ?? 0), new("Backdrop", "Missing", counts?.Backdrop ?? 0)]);
    }
    public async Task<ErrorOr<ArtworkCompletionJobDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Set<ArtworkCompletionJobEntity>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is not null)
            return await MapAsync(row, ct);
        return ArtworkCurationErrors.TitleNotFound();
    }
    private async Task<ArtworkCompletionJobDto> MapAsync(ArtworkCompletionJobEntity row, CancellationToken ct) =>
        Map(row, (await Projection().SummariesAsync([row.Id], await ProvidersAsync(ct), ct)).GetValueOrDefault(row.Id));

    // Phase, Total and Processed are the job row's own execution history; the projection only supplies run counts.
    private static ArtworkCompletionJobDto Map(ArtworkCompletionJobEntity row, EnrichmentTaskProjection.Summary? summary) =>
        new(row.Id, row.Phase, row.Total, row.Processed, summary?.Roles ?? [], summary?.Review ?? 0, summary?.Failed ?? 0,
            summary?.Unfinished ?? 0, summary?.Working ?? 0, row.NextAttemptAtUtc, Mode(row), row.CreatedAt);
    private EnrichmentTaskProjection Projection() => new(db, enrichmentOptions?.Value);
    private Task<EnrichmentProviderAvailability> ProvidersAsync(CancellationToken ct) => EnrichmentProviderAvailability.ReadAsync(identityAdapters ?? [], ct);

    public async Task<IReadOnlyList<ArtworkCompletionJobDto>> RecentAsync(CancellationToken ct)
    {
        // Later continuations belong to the original task. Shared work points back to an older
        // independently requested task, which must remain visible in execution history.
        var rows = await db.Set<ArtworkCompletionJobEntity>().Where(x => !db.Set<ArtworkCompletionItemEntity>().Any(i => i.FollowUpJobId == x.Id &&
                db.Set<ArtworkCompletionJobEntity>().Any(parent => parent.Id == i.JobId && parent.CreatedAt < x.CreatedAt)))
            .OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(ct);
        if (rows.Count == 0)
            return [];
        var summaries = await Projection().SummariesAsync(rows.Select(x => x.Id).ToArray(), await ProvidersAsync(ct), ct);
        return rows.Select(row => Map(row, summaries.GetValueOrDefault(row.Id))).ToArray();
    }
    private static string Mode(ArtworkCompletionJobEntity row) => JsonSerializer.Deserialize<ArtworkCompletionRequest>(row.ScopeJson)?.Mode ?? "Artwork";
    public async Task<ErrorOr<ArtworkCompletionItemsDto>> ItemsAsync(Guid id, int after, string? status, CancellationToken ct, int limit = 50)
    {
        if (!await db.Set<ArtworkCompletionJobEntity>().AnyAsync(x => x.Id == id, ct))
            return ArtworkCurationErrors.TitleNotFound();
        var rows = await Projection().PageAsync(id, after, status, await ProvidersAsync(ct), ct, limit);
        return new ArtworkCompletionItemsDto(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].TitleId : null);
    }

    public async Task<ArtworkCompletionWork?> NextAsync(Guid id, CancellationToken ct)
    {
        var item = await db.Set<ArtworkCompletionItemEntity>().Where(x => x.JobId == id && x.CompletedAt == null).OrderBy(x => x.TitleId).FirstOrDefaultAsync(ct);
        if (item is null)
            return null;
        var job = await db.Set<ArtworkCompletionJobEntity>().SingleAsync(x => x.Id == id, ct);
        return new(item.TitleId, item.TitleName, item.RequireTracked, Mode(job));
    }
    public async Task<IReadOnlyDictionary<ArtworkRole, ArtworkCompletionOutcome>> OutcomesAsync(Guid jobId, int titleId, CancellationToken ct) =>
        await db.Set<ArtworkCompletionResultEntity>().Where(x => x.JobId == jobId && x.TitleId == titleId).ToDictionaryAsync(x => x.Role, x => x.Outcome, ct);
    public Task<int> CompletedCountAsync(Guid id, CancellationToken ct) => db.Set<ArtworkCompletionItemEntity>().CountAsync(x => x.JobId == id && x.CompletedAt != null, ct);
    public Task<bool> IsTrackedAsync(int titleId, CancellationToken ct) => db.TrackedTitles.AnyAsync(x => x.TitleId == titleId, ct);
    public Task CompleteAsync(Guid id, int titleId, IReadOnlyDictionary<ArtworkRole, ArtworkCompletionOutcome> outcomes,
        MetadataExecutionOutcome metadata, JobContext context, CancellationToken ct) =>
        context.ExecuteOwnedMutationAsync(async token =>
        {
            var row = await db.Set<ArtworkCompletionItemEntity>().AsTracking().SingleAsync(x => x.JobId == id && x.TitleId == titleId, token);
            if (row.CompletedAt != null)
                return;
            // Applied before the tracked completion; that save changes only CompletedAt and result rows.
            await RaiseMetadataOutcomeAsync(id, titleId, metadata, token);
            var recorded = await db.Set<ArtworkCompletionResultEntity>().AsTracking().Where(x => x.JobId == id && x.TitleId == titleId).ToListAsync(token);
            foreach (var (role, outcome) in outcomes)
            {
                var result = recorded.SingleOrDefault(x => x.Role == role);
                if (result is null)
                    db.Add(new ArtworkCompletionResultEntity { JobId = id, TitleId = titleId, Role = role, Outcome = outcome });
                else if (result.Outcome != ArtworkCompletionOutcome.Updated)
                    result.Outcome = outcome;
            }
            row.CompletedAt = DateTimeOffset.UtcNow;
        }, ct);
    public Task RecordMetadataOutcomeAsync(Guid jobId, int titleId, MetadataExecutionOutcome outcome, CancellationToken ct) =>
        db.Database.CurrentTransaction is null
            ? throw new InvalidOperationException("An owned transaction is required.")
            : RaiseMetadataOutcomeAsync(jobId, titleId, outcome, ct);
    // Resumed attempts only raise the record (NotAttempted < Failed < Succeeded) and finished items are immutable.
    private Task<int> RaiseMetadataOutcomeAsync(Guid jobId, int titleId, MetadataExecutionOutcome outcome, CancellationToken ct)
    {
        var items = db.Set<ArtworkCompletionItemEntity>().Where(x => x.JobId == jobId && x.TitleId == titleId && x.CompletedAt == null);
        items = outcome switch
        {
            MetadataExecutionOutcome.Succeeded => items.Where(x => x.MetadataOutcome == null || x.MetadataOutcome != MetadataExecutionOutcome.Succeeded),
            MetadataExecutionOutcome.Failed => items.Where(x => x.MetadataOutcome == null || x.MetadataOutcome == MetadataExecutionOutcome.NotAttempted),
            _ => items.Where(x => x.MetadataOutcome == null)
        };
        return items.ExecuteUpdateAsync(s => s.SetProperty(x => x.MetadataOutcome, outcome), ct);
    }
    // A failed role or a failed metadata step is an execution error of this run.
    public async Task<bool> HasFailuresAsync(Guid jobId, CancellationToken ct) =>
        await db.Set<ArtworkCompletionResultEntity>().AnyAsync(x => x.JobId == jobId && x.Outcome == ArtworkCompletionOutcome.Failed, ct) ||
        await db.Set<ArtworkCompletionItemEntity>().AnyAsync(x => x.JobId == jobId && x.MetadataOutcome == MetadataExecutionOutcome.Failed, ct);
    public async Task<Guid?> ResumeTitleStagedAsync(int titleId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Resume acceptance requires the identity transaction.");
        await JobAcceptanceLock.AcquireAsync(db, 148, 1, ct);
        var candidates = await db.Set<ArtworkCompletionItemEntity>().AsTracking().Where(x => x.TitleId == titleId && x.FollowUpJobId == null && x.CompletedAt != null).ToListAsync(ct);
        var parentIds = candidates.Select(x => x.JobId).ToArray();
        var parents = await db.Set<ArtworkCompletionJobEntity>().Where(x => parentIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var needsMatch = await db.Set<ArtworkCompletionResultEntity>().Where(x => x.TitleId == titleId && parentIds.Contains(x.JobId) && x.Outcome == ArtworkCompletionOutcome.NeedsMatch).Select(x => x.JobId).ToListAsync(ct);
        var awaiting = candidates.Where(x => Mode(parents[x.JobId]) == "Enrich" || needsMatch.Contains(x.JobId)).ToList();
        var title = await db.Titles.SingleOrDefaultAsync(x => x.Id == titleId, ct);
        if (title is null)
            return null;
        // Identity confirmation always enables full enrichment, whether or not a historical run exists.
        // A Pending job has not read provider inputs yet. Enriching work may have, so it must never
        // suppress the new follow-up. Publication remains protected by title revisions and job fencing.
        var pending = await (from item in db.Set<ArtworkCompletionItemEntity>()
                             join job in db.Set<ArtworkCompletionJobEntity>() on item.JobId equals job.Id
                             where item.TitleId == titleId && item.CompletedAt == null && item.FollowUpJobId == null && job.Phase == "Pending"
                             orderby job.CreatedAt, job.Id
                             select new { Item = item, Job = job }).ToListAsync(ct);
        var requireTracked = awaiting.Count > 0 && awaiting.All(x => x.RequireTracked);
        var active = pending.FirstOrDefault(x => Mode(x.Job) == "Enrich" && (requireTracked || !x.Item.RequireTracked));
        var id = active?.Job.Id ?? Guid.NewGuid();
        if (active is null)
        {
            var originalActor = awaiting.Count > 0 ? parents[awaiting[0].JobId].CreatedByUserId : null;
            var continuation = ArtworkCompletionJobEntity.FromDomain(ArtworkCompletionJob.Create(id, 1, originalActor, createdAt: clock.GetUtcNow(), "Enrich"));
            continuation.ScopeJson = JsonSerializer.Serialize(new ArtworkCompletionRequest(Guid.Empty, [IdCoder.Encode(titleId)], Mode: "Enrich"));
            db.Add(continuation);
            db.Add(new ArtworkCompletionItemEntity { JobId = id, TitleId = titleId, TitleName = title.Name, RequireTracked = requireTracked });
        }
        foreach (var row in awaiting)
            row.FollowUpJobId = id;
        return id;
    }
}
