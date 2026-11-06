using System.Text.Json;
using Romd.Contracts.Management.Libraries;
using Romd.Contracts.Common.Models;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Catalog;

public sealed partial class DiscSetService(RomdDbContext db) : IDiscSetService, IDiscSetProposalStore, IDiscSetDecisionStore
{
    public static DiscSetDefinition Decode(string json) => JsonSerializer.Deserialize<DiscSetDefinition>(json)!;

    public Task<List<DiscSetSourceClaim>> ClaimsAsync(int platformId, CancellationToken ct, int? sourceId = null) =>
        ClaimsQuery(db.EffectiveSourceEntries().Where(entry => entry.PlatformId == platformId
            && (sourceId == null || entry.CatalogSourceId == sourceId))).ToListAsync(ct);

    public async Task<IReadOnlyList<DiscSetSourceClaim>> ReadClaimsAsync(int platformId, CancellationToken ct) =>
        await ClaimsAsync(platformId, ct);

    // The catalog convergence worker owns the flush, proposal replacement and rollback.
    public async Task RefreshStagedAsync(int platformId, IReadOnlyList<DiscSetProposal> desired, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Disc proposals require the rebuild transaction.");
        var stored = await db.DiscSets.AsTracking().Where(x => x.PlatformId == platformId).ToListAsync(ct);
        stored.AddRange(db.ChangeTracker.Entries<DiscSetEntity>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.PlatformId == platformId)
            .Select(entry => entry.Entity));
        foreach (var item in desired)
        {
            var row = stored.SingleOrDefault(x => x.CatalogSourceId == item.Source && x.ProposalKey == item.Key);
            if (row is null)
            {
                row = new() { PlatformId = platformId, CatalogSourceId = item.Source, ProposalKey = item.Key };
                db.DiscSets.Add(row);
            }
            string json = JsonSerializer.Serialize(item.Value);
            if (row.ProposalJson == json)
                continue;
            row.ProposalJson = json;
            row.TitleIds = item.Value.Members.Select(m => m.TitleId).Concat(row.CorrectionJson is null ? [] : DiscSetService.Decode(row.CorrectionJson).Members.Select(m => m.TitleId)).Distinct().ToArray();
            row.Revision = Guid.NewGuid();
        }
        foreach (var row in stored.Where(row => !desired.Any(x => x.Source == row.CatalogSourceId && x.Key == row.ProposalKey)))
        {
            var old = DiscSetService.Decode(row.ProposalJson);
            string json = JsonSerializer.Serialize(old with { ReviewReasons = ["SourceUnavailable"] });
            if (row.ProposalJson == json)
                continue;
            row.ProposalJson = json;
            row.Revision = Guid.NewGuid();
        }
    }


    public async Task<ErrorOr<Page<DiscSetDto>>> ListAsync(int? titleId, string? cursor, int limit, CancellationToken ct)
    {
        if (titleId is not null && !await db.Titles.AnyAsync(x => x.Id == titleId, ct))
            return CatalogErrors.TitleNotFound();
        string fingerprint = KeysetCursor.Fingerprint("admin.disc-sets", titleId, "id:asc");
        var position = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, value => value.IsValid);
        if (position.IsError)
            return FieldErrors.Validation("Request.InvalidParameter", "The cursor is invalid for this query.", "cursor");
        int after = position.Value?.Id ?? 0;
        var rows = new List<DiscSetEntity>();
        while (rows.Count <= limit)
        {
            var candidates = await db.DiscSets.Where(x => x.Id > after && (titleId == null || x.TitleIds.Contains(titleId.Value)))
                .OrderBy(x => x.Id).Take(100).ToListAsync(ct);
            foreach (var row in candidates)
            {
                after = row.Id;
                if (titleId is null || Decode(row.ProposalJson).Members.Any(m => m.TitleId == titleId)
                    || row.CorrectionJson != null && Decode(row.CorrectionJson).Members.Any(m => m.TitleId == titleId))
                    rows.Add(row);
                if (rows.Count > limit)
                    break;
            }
            if (candidates.Count < 100)
                break;
        }
        bool more = rows.Count > limit;
        var items = new List<DiscSetDto>();
        foreach (var batch in rows.Take(limit).Chunk(ReadBatchSize))
        {
            var facts = await ReadFactsAsync(batch, includeProposal: true, ct);
            foreach (var row in batch)
                items.Add(DiscSetContractMapping.ToDto(facts.Rows[row.Id], facts.ClaimsByPlatform[row.PlatformId]));
        }
        return new Page<DiscSetDto>
        {
            Items = items,
            HasNextPage = more,
            NextCursor = more ? KeysetCursor.Encode(new IntegerPosition(rows[limit - 1].Id), fingerprint) : null
        };
    }

    public async Task<ErrorOr<DiscSetDto>> GetAsync(int id, CancellationToken ct)
    {
        var row = await db.DiscSets.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null)
            return Error.NotFound("DiscSet.Missing", "Disc set not found.");
        var facts = await ReadFactsAsync([row], includeProposal: true, ct);
        return DiscSetContractMapping.ToDto(facts.Rows[row.Id], facts.ClaimsByPlatform[row.PlatformId]);
    }

    public Task<string?> ReadSystemKeyAsync(int platformId, CancellationToken ct) =>
        db.Platforms.Where(row => row.Id == platformId).Select(row => row.CanonicalKey).SingleOrDefaultAsync(ct);

    public async Task<ErrorOr<LibraryDiscSetOptionsDto>> ListLibraryOptionsAsync(int libraryId, IReadOnlySet<int> eligibleReleaseIds,
        IReadOnlyList<int> selectedIds, string? query, string? cursor, int limit, CancellationToken ct)
    {
        string fingerprint = KeysetCursor.Fingerprint("admin.library.disc-set-options", libraryId,
            eligibleReleaseIds.Order().ToArray(), selectedIds.Order().ToArray(), query, "id:asc");
        var position = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, value => value.IsValid);
        if (position.IsError)
            return FieldErrors.Validation("Request.InvalidParameter", "The cursor is invalid for this query.", "cursor");
        var selectedRows = await db.DiscSets.Where(x => selectedIds.Contains(x.Id)).OrderBy(x => x.Id).ToListAsync(ct);
        var selectedOptions = new Dictionary<int, LibraryDiscSetOptionDto>();
        foreach (var batch in selectedRows.Chunk(ReadBatchSize))
        {
            var facts = await ReadFactsAsync(batch, includeProposal: false, ct);
            foreach (var row in batch)
                selectedOptions.Add(row.Id, DiscSetContractMapping.ToOption(facts.Rows[row.Id], facts.ClaimsByPlatform[row.PlatformId], eligibleReleaseIds));
        }
        var selected = new List<LibraryDiscSetOptionDto>();
        foreach (int id in selectedIds)
        {
            selected.Add(selectedOptions.TryGetValue(id, out var option) ? option
                : new(IdCoder.Encode(id), "Unavailable disc set", null, null, 0, null, false, "MissingDiscSet"));
        }
        var items = new List<LibraryDiscSetOptionDto>();
        int? scanPosition = position.Value?.Id;
        var itemIds = new List<int>();
        bool terminal = false;
        while (items.Count <= limit && !terminal)
        {
            var rows = await db.DiscSets.Where(x => x.Decision == "accepted" && x.CorrectionJson != null && (scanPosition == null || x.Id > scanPosition))
                .OrderBy(x => x.Id).Take(100).ToListAsync(ct);
            var facts = await ReadFactsAsync(rows, includeProposal: false, ct);
            foreach (var row in rows)
            {
                scanPosition = row.Id;
                var option = DiscSetContractMapping.ToOption(facts.Rows[row.Id], facts.ClaimsByPlatform[row.PlatformId], eligibleReleaseIds);
                if (option.IsEligible && (query == null || option.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                { items.Add(option); itemIds.Add(row.Id); }
                if (items.Count > limit)
                    break;
            }
            terminal = rows.Count == 0 || rows.Count < 100 && scanPosition == rows[^1].Id;
        }
        return new LibraryDiscSetOptionsDto(selectedIds.Select(IdCoder.Encode).ToArray(), selected, new Page<LibraryDiscSetOptionDto>
        {
            Items = items.Take(limit).ToArray(),
            HasNextPage = items.Count > limit,
            NextCursor = items.Count > limit ? KeysetCursor.Encode(new IntegerPosition(itemIds[limit - 1]), fingerprint) : null
        });
    }

}
