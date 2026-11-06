using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Artwork;

/// <summary>
///     Curates a title's artwork. A role write runs under that role's revision and answers the role; a write to the
///     family's assets, rejections or hidden candidates runs under the family validator and answers every role. Each
///     answer is read inside the write's transaction, so its revision is the write's own.
/// </summary>
public sealed class ArtworkCurationService(IArtworkCurationRepository repository, IUnitOfWork unitOfWork,
    ILogger<ArtworkCurationService> logger)
{
    public Task<ErrorOr<ArtworkFamily>> DeleteAsync(int titleId, int assetId, RevisionPrecondition ifMatch,
        CancellationToken ct) =>
        titleId <= 0 || assetId <= 0
            ? Task.FromResult<ErrorOr<ArtworkFamily>>(ArtworkCurationErrors.InvalidRequest())
            : FamilyWriteAsync(titleId, () => repository.StageDeleteAsync(titleId, assetId, ifMatch, ct), ct);

    public Task<ErrorOr<ArtworkFamily>> HideAsync(int titleId, string sourceUrl, RevisionPrecondition ifMatch,
        CancellationToken ct) =>
        FamilyWriteAsync(titleId, () => repository.StageHideAsync(titleId, sourceUrl, ifMatch, ct), ct);

    public Task<ErrorOr<ArtworkFamily>> RejectAsync(int titleId, int assetId, bool rejected, RevisionPrecondition ifMatch,
        CancellationToken ct) =>
        titleId <= 0 || assetId <= 0
            ? Task.FromResult<ErrorOr<ArtworkFamily>>(ArtworkCurationErrors.InvalidRequest())
            : FamilyWriteAsync(titleId, () => repository.StageRejectionAsync(titleId, assetId, ifMatch, rejected, ct), ct);

    public Task<ErrorOr<ArtworkCurationState>> LeaveEmptyAsync(int titleId, ArtworkRole role, RevisionPrecondition ifMatch,
        CancellationToken ct) =>
        titleId <= 0 || !Enum.IsDefined(role)
            ? Task.FromResult<ErrorOr<ArtworkCurationState>>(ArtworkCurationErrors.InvalidRequest())
            : RoleWriteAsync(titleId, role, () => repository.StageEmptyAsync(titleId, role, ifMatch, ct), ct);

    public Task<ErrorOr<ArtworkCurationState>> PinAsync(int titleId, ArtworkRole role, int assetId, RevisionPrecondition ifMatch,
        CancellationToken ct = default, int focalX = 50, int focalY = 50) =>
        focalX is < 0 or > 100 || focalY is < 0 or > 100 || titleId <= 0 || assetId <= 0 || !Enum.IsDefined(role)
            ? Task.FromResult<ErrorOr<ArtworkCurationState>>(ArtworkCurationErrors.InvalidRequest())
            : RoleWriteAsync(titleId, role, () => repository.StagePinAsync(titleId, role, assetId, ifMatch, ct, focalX, focalY), ct);

    public Task<ErrorOr<ArtworkCurationState>> ReturnToAutomaticAsync(int titleId, ArtworkRole role, RevisionPrecondition ifMatch,
        CancellationToken ct = default) =>
        titleId <= 0 || !Enum.IsDefined(role)
            ? Task.FromResult<ErrorOr<ArtworkCurationState>>(ArtworkCurationErrors.InvalidRequest())
            : RoleWriteAsync(titleId, role, () => repository.StageAutomaticAsync(titleId, role, ifMatch, ct), ct);

    public async Task<ErrorOr<ArtworkImportJob>> RequestAsync(ArtworkImportRequest request, CancellationToken ct = default)
    {
        if (request.RequestId == Guid.Empty || request.TitleId <= 0 || !Enum.IsDefined(request.Role) ||
            string.IsNullOrWhiteSpace(request.ProviderId) || request.ProviderId.Length > 50 ||
            string.IsNullOrWhiteSpace(request.ProviderGameId) || request.ProviderGameId.Length > 100 ||
            string.IsNullOrWhiteSpace(request.ProviderAssetId) || request.ProviderAssetId.Length > 100 ||
            string.IsNullOrWhiteSpace(request.TrustedAssetUrl) || request.TrustedAssetUrl.Length > 2048 ||
            request.Attribution?.Length > 500 || request.FocalX is < 0 or > 100 || request.FocalY is < 0 or > 100)
            return ArtworkCurationErrors.InvalidRequest();
        // The same commit publishes selection intent, job, and its durable dispatch.
        return await unitOfWork.ExecuteInTransactionAsync<ArtworkImportJob>(
            token => repository.StageRequestAsync(request, token), logger, ct);
    }

    private async Task<ErrorOr<ArtworkCurationState>> RoleWriteAsync<T>(int titleId, ArtworkRole role,
        Func<Task<ErrorOr<T>>> stage, CancellationToken ct)
    {
        var family = await FamilyWriteAsync(titleId, stage, ct);
        return family.IsError ? family.Errors : family.Value.Roles.Single(state => state.Role == role);
    }

    private async Task<ErrorOr<ArtworkFamily>> FamilyWriteAsync<T>(int titleId, Func<Task<ErrorOr<T>>> stage,
        CancellationToken ct)
    {
        return await unitOfWork.ExecuteInTransactionAsync<ArtworkFamily>(async token =>
        {
            var staged = await stage();
            if (staged.IsError)
                return staged.Errors;
            // Read the answer before commit, so a writer that commits after this one is in neither its body nor its ETag.
            await unitOfWork.FlushAsync(token);
            var family = await repository.GetFamilyAsync(titleId, token);
            if (family.IsError)
                return family.Errors;
            return family.Value;
        }, logger, ct);
    }
}
