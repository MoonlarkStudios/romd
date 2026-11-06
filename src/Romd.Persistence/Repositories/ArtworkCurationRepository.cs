using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Pagination;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class ArtworkCurationRepository(RomdDbContext context, TimeProvider clock) : IArtworkCurationRepository
{
    public async Task<ErrorOr<PagedList<SavedArtworkReadModel>>> ListAssetsAsync(int titleId, string? cursor, int limit, CancellationToken ct)
    {
        if (!await context.Titles.AnyAsync(title => title.Id == titleId, ct))
            return CatalogErrors.TitleNotFound();
        if (limit is < 1 or > 100)
            return Error.Validation("Artwork.InvalidLimit", "Choose a limit between 1 and 100.");
        var fingerprint = KeysetCursor.Fingerprint("title-artwork-assets", titleId, "createdAt,id", "asc", "eligible-kept");
        var decoded = KeysetCursor.Decode<AssetPosition>(cursor, fingerprint, position => position.Id > 0 && position.CreatedAt != default);
        if (decoded.IsError)
            return decoded.Errors;
        var query = context.ArtworkAssets.Where(asset => asset.TitleId == titleId && asset.IsEligible && !asset.IsRejected);
        if (decoded.Value is { } after)
            query = query.Where(asset => asset.CreatedAt > after.CreatedAt || asset.CreatedAt == after.CreatedAt && asset.Id > after.Id);
        var assets = await query.Include(asset => asset.Variants).OrderBy(asset => asset.CreatedAt).ThenBy(asset => asset.Id)
            .Take(limit + 1).ToListAsync(ct);
        var hasNext = assets.Count > limit;
        var page = assets.Take(limit).ToArray();
        var fileIds = page.Select(asset => asset.OriginalFileId).Distinct().ToArray();
        var media = await context.TitleMedia.Where(item => item.TitleId == titleId && fileIds.Contains(item.FileId))
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.FileId }).ToListAsync(ct);
        var items = page.Select(asset => new SavedArtworkReadModel(asset.ToDomain(),
            media.Where(item => item.FileId == asset.OriginalFileId).Select(item => item.Id).ToArray())).ToArray();
        var next = hasNext ? KeysetCursor.Encode(new AssetPosition(page[^1].CreatedAt, page[^1].Id), fingerprint) : null;
        return new PagedList<SavedArtworkReadModel>(items, next, hasNext);
    }

    public async Task<ErrorOr<PagedList<ArtworkAsset>>> ListRejectionsAsync(int titleId, string? cursor, int limit, CancellationToken ct)
    {
        if (!await context.Titles.AnyAsync(title => title.Id == titleId, ct))
            return CatalogErrors.TitleNotFound();
        if (limit is < 1 or > 100)
            return Error.Validation("Artwork.InvalidLimit", "Choose a limit between 1 and 100.");
        var fingerprint = KeysetCursor.Fingerprint("title-artwork-rejections", titleId, "id", "asc", "rejected");
        var decoded = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, position => position.IsValid);
        if (decoded.IsError)
            return decoded.Errors;
        var after = decoded.Value?.Id ?? 0;
        var assets = await context.ArtworkAssets.Include(asset => asset.Variants)
            .Where(asset => asset.TitleId == titleId && asset.IsRejected && asset.Id > after)
            .OrderBy(asset => asset.Id).Take(limit + 1).ToListAsync(ct);
        var hasNext = assets.Count > limit;
        var page = assets.Take(limit).ToArray();
        var next = hasNext ? KeysetCursor.Encode(new IntegerPosition(page[^1].Id), fingerprint) : null;
        return new PagedList<ArtworkAsset>(page.Select(asset => asset.ToDomain()).ToArray(), next, hasNext);
    }

    private sealed record AssetPosition(DateTimeOffset CreatedAt, int Id);

    public async Task<ErrorOr<IReadOnlyList<ArtworkAsset>>> GetGalleryAsync(int titleId, CancellationToken ct)
    {
        if (!await context.Titles.AnyAsync(title => title.Id == titleId, ct))
            return CatalogErrors.TitleNotFound();
        var assets = await context.ArtworkAssets.Include(asset => asset.Variants)
            .Where(asset => asset.TitleId == titleId && asset.IsEligible && !asset.IsRejected).OrderBy(asset => asset.CreatedAt).ThenBy(asset => asset.Id).ToListAsync(ct);
        return ErrorOrFactory.From<IReadOnlyList<ArtworkAsset>>(assets.Select(asset => asset.ToDomain()).ToArray());
    }

    public async Task<ErrorOr<IReadOnlyList<ArtworkAsset>>> GetRejectedAsync(int titleId, CancellationToken ct)
    {
        if (!await context.Titles.AnyAsync(x => x.Id == titleId, ct))
            return CatalogErrors.TitleNotFound();
        var assets = await context.ArtworkAssets.Include(x => x.Variants)
            .Where(x => x.TitleId == titleId && x.IsRejected).OrderBy(x => x.Id).ToListAsync(ct);
        return ErrorOrFactory.From<IReadOnlyList<ArtworkAsset>>(assets.Select(x => x.ToDomain()).ToArray());
    }

    public async Task<ErrorOr<long>> StageRejectionAsync(int titleId, int assetId, RevisionPrecondition familyIfMatch, bool rejected, CancellationToken ct)
    {
        var locked = await LockFamilyAsync(titleId, familyIfMatch, ct);
        if (locked.IsError)
            return locked.Errors;
        var asset = await context.ArtworkAssets.AsTracking().SingleOrDefaultAsync(x => x.TitleId == titleId &&
            x.Id == assetId && x.IsEligible, ct);
        if (asset is null)
            return ArtworkCurationErrors.AssetNotFound();
        var role = asset.Role;
        var entity = await GetSelectionAsync(titleId, role, ct);
        // Every version of the same provider candidate has the same rejection intent.
        if (asset.ProviderGameId is not null && asset.ProviderAssetId is not null)
            await context.ArtworkAssets.Where(x => x.TitleId == titleId && x.Role == role && x.SourceId == asset.SourceId &&
                x.ProviderGameId == asset.ProviderGameId && x.ProviderAssetId == asset.ProviderAssetId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRejected, rejected), ct);
        asset.IsRejected = rejected;
        var selection = entity.ToDomain();
        if (rejected && entity.PinnedAssetId == assetId)
            selection.ReturnToAutomatic();
        else if (entity.Mode == ArtworkSelectionMode.Empty)
            selection.LeaveEmpty();
        else if (entity.Mode == ArtworkSelectionMode.Automatic)
            selection.ReturnToAutomatic();
        else
        {
            entity.Revision++;
            entity.PendingRequestId = null;
            return entity.Revision;
        }
        Copy(selection, entity);
        return selection.Revision;
    }

    public async Task<IReadOnlyList<string>> GetExcludedUrlsAsync(int titleId, CancellationToken ct) =>
        await context.Set<ArtworkExclusionEntity>().Where(x => x.TitleId == titleId).Select(x => x.SourceUrl).ToListAsync(ct);

    public async Task<ErrorOr<bool>> StageHideAsync(int titleId, string sourceUrl, RevisionPrecondition familyIfMatch, CancellationToken ct)
    {
        var locked = await LockFamilyAsync(titleId, familyIfMatch, ct);
        if (locked.IsError)
            return locked.Errors;
        if (!await context.Set<ArtworkExclusionEntity>().AnyAsync(x => x.TitleId == titleId && x.SourceUrl == sourceUrl, ct))
            context.Add(new ArtworkExclusionEntity { TitleId = titleId, SourceUrl = sourceUrl });
        // Fence any import/automatic acquisition that started before the exclusion.
        foreach (var role in Enum.GetValues<ArtworkRole>())
        {
            var selection = await GetSelectionAsync(titleId, role, ct);
            if (selection.PendingRequestId is { } pending &&
                !await context.Set<ArtworkImportJobEntity>().AnyAsync(x => x.Id == pending && x.TrustedAssetUrl == sourceUrl, ct))
                continue;
            selection.Revision++;
            selection.PendingRequestId = null;
        }
        // In-flight metadata saves must not reintroduce gallery media from an older title snapshot.
        await context.Titles.Where(x => x.Id == titleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, Guid.NewGuid()), ct);
        return true;
    }

    public async Task<ErrorOr<bool>> StageDeleteAsync(int titleId, int assetId, RevisionPrecondition familyIfMatch,
        CancellationToken ct)
    {
        var locked = await LockFamilyAsync(titleId, familyIfMatch, ct);
        if (locked.IsError)
            return locked.Errors;
        var fileId = await context.ArtworkAssets.Where(x => x.TitleId == titleId && x.Id == assetId && !x.IsRejected)
            .Select(x => (int?)x.OriginalFileId).SingleOrDefaultAsync(ct);
        if (fileId is null)
            return ArtworkCurationErrors.AssetNotFound();
        // Deleting the image removes every title copy of its file.
        var media = await context.TitleMedia.Where(x => x.TitleId == titleId && x.FileId == fileId).ToListAsync(ct);
        await StageImageRemovalAsync(titleId, fileId.Value, media.Select(x => x.SourceUrl).OfType<string>().Distinct().ToArray(), ct);
        context.TitleMedia.RemoveRange(media);
        foreach (var type in media.Where(x => x.IsPrimary).Select(x => x.Type).Distinct())
        {
            var remaining = await context.TitleMedia.AsTracking().Where(x => x.TitleId == titleId && x.Type == type && x.FileId != fileId)
                .OrderByDescending(x => x.SourceId == "user").ThenBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
            if (remaining is not null)
                remaining.IsPrimary = true;
        }
        // In-flight metadata saves must not reintroduce gallery media from an older title snapshot.
        await context.Titles.Where(x => x.Id == titleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, Guid.NewGuid()), ct);
        return true;
    }

    public async Task StageTitleMediaRemovalAsync(int titleId, int mediaId, int fileId, string? sourceUrl, CancellationToken ct)
    {
        RequireTransaction();
        if (!await LockTitleAsync(titleId, ct))
            return;
        // Another row of the title still shows the file, so only the removed row's source leaves acquisition.
        if (await context.TitleMedia.AnyAsync(media => media.TitleId == titleId && media.FileId == fileId && media.Id != mediaId, ct))
        {
            if (sourceUrl is not null)
                await StageExclusionsAsync(titleId, [sourceUrl], ct);
            return;
        }
        await StageImageRemovalAsync(titleId, fileId, sourceUrl is null ? [] : [sourceUrl], ct);
    }

    /// <summary>Excludes source URLs from the title's acquisition, each once.</summary>
    private async Task StageExclusionsAsync(int titleId, IEnumerable<string> sourceUrls, CancellationToken ct)
    {
        foreach (var url in sourceUrls)
            if (!await context.Set<ArtworkExclusionEntity>().AnyAsync(x => x.TitleId == titleId && x.SourceUrl == url, ct) &&
                !context.Set<ArtworkExclusionEntity>().Local.Any(x => x.TitleId == titleId && x.SourceUrl == url))
                context.Add(new ArtworkExclusionEntity { TitleId = titleId, SourceUrl = url });
    }

    /// <summary>
    ///     Removes an image from a title's artwork: rejects every asset of the file and its provider versions,
    ///     excludes the source URLs from acquisition, returns roles pinned to a rejected asset to automatic, and fences
    ///     every other role except one whose pending import is for unrelated artwork.
    /// </summary>
    private async Task StageImageRemovalAsync(int titleId, int fileId, IReadOnlyCollection<string> sourceUrls, CancellationToken ct)
    {
        var states = new List<ArtworkSelectionEntity>();
        foreach (var role in Enum.GetValues<ArtworkRole>())
            states.Add(await GetSelectionAsync(titleId, role, ct));
        var assets = await context.ArtworkAssets.AsTracking().Where(x => x.TitleId == titleId && x.OriginalFileId == fileId).ToListAsync(ct);
        // Keep identity records and bytes as soft-deleted artwork; shared files remain safe.
        foreach (var asset in assets)
        {
            asset.IsRejected = true;
            if (asset.ProviderGameId is not null && asset.ProviderAssetId is not null)
                await context.ArtworkAssets.Where(x => x.TitleId == titleId && x.Role == asset.Role && x.SourceId == asset.SourceId &&
                    x.ProviderGameId == asset.ProviderGameId && x.ProviderAssetId == asset.ProviderAssetId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRejected, true), ct);
        }
        await StageExclusionsAsync(titleId, sourceUrls, ct);
        foreach (var state in states)
        {
            if (state.PinnedAssetId is { } pinned && (assets.Any(x => x.Id == pinned) ||
                await context.ArtworkAssets.AnyAsync(x => x.Id == pinned && x.IsRejected, ct)))
            {
                var selection = state.ToDomain();
                selection.ReturnToAutomatic();
                Copy(selection, state);
            }
            else
            {
                if (state.PendingRequestId is { } pending)
                {
                    var job = await context.Set<ArtworkImportJobEntity>().SingleOrDefaultAsync(x => x.Id == pending, ct);
                    if (job is not null && !sourceUrls.Contains(job.TrustedAssetUrl) &&
                        !assets.Any(x => x.SourceId == job.ProviderId && x.ProviderGameId == job.ProviderGameId && x.ProviderAssetId == job.ProviderAssetId))
                        continue;
                }
                state.Revision++;
                state.PendingRequestId = null;
            }
        }
    }

    public async Task<TitleMedia?> FindMediaAsync(int titleId, int mediaId, CancellationToken ct) =>
        (await context.TitleMedia.SingleOrDefaultAsync(media => media.TitleId == titleId && media.Id == mediaId, ct))?.ToDomain();

    public async Task<ErrorOr<bool>> LockSelectionAsync(int titleId, ArtworkRole role, RevisionPrecondition ifMatch, CancellationToken ct)
    {
        RequireTransaction();
        if (!await LockTitleAsync(titleId, ct))
            return CatalogErrors.TitleNotFound();
        var selection = await GetSelectionAsync(titleId, role, ct);
        return ifMatch.IsSatisfiedBy(ArtworkValidators.Role(selection.Revision)) ? true : ArtworkCurationErrors.PreconditionFailed();
    }

    /// <summary>Locks the title and compares the precondition with the family validator, read as a GET reads it.</summary>
    private async Task<ErrorOr<bool>> LockFamilyAsync(int titleId, RevisionPrecondition ifMatch, CancellationToken ct)
    {
        RequireTransaction();
        if (!await LockTitleAsync(titleId, ct))
            return CatalogErrors.TitleNotFound();
        var family = await GetFamilyAsync(titleId, ct);
        if (family.IsError)
            return family.Errors;
        return ifMatch.IsSatisfiedBy(family.Value.Revision) ? true : ArtworkCurationErrors.PreconditionFailed();
    }

    /// <summary>
    ///     Reads the roles and the validator in one statement, so a write between reads cannot pair a newer
    ///     validator with older roles. The validator comes from the family's own mutable state: role revisions (a
    ///     role without a row is 0), saved assets, rejected assets and hidden candidates. The read and every family
    ///     write compute it here.
    /// </summary>
    public async Task<ErrorOr<ArtworkFamily>> GetFamilyAsync(int titleId, CancellationToken ct)
    {
        // One UNION ALL of the title, its roles, its kept assets and its hidden candidates: a row per item, never a
        // product of the three lists.
        // The roles come first, so the stored enum columns give the union its column types.
        var rows = await context.ArtworkSelections.Where(selection => selection.TitleId == titleId).Select(selection => new
        {
            Kind = 1,
            Role = (ArtworkRole?)selection.Role,
            Mode = (ArtworkSelectionMode?)selection.Mode,
            Revision = (long?)selection.Revision,
            selection.PinnedAssetId,
            selection.PendingRequestId,
            AssetId = (int?)null,
            IsRejected = false,
            SourceUrl = (string?)null
        })
            .Concat(context.Titles.Where(title => title.Id == titleId).Select(title => new
            {
                Kind = 0,
                Role = (ArtworkRole?)null,
                Mode = (ArtworkSelectionMode?)null,
                Revision = (long?)null,
                PinnedAssetId = (int?)null,
                PendingRequestId = (Guid?)null,
                AssetId = (int?)null,
                IsRejected = false,
                SourceUrl = (string?)null
            }))
            .Concat(context.ArtworkAssets.Where(asset => asset.TitleId == titleId && (asset.IsEligible || asset.IsRejected))
                .Select(asset => new
                {
                    Kind = 2,
                    Role = (ArtworkRole?)null,
                    Mode = (ArtworkSelectionMode?)null,
                    Revision = (long?)null,
                    PinnedAssetId = (int?)null,
                    PendingRequestId = (Guid?)null,
                    AssetId = (int?)asset.Id,
                    asset.IsRejected,
                    SourceUrl = (string?)null
                }))
            .Concat(context.Set<ArtworkExclusionEntity>().Where(exclusion => exclusion.TitleId == titleId)
                .Select(exclusion => new
                {
                    Kind = 3,
                    Role = (ArtworkRole?)null,
                    Mode = (ArtworkSelectionMode?)null,
                    Revision = (long?)null,
                    PinnedAssetId = (int?)null,
                    PendingRequestId = (Guid?)null,
                    AssetId = (int?)null,
                    IsRejected = false,
                    SourceUrl = (string?)exclusion.SourceUrl
                }))
            .ToListAsync(ct);
        if (!rows.Any(row => row.Kind == 0))
            return CatalogErrors.TitleNotFound();
        var roles = rows.Where(row => row.Kind == 1).Select(row => new ArtworkCurationState(row.Role!.Value, row.Mode!.Value,
            row.Revision!.Value, row.PinnedAssetId, row.PendingRequestId)).ToArray();
        var assets = rows.Where(row => row.Kind == 2).ToArray();
        return new ArtworkFamily(WithEveryRole(roles),
            ArtworkValidators.Family(roles.Select(role => (role.Role, role.Revision)),
                assets.Where(asset => !asset.IsRejected).Select(asset => asset.AssetId!.Value),
                assets.Where(asset => asset.IsRejected).Select(asset => asset.AssetId!.Value),
                rows.Where(row => row.Kind == 3).Select(row => ArtworkCandidateIds.Of(row.SourceUrl!))));
    }

    public async Task<ErrorOr<long>> StageEmptyAsync(int titleId, ArtworkRole role, RevisionPrecondition ifMatch, CancellationToken ct)
    {
        var locked = await LockSelectionAsync(titleId, role, ifMatch, ct);
        if (locked.IsError)
            return locked.Errors;
        var entity = await GetSelectionAsync(titleId, role, ct);
        var selection = entity.ToDomain();
        selection.LeaveEmpty();
        Copy(selection, entity);
        return selection.Revision;
    }

    public async Task<ErrorOr<long>> StagePinAsync(int titleId, ArtworkRole role, int assetId, RevisionPrecondition ifMatch, CancellationToken ct, int focalX = 50, int focalY = 50)
    {
        var locked = await LockSelectionAsync(titleId, role, ifMatch, ct);
        if (locked.IsError)
            return locked.Errors;
        var asset = await context.ArtworkAssets.Include(item => item.Variants)
            .SingleOrDefaultAsync(item => item.Id == assetId && item.TitleId == titleId && item.Role == role && item.IsEligible && !item.IsRejected, ct);
        if (asset is null)
            return ArtworkCurationErrors.InvalidRequest();
        var entity = await GetSelectionAsync(titleId, role, ct);
        var selection = entity.ToDomain();
        if (!selection.TryPinRetained(asset.ToDomain(), entity.Revision, focalX, focalY))
            return ArtworkCurationErrors.SelectionChanged();
        Copy(selection, entity);
        return selection.Revision;
    }

    public async Task StageLocalAssetAsync(int titleId, ArtworkRole role, string sourceId,
        RetainedArtworkContent content, CancellationToken ct, string? providerGameId = null, string? providerAssetId = null)
    {
        RequireTransaction();
        var version = content.Original.Hash.ToString();
        var existing = await context.ArtworkAssets.Include(asset => asset.Variants).SingleOrDefaultAsync(asset =>
            asset.TitleId == titleId && asset.Role == role && asset.SourceId == sourceId &&
            asset.ProviderAssetId == providerAssetId && asset.ContentVersion == version, ct);
        if (existing is { IsEligible: true })
            return;
        if (existing is not null)
            throw new InvalidOperationException("An incomplete local artwork asset cannot be published.");
        var entity = new ArtworkAssetEntity
        {
            TitleId = titleId,
            Role = role,
            SourceId = sourceId,
            OriginalFileId = content.Original.FileId,
            ProviderGameId = providerGameId,
            ProviderAssetId = providerAssetId,
            Attribution = content.Attribution,
            SourcePageUrl = content.SourcePageUrl,
            ContentVersion = version,
            ContentType = content.Original.ContentType,
            Width = content.Original.Width,
            Height = content.Original.Height,
            IsEligible = true,
            CreatedAt = clock.GetUtcNow(),
            Variants = content.Variants.Select(file => new ArtworkVariantEntity
            {
                Name = file.Name,
                FileId = file.FileId,
                ContentVersion = file.Hash.ToString(),
                ContentType = file.ContentType,
                Width = file.Width,
                Height = file.Height
            }).ToList()
        };
        context.ArtworkAssets.Add(entity);
    }
    public async Task<ErrorOr<IReadOnlyList<ArtworkCurationState>>> GetStatesAsync(int titleId, CancellationToken ct)
    {
        if (!await context.Titles.AnyAsync(title => title.Id == titleId, ct))
            return CatalogErrors.TitleNotFound();
        var states = await context.ArtworkSelections.Where(selection => selection.TitleId == titleId)
            .Select(selection => new ArtworkCurationState(selection.Role, selection.Mode, selection.Revision,
                selection.PinnedAssetId, selection.PendingRequestId)).ToListAsync(ct);
        return ErrorOrFactory.From(WithEveryRole(states));
    }

    /// <summary>Every role in order; a role without a stored selection is automatic at revision 0.</summary>
    private static IReadOnlyList<ArtworkCurationState> WithEveryRole(IEnumerable<ArtworkCurationState> stored) =>
        Enum.GetValues<ArtworkRole>().Select(role => stored.SingleOrDefault(state => state.Role == role) ??
            new ArtworkCurationState(role, ArtworkSelectionMode.Automatic, 0, null, null)).ToArray();

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
        if (!await LockTitleAsync(request.TitleId, ct))
            return CatalogErrors.TitleNotFound();
        if (await context.Set<TitleProviderMatchStateEntity>().AnyAsync(x => x.TitleId == request.TitleId && x.ProviderId == request.ProviderId, ct) &&
            !await context.TitleExternalIds.AnyAsync(x => x.TitleId == request.TitleId && x.Provider == request.ProviderId && x.ExternalId == request.ProviderGameId, ct))
            return ArtworkCurationErrors.RequestConflict();
        if (await context.Set<ArtworkExclusionEntity>().AnyAsync(x => x.TitleId == request.TitleId && x.SourceUrl == request.TrustedAssetUrl, ct) ||
            await context.ArtworkAssets.AnyAsync(x => x.TitleId == request.TitleId && x.Role == request.Role && x.SourceId == request.ProviderId &&
                x.ProviderGameId == request.ProviderGameId && x.ProviderAssetId == request.ProviderAssetId && x.IsRejected, ct))
            return ArtworkCurationErrors.InvalidRequest();
        var title = await context.Titles.SingleAsync(title => title.Id == request.TitleId, ct);
        var entity = await GetSelectionAsync(request.TitleId, request.Role, ct);
        if (!request.IfMatch.IsSatisfiedBy(ArtworkValidators.Role(entity.Revision)))
            return ArtworkCurationErrors.PreconditionFailed();
        var selection = entity.ToDomain();
        var revision = selection.RequestPin(request.RequestId);
        Copy(selection, entity);
        var createdAt = clock.GetUtcNow();
        var job = ArtworkImportJob.Create(request.RequestId, title.Name, title.Id, title.PlatformId,
            request.Role, revision, request.ProviderId, request.ProviderGameId, request.ProviderAssetId,
            request.TrustedAssetUrl, request.Attribution, createdAt, request.ActorId, request.FocalX, request.FocalY);
        context.Set<ArtworkImportJobEntity>().Add(ArtworkImportJobEntity.FromDomain(job, createdAt));
        return job;
    }

    public async Task<ErrorOr<long>> StageAutomaticAsync(int titleId, ArtworkRole role, RevisionPrecondition ifMatch, CancellationToken ct)
    {
        var locked = await LockSelectionAsync(titleId, role, ifMatch, ct);
        if (locked.IsError)
            return locked.Errors;
        var entity = await GetSelectionAsync(titleId, role, ct);
        var selection = entity.ToDomain();
        var revision = selection.ReturnToAutomatic();
        Copy(selection, entity);
        return revision;
    }

    public async Task<bool> LockPendingAsync(ArtworkImportJob job, CancellationToken ct)
    {
        RequireTransaction();
        if (!await LockTitleAsync(job.TitleId, ct))
            return false;
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

        if (existing is { IsEligible: true })
            return;
        var asset = existing ?? new ArtworkAssetEntity
        {
            TitleId = job.TitleId,
            Role = job.Role,
            SourceId = job.ProviderId,
            ProviderGameId = job.ProviderGameId,
            ProviderAssetId = job.ProviderAssetId,
            ContentVersion = version,
            CreatedAt = clock.GetUtcNow()
        };
        asset.OriginalFileId = content.Original.FileId;
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
            entity.FileId = variant.FileId;
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
        foreach (var variant in asset.Variants)
            _ = variant.ToDomain();
        if (existing is null)
            context.ArtworkAssets.Add(asset);
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

    public async Task StageOutcomeAsync(ArtworkImportJob job, int? assetId, bool superseded, CancellationToken ct)
    {
        RequireTransaction();
        if (assetId is <= 0 || (assetId is null && !superseded))
            throw new ArgumentException("An artwork import outcome is required.");
        var updated = await context.Jobs.OfType<ArtworkImportJobEntity>()
            .Where(entity => entity.Id == job.Id && entity.Phase == nameof(ArtworkImportPhase.Importing))
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.RetainedAssetId, assetId)
                .SetProperty(entity => entity.WasSuperseded, superseded), ct);
        if (updated != 1)
            throw new InvalidOperationException("Artwork import cannot publish an outcome.");
    }

    private Task<bool> LockTitleAsync(int titleId, CancellationToken ct) => TitleRowLock.AcquireAsync(context, titleId, ct);

    private async Task<ArtworkSelectionEntity> GetSelectionAsync(int titleId, ArtworkRole role, CancellationToken ct)
    {
        var staged = context.ArtworkSelections.Local.SingleOrDefault(selection => selection.TitleId == titleId && selection.Role == role);
        if (staged is not null)
            return staged;
        var entity = await context.ArtworkSelections.AsTracking()
            .SingleOrDefaultAsync(selection => selection.TitleId == titleId && selection.Role == role, ct);
        if (entity is not null)
            return entity;
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
