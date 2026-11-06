using System.Text.Json;
using System.Data;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Common.Models;
using Romd.Persistence.ReferenceData;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Platform;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Platform;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Systems;

public sealed class SystemSetupService(RomdDbContext db, IDatReplacementReview review,
    IPlatformHeaderResolver resolver) : ISystemSetupService
{
    private static readonly string[] Terminal = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred"];

    public async Task<Page<ManagedSystem>> ListAsync(ReferencePageRequest request, CancellationToken ct)
    {
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        var platforms = db.Platforms.Where(x => x.Ownership != null);
        if (request.Query.Length > 0)
            platforms = platforms.Where(x => ReferenceSearch.Normalize(x.Name).Contains(request.Query)
                || ReferenceSearch.Normalize(x.ShortName).Contains(request.Query)
                || ReferenceSearch.Normalize(x.CanonicalKey!).Contains(request.Query)
                || ReferenceSearch.Normalize(x.Manufacturer ?? "").Contains(request.Query)
                || db.PlatformAliases.Any(a => a.PlatformId == x.Id && a.Type == PlatformAliasType.Name
                    && ReferenceSearch.Normalize(a.Value).Contains(request.Query)));
        if (request.AfterKey is { } after)
            platforms = platforms.Where(x => string.Compare(EF.Functions.Collate(x.CanonicalKey!, "C"), after) > 0);
        var selected = await platforms.OrderBy(x => EF.Functions.Collate(x.CanonicalKey!, "C")).Take(request.Limit + 1)
            .Select(x => new { x.Id, x.CanonicalKey }).ToListAsync(ct);
        var more = selected.Count > request.Limit;
        var page = selected.Take(request.Limit).ToArray();
        var ids = page.Select(x => x.Id).ToArray();
        var rows = await db.Platforms.Where(x => ids.Contains(x.Id)).OrderBy(x => EF.Functions.Collate(x.CanonicalKey!, "C")).Select(p => new
        {
            Platform = p,
            HasData = db.DatFiles.Any(x => x.PlatformId == p.Id) || db.Titles.Any(x => x.PlatformId == p.Id)
                || db.DatSubscriptions.Any(x => x.PlatformId == p.Id),
            Catalogs = db.DatFiles.Count(x => x.PlatformId == p.Id && x.Lifecycle == "Active"),
            UsableCatalog = db.DatFiles.Any(d => d.PlatformId == p.Id && d.Lifecycle == "Active"
                && db.DatSources.Any(s => s.Id == d.DatSourceId
                    && db.CatalogSources.Any(c => c.Id == s.CatalogSourceId && c.Status == "Active"))),
            Owned = db.Titles.Count(x => x.PlatformId == p.Id && x.HasLocalPayload),
            Tracked = db.Titles.Count(x => x.PlatformId == p.Id && db.TrackedTitles.Any(t => t.TitleId == x.Id)),
            LastPhase = db.Jobs.Where(x => x.PlatformId == p.Id && (x is UploadJobEntity || x is ReplaceDatJobEntity))
                .OrderByDescending(x => x.CreatedAt).Select(x => x.Phase).FirstOrDefault(),
            Processing = db.Jobs.Any(x => x.PlatformId == p.Id && (x is UploadJobEntity || x is ReplaceDatJobEntity) && !Terminal.Contains(x.Phase))
        }).ToListAsync(ct);
        var manufacturers = await new Romd.Persistence.ReferenceData.SystemCompanyReader(db).ReadAsync(rows.Select(x => x.Platform.Id), ct);
        var items = rows.Select(x => new ManagedSystem(x.Platform.Id,
            x.Platform.CanonicalKey,
            x.Platform.Name, x.Platform.ShortName, Romd.Persistence.ReferenceData.SystemCompanyReader.Names(manufacturers.GetValueOrDefault(x.Platform.Id)),
            x.Platform.IsEnabled ?? x.HasData,
            x.UsableCatalog && x.Platform.CatalogRebuildState == CatalogRebuildState.Clean ? "Ready"
                : x.Platform.CatalogRebuildState == CatalogRebuildState.Failed ? "NeedsAttention"
                : x.Processing || x.Platform.CatalogRebuildState != CatalogRebuildState.Clean ? "Processing"
                : x.Catalogs > 0 || x.LastPhase is "Failed" or "CompletedWithErrors" or "Cancelled" ? "NeedsAttention" : "NeedsCatalog",
            x.Platform.CatalogRebuildState == CatalogRebuildState.Failed
                ? "Catalog processing needs attention. Your stored documents and ROMs are preserved. Open Jobs for details."
                : x.Catalogs > 0 && !x.UsableCatalog ? "No installed sources are active. Review their status in Catalog sources."
                : !x.UsableCatalog && x.LastPhase is "Failed" or "CompletedWithErrors" or "Cancelled"
                    ? "The source did not finish processing. Open Jobs for details and retry." : null,
            x.Catalogs, x.Owned, x.Tracked)).ToArray();
        if (snapshot is not null)
            await snapshot.CommitAsync(ct);
        return new Page<ManagedSystem>
        {
            Items = items,
            HasNextPage = more,
            NextCursor = more ? request.CursorFor(page[^1].CanonicalKey!) : null
        };
    }

    public async Task<ErrorOr<Success>> SetEnabledAsync(int platformId, bool enabled, CancellationToken ct)
    {
        var count = await db.Platforms.Where(x => x.Id == platformId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, enabled), ct);
        return count == 0 ? Error.NotFound("SystemSetup.NotFound", "This system no longer exists.") : Result.Success;
    }

    public async Task<ErrorOr<SystemDatPreview>> PreviewAsync(Stream document, CancellationToken ct)
    {
        var inspected = await review.InspectAsync(document, ct);
        if (inspected.IsError)
            return inspected.Errors;
        var suggested = await resolver.ResolvePlatformIdAsync(inspected.Value.Name, ct);
        var existing = await db.DatFiles.Where(x => x.Lifecycle == "Active" && x.Name == inspected.Value.Name)
            .Select(x => x.Id).ToListAsync(ct);
        return new SystemDatPreview(inspected.Value, suggested, existing);
    }

}
