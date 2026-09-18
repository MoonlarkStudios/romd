using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Libraries;
using Romd.Persistence;

namespace Romd.Persistence.Repositories;

/// <summary>Read-only comparison of two policies over one PostgreSQL snapshot.</summary>
public sealed class LibraryDraftEvaluator(
    RomdDbContext context,
    ILibraryRepository libraries,
    ILibraryCandidateReader dataProvider,
    ICatalogProjectionService catalog) : ILibraryDraftEvaluator
{
    public async Task<ErrorOr<LibraryEvaluationDto>> EvaluateAsync(int libraryId,
        LibraryConfiguration configuration, string view, string? search, int afterId, CancellationToken ct)
    {
        if (LibraryConfigurationValidator.Validate(configuration) is not null ||
            view is not ("matching" or "added" or "removed" or "excluded" or "all") ||
            search?.Length > 200 || afterId < 0)
            return LibraryErrors.InvalidConfiguration();

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var library = await libraries.GetByIdAsync(libraryId, ct);
        if (library is null) return LibraryErrors.NotFound();
        if (!library.HasValidConfiguration)
            return Error.Conflict("Libraries.InvalidBaseline", "Repair the saved library rules before comparing changes.");
        if (await libraries.ValidateConfigurationReferencesAsync(configuration, ct) is not null)
            return LibraryErrors.InvalidConfiguration();
        var dirtyPlatforms = await catalog.GetPlatformIdsNeedingRebuildAsync(ct);
        // Exclusion inspection spans the whole catalog, so do not serve partial explanations.
        if (dirtyPlatforms.Count > 0)
            return Error.Conflict("Libraries.CatalogUpdating", "The catalog is updating. Try evaluating again when it finishes.");

        var evaluatedAt = DateTimeOffset.UtcNow;
        var loaded = await dataProvider.GetCandidatesAsync(new LibraryConfiguration(), ct);
        var candidatesById = loaded.ToDictionary(t => t.TitleId);
        var metadata = await context.Titles.Select(t => new {
            t.Id, t.Name, t.PlatformId, t.Genre,
            PlatformName = context.Platforms.Where(p => p.Id == t.PlatformId).Select(p => p.Name).First(),
            Cover = t.Media.Where(m => m.Type == "Cover" && m.IsPrimary).Select(m => (int?)m.Id).FirstOrDefault(),
            Collections = context.CollectionItems.Count(i => i.TitleId == t.Id &&
                context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == i.CollectionId))
        }).ToListAsync(ct);
        var missingIds = metadata.Where(t => !candidatesById.ContainsKey(t.Id)).Select(t => t.Id).ToHashSet();
        var missingRatings = (await context.TitleContentRatings.Where(r => missingIds.Contains(r.TitleId)).ToListAsync(ct))
            .ToLookup(r => r.TitleId);
        foreach (var title in metadata.Where(t => missingIds.Contains(t.Id)))
            candidatesById[title.Id] = new TitleCandidates(title.Id, title.PlatformId, title.Genre,
                missingRatings[title.Id].Select(r => r.ToDomain()).ToList(), []);

        var candidates = candidatesById.Values.ToList();
        var saved = MaterializedLibraryProjectionBuilder.Build(libraryId, candidates, library.Configuration)
            .Titles.ToDictionary(t => t.TitleId);
        var draft = MaterializedLibraryProjectionBuilder.Build(libraryId, candidates, configuration)
            .Titles.ToDictionary(t => t.TitleId);
        var rows = metadata.Select(title => {
            var candidate = candidatesById[title.Id];
            var projected = draft.GetValueOrDefault(title.Id);
            var basis = ContentRatingPolicyEvaluator.Evaluate(configuration.ContentRatingPolicy, candidate.ContentRatings).Basis;
            return (Id: title.Id, Item: new LibraryEvaluationTitleDto(IdCoder.Encode(title.Id), title.Name,
                title.PlatformName, title.Cover is int cover ? $"/media/{IdCoder.Encode(cover)}" : null,
                saved.ContainsKey(title.Id), projected is not null, projected?.IsOwned ?? false,
                projected?.IsPlayable ?? false,
                projected is not null ? null : MaterializedLibraryProjectionBuilder.GetExclusionReason(candidate, configuration),
                basis?.Board.ToString(), basis?.Code, basis?.MinimumAge, title.Collections));
        }).ToList();
        var removed = rows.Where(r => r.Item.WasEligible && !r.Item.IsEligible).ToList();
        var filtered = rows.Where(r => view switch {
            "matching" => r.Item.IsEligible,
            "added" => r.Item.IsEligible && !r.Item.WasEligible,
            "removed" => r.Item.WasEligible && !r.Item.IsEligible,
            "excluded" => !r.Item.IsEligible,
            _ => true
        }).Where(r => string.IsNullOrWhiteSpace(search) || r.Item.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(r => r.Id > afterId).OrderBy(r => r.Id).Take(49).ToList();
        return new LibraryEvaluationDto(evaluatedAt, saved.Count, draft.Count,
            rows.Count(r => r.Item.IsEligible && !r.Item.WasEligible), removed.Count,
            removed.GroupBy(r => r.Item.Reason ?? "NoEligibleRelease")
                .Select(g => new LibraryEvaluationReasonDto(g.Key, g.Count())).OrderByDescending(g => g.Count).ToList(),
            filtered.Take(48).Select(r => r.Item).ToList(),
            filtered.Count > 48 ? IdCoder.Encode(filtered[47].Id) : null);
    }
}
