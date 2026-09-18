using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Artwork;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class ArtworkCurationRepository(RomdDbContext context, TimeProvider clock) : IArtworkCurationRepository
{
    public async Task<ErrorOr<IReadOnlyList<ArtworkAsset>>> GetGalleryAsync(int titleId, CancellationToken ct)
    {
        if (!await context.Titles.AnyAsync(title => title.Id == titleId, ct)) return ArtworkCurationErrors.TitleNotFound();
        var assets = await context.ArtworkAssets.Include(asset => asset.Variants)
            .Where(asset => asset.TitleId == titleId && asset.IsEligible).OrderBy(asset => asset.CreatedAt).ThenBy(asset => asset.Id).ToListAsync(ct);
        return ErrorOrFactory.From<IReadOnlyList<ArtworkAsset>>(assets.Select(asset => asset.ToDomain()).ToArray());
    }

    public async Task<TitleMedia?> FindMediaAsync(int titleId, int mediaId, CancellationToken ct) =>
        (await context.TitleMedia.SingleOrDefaultAsync(media => media.TitleId == titleId && media.Id == mediaId, ct))?.ToDomain();

    public async Task<ErrorOr<bool>> LockSelectionAsync(int titleId, ArtworkRole role, long expectedRevision, CancellationToken ct)
    {
        RequireTransaction();
        if (!await LockTitleAsync(titleId, ct)) return ArtworkCurationErrors.TitleNotFound();
        var selection = await GetSelectionAsync(titleId, role, ct);
        return selection.Revision == expectedRevision ? true : ArtworkCurationErrors.SelectionChanged();
    }

    public async Task<ErrorOr<long>> StagePinAsync(int titleId, ArtworkRole role, int assetId, long expectedRevision, CancellationToken ct, int focalX = 50, int focalY = 50)
    {
        var locked = await LockSelectionAsync(titleId, role, expectedRevision, ct);
        if (locked.IsError) return locked.Errors;
        var asset = await context.ArtworkAssets.Include(item => item.Variants)
            .SingleOrDefaultAsync(item => item.Id == assetId && item.TitleId == titleId && item.Role == role && item.IsEligible, ct);
        if (asset is null) return ArtworkCurationErrors.InvalidRequest();
        var entity = await GetSelectionAsync(titleId, role, ct);
        var selection = entity.ToDomain();
        if (!selection.TryPinRetained(asset.ToDomain(), expectedRevision, focalX, focalY)) return ArtworkCurationErrors.SelectionChanged();
        Copy(selection, entity);
        return selection.Revision;
    }

    public async Task StageLocalAssetAsync(int titleId, ArtworkRole role, string sourceId,
        RetainedArtworkContent content, Guid actorId, CancellationToken ct, string? providerGameId = null, string? providerAssetId = null)
    {
        RequireTransaction();
        var version = content.Original.Hash.ToString();
        var existing = await context.ArtworkAssets.Include(asset => asset.Variants).SingleOrDefaultAsync(asset =>
            asset.TitleId == titleId && asset.Role == role && asset.SourceId == sourceId &&
            asset.ProviderAssetId == providerAssetId && asset.ContentVersion == version, ct);
        if (existing is { IsEligible: true }) return;
        if (existing is not null) throw new InvalidOperationException("An incomplete local artwork asset cannot be published.");
        var files = new Dictionary<Sha256, int>();
        foreach (var file in content.Variants.Prepend(content.Original).DistinctBy(file => file.Hash)
                     .OrderBy(file => file.Hash.ToString(), StringComparer.Ordinal))
            files.Add(file.Hash, await UpsertFileAsync(file, actorId, ct));
        var entity = new ArtworkAssetEntity
        {
            TitleId = titleId, Role = role, SourceId = sourceId, OriginalFileId = files[content.Original.Hash],
            ProviderGameId = providerGameId, ProviderAssetId = providerAssetId,
            Attribution = content.Attribution, SourcePageUrl = content.SourcePageUrl,
            ContentVersion = version, ContentType = content.Original.ContentType,
            Width = content.Original.Width, Height = content.Original.Height, IsEligible = true, CreatedAt = clock.GetUtcNow(),
            Variants = content.Variants.Select(file => new ArtworkVariantEntity
            {
                Name = file.Name, FileId = files[file.Hash], ContentVersion = file.Hash.ToString(),
                ContentType = file.ContentType, Width = file.Width, Height = file.Height
            }).ToList()
        };
        context.ArtworkAssets.Add(entity);
    }
    public async Task<ErrorOr<IReadOnlyList<ArtworkCurationState>>> GetStatesAsync(int titleId, CancellationToken ct)
    {
        if (!await context.Titles.AnyAsync(title => title.Id == titleId, ct))
            return ArtworkCurationErrors.TitleNotFound();
        var states = await context.ArtworkSelections.Where(selection => selection.TitleId == titleId)
            .Select(selection => new ArtworkCurationState(selection.Role, selection.Mode, selection.Revision,
                selection.PinnedAssetId, selection.PendingRequestId)).ToListAsync(ct);
        IReadOnlyList<ArtworkCurationState> result = Enum.GetValues<ArtworkRole>().Select(role =>
            states.SingleOrDefault(state => state.Role == role) ??
            new ArtworkCurationState(role, ArtworkSelectionMode.Automatic, 0, null, null)).ToArray();
        return ErrorOrFactory.From(result);
    }

    public async Task<ErrorOr<ArtworkImportJob>> StageRequestAsync(ArtworkImportRequest request, CancellationToken ct)
    {
        RequireTransaction();
        // The request UUID is global, so retries against different titles must also serialize.
        await JobAcceptanceLock.AcquireAsync(context, 146, request.RequestId.GetHashCode(), ct);
        var existing = await context.Jobs.SingleOrDefaultAsync(job => job.Id == request.RequestId, ct);
        if (existing is not null)
        {
            if (existing is ArtworkImportJobEntity artwork && artwork.TitleId == request.TitleId &&
                artwork.Role == request.Role && artwork.ProviderId == request.ProviderId &&
                artwork.ProviderGameId == request.ProviderGameId && artwork.ProviderAssetId == request.ProviderAssetId &&
                artwork.TrustedAssetUrl == request.TrustedAssetUrl &&
                artwork.FocalX == request.FocalX && artwork.FocalY == request.FocalY &&
                artwork.CreatedByUserId == request.ActorId)
                return artwork.ToDomain();
            return ArtworkCurationErrors.RequestConflict();
        }
        if (!await LockTitleAsync(request.TitleId, ct)) return ArtworkCurationErrors.TitleNotFound();
        if (await context.Set<TitleProviderMatchStateEntity>().AnyAsync(x => x.TitleId == request.TitleId && x.ProviderId == request.ProviderId, ct) &&
            !await context.TitleExternalIds.AnyAsync(x => x.TitleId == request.TitleId && x.Provider == request.ProviderId && x.ExternalId == request.ProviderGameId, ct))
            return ArtworkCurationErrors.RequestConflict();
        var title = await context.Titles.SingleAsync(title => title.Id == request.TitleId, ct);
        var entity = await GetSelectionAsync(request.TitleId, request.Role, ct);
        if (request.ExpectedRevision is { } expected && entity.Revision != expected) return ArtworkCurationErrors.SelectionChanged();
        var selection = entity.ToDomain();
        var revision = selection.RequestPin(request.RequestId);
        Copy(selection, entity);
        var job = ArtworkImportJob.Create(request.RequestId, title.Name, title.Id, title.PlatformId,
            request.Role, revision, request.ProviderId, request.ProviderGameId, request.ProviderAssetId,
            request.TrustedAssetUrl, request.Attribution, clock, request.ActorId, request.FocalX, request.FocalY);
        context.Set<ArtworkImportJobEntity>().Add(ArtworkImportJobEntity.FromDomain(job, clock.GetUtcNow()));
        return job;
    }

    public async Task<ErrorOr<long>> StageAutomaticAsync(int titleId, ArtworkRole role, CancellationToken ct)
    {
        RequireTransaction();
        if (!await LockTitleAsync(titleId, ct)) return ArtworkCurationErrors.TitleNotFound();
        var entity = await GetSelectionAsync(titleId, role, ct);
        var selection = entity.ToDomain();
        var revision = selection.ReturnToAutomatic();
        Copy(selection, entity);
        return revision;
    }

    public async Task<bool> LockPendingAsync(ArtworkImportJob job, CancellationToken ct)
    {
        RequireTransaction();
        if (!await LockTitleAsync(job.TitleId, ct)) return false;
        return await context.ArtworkSelections.AnyAsync(selection => selection.TitleId == job.TitleId &&
            selection.Role == job.Role && selection.Revision == job.SelectionRevision && selection.PendingRequestId == job.Id, ct);
    }

    public async Task StageAssetAsync(ArtworkImportJob job, RetainedArtworkContent content, CancellationToken ct)
    {
        RequireTransaction();
        if (!await LockPendingAsync(job, ct))
            throw new InvalidOperationException("Artwork selection is no longer pending.");
        var version = content.Original.Hash.ToString();
        var existing = await context.ArtworkAssets.AsTracking().Include(asset => asset.Variants)
            .SingleOrDefaultAsync(asset => asset.TitleId == job.TitleId && asset.Role == job.Role &&
                asset.SourceId == job.ProviderId && asset.ProviderAssetId == job.ProviderAssetId &&
                asset.ContentVersion == version, ct);

        // All hashes are locked in one order: simultaneous imports can share original/variant bytes.
        var files = new Dictionary<Sha256, int>();
        foreach (var file in content.Variants.Prepend(content.Original).DistinctBy(file => file.Hash)
                     .OrderBy(file => file.Hash.ToString(), StringComparer.Ordinal))
            files.Add(file.Hash, await UpsertFileAsync(file, job.CreatedByUserId ?? Guid.Empty, ct));

        if (existing is { IsEligible: true }) return;
        var asset = existing ?? new ArtworkAssetEntity
        {
            TitleId = job.TitleId, Role = job.Role, SourceId = job.ProviderId,
            ProviderGameId = job.ProviderGameId, ProviderAssetId = job.ProviderAssetId,
            ContentVersion = version, CreatedAt = clock.GetUtcNow()
        };
        asset.OriginalFileId = files[content.Original.Hash];
        asset.Width = content.Original.Width;
        asset.Height = content.Original.Height;
        asset.ContentType = content.Original.ContentType;
        asset.Attribution = content.Attribution;
        asset.SourcePageUrl = content.SourcePageUrl;
        asset.IsEligible = true;
        // Replace incomplete variants in place to avoid duplicate tracked composite keys.
        foreach (var variant in content.Variants)
        {
            var entity = asset.Variants.SingleOrDefault(candidate => candidate.Name == variant.Name);
            if (entity is null)
            {
                entity = new ArtworkVariantEntity { Name = variant.Name };
                asset.Variants.Add(entity);
            }
            entity.FileId = files[variant.Hash];
            entity.ContentVersion = variant.Hash.ToString();
            entity.ContentType = variant.ContentType;
            entity.Width = variant.Width;
            entity.Height = variant.Height;
        }
        foreach (var obsolete in asset.Variants.Where(variant => content.Variants.All(file => file.Name != variant.Name)).ToArray())
        {
            context.ArtworkVariants.Remove(obsolete);
            asset.Variants.Remove(obsolete);
        }
        // The database identity is allocated at the caller's flush. Validate the
        // measured files now; full aggregate validation follows that flush.
        _ = new ArtworkVariant(asset.OriginalFileId, version, asset.ContentType, asset.Width, asset.Height, "original");
        if (asset.Variants.Count == 0 || asset.Variants.Select(v => v.Name).Append("original")
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != asset.Variants.Count + 1)
            throw new InvalidOperationException("Artwork requires distinct bounded variants.");
        foreach (var variant in asset.Variants) _ = variant.ToDomain();
        if (existing is null) context.ArtworkAssets.Add(asset);
    }

    public async Task<int> StagePublicationAsync(ArtworkImportJob job, string contentVersion, CancellationToken ct)
    {
        RequireTransaction();
        var asset = await context.ArtworkAssets.Include(candidate => candidate.Variants)
            .SingleAsync(candidate => candidate.TitleId == job.TitleId && candidate.Role == job.Role &&
                candidate.SourceId == job.ProviderId && candidate.ProviderAssetId == job.ProviderAssetId &&
                candidate.ContentVersion == contentVersion, ct);
        var entity = await GetSelectionAsync(job.TitleId, job.Role, ct);
        var selection = entity.ToDomain();
        if (!selection.TryPublishPin(job.Id, job.SelectionRevision, asset.ToDomain(), job.FocalX, job.FocalY))
            throw new InvalidOperationException("Artwork publication lost its selection revision.");
        Copy(selection, entity);
        return asset.Id;
    }

    private async Task<int> UpsertFileAsync(RetainedArtworkFile file, Guid actor, CancellationToken ct)
    {
        var hash = file.Hash.ToArray();
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        // PostgreSQL's conflict update holds the existing row against concurrent cleanup;
        // it also handles first-import races without poisoning the surrounding transaction.
        var ids = await context.Database.SqlQuery<int>($"""
            INSERT INTO romd."Files" ("Sha256", "Size", "SizeOnDisk", "IsCompressed", "CreatedAt", "CreatedByUserId")
            VALUES ({hash}, {file.Size}, {file.SizeOnDisk}, {file.IsCompressed}, {now}, {actor})
            ON CONFLICT ("Sha256") DO UPDATE SET "Sha256" = EXCLUDED."Sha256"
            RETURNING "Id" AS "Value"
            """).ToListAsync(ct);
        return ids.Single();
    }

    public async Task StageOutcomeAsync(ArtworkImportJob job, int? assetId, bool superseded, CancellationToken ct)
    {
        RequireTransaction();
        if (assetId is <= 0 || (assetId is null && !superseded))
            throw new ArgumentException("An artwork import outcome is required.");
        var updated = await context.Jobs.OfType<ArtworkImportJobEntity>()
            .Where(entity => entity.Id == job.Id && entity.Phase == nameof(ArtworkImportPhase.Importing))
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.RetainedAssetId, assetId)
                .SetProperty(entity => entity.WasSuperseded, superseded), ct);
        if (updated != 1) throw new InvalidOperationException("Artwork import cannot publish an outcome.");
    }

    private async Task<bool> LockTitleAsync(int titleId, CancellationToken ct) =>
        await context.Titles.Where(title => title.Id == titleId).ExecuteUpdateAsync(setters =>
            setters.SetProperty(title => title.RetainWithoutCatalog, title => title.RetainWithoutCatalog), ct) == 1;

    private async Task<ArtworkSelectionEntity> GetSelectionAsync(int titleId, ArtworkRole role, CancellationToken ct)
    {
        var staged = context.ArtworkSelections.Local.SingleOrDefault(selection => selection.TitleId == titleId && selection.Role == role);
        if (staged is not null) return staged;
        var entity = await context.ArtworkSelections.AsTracking()
            .SingleOrDefaultAsync(selection => selection.TitleId == titleId && selection.Role == role, ct);
        if (entity is not null) return entity;
        entity = new ArtworkSelectionEntity { TitleId = titleId, Role = role, Mode = ArtworkSelectionMode.Automatic };
        context.ArtworkSelections.Add(entity);
        return entity;
    }

    private static void Copy(ArtworkSelection selection, ArtworkSelectionEntity entity)
    {
        entity.Revision = selection.Revision;
        entity.Mode = selection.Mode;
        entity.PinnedAssetId = selection.PinnedAssetId;
        entity.PendingRequestId = selection.PendingRequestId;
        entity.FocalX = selection.FocalX;
        entity.FocalY = selection.FocalY;
    }

    private void RequireTransaction()
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Artwork curation requires a caller-owned transaction.");
    }
}
