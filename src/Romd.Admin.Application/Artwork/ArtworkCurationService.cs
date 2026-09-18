using ErrorOr;
using Romd.Admin.Application.Common.Persistence;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Artwork;

public sealed class ArtworkCurationService(IArtworkCurationRepository repository, IUnitOfWork unitOfWork)
{
    public async Task<ErrorOr<long>> PinAsync(int titleId, ArtworkRole role, int assetId, long expectedRevision, CancellationToken ct = default, int focalX = 50, int focalY = 50)
    {
        if (focalX is < 0 or > 100 || focalY is < 0 or > 100) return ArtworkCurationErrors.InvalidRequest();
        if (titleId <= 0 || assetId <= 0 || expectedRevision < 0 || !Enum.IsDefined(role)) return ArtworkCurationErrors.InvalidRequest();
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var result = await repository.StagePinAsync(titleId, role, assetId, expectedRevision, ct, focalX, focalY);
        if (result.IsError) return result.Errors;
        await transaction.CommitAsync(ct);
        return result.Value;
    }
    public async Task<ErrorOr<ArtworkImportJob>> RequestAsync(ArtworkImportRequest request, CancellationToken ct = default)
    {
        if (request.RequestId == Guid.Empty || request.TitleId <= 0 || !Enum.IsDefined(request.Role) ||
            string.IsNullOrWhiteSpace(request.ProviderId) || request.ProviderId.Length > 50 ||
            string.IsNullOrWhiteSpace(request.ProviderGameId) || request.ProviderGameId.Length > 100 ||
            string.IsNullOrWhiteSpace(request.ProviderAssetId) || request.ProviderAssetId.Length > 100 ||
            string.IsNullOrWhiteSpace(request.TrustedAssetUrl) || request.TrustedAssetUrl.Length > 2048 ||
            request.Attribution?.Length > 500 || request.FocalX is < 0 or > 100 || request.FocalY is < 0 or > 100 || request.ExpectedRevision < 0)
            return ArtworkCurationErrors.InvalidRequest();
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var result = await repository.StageRequestAsync(request, ct);
        if (result.IsError) return result.Errors;
        // The same commit publishes selection intent, job, and its durable dispatch.
        await transaction.CommitAsync(ct);
        return result.Value;
    }

    public async Task<ErrorOr<long>> ReturnToAutomaticAsync(int titleId, ArtworkRole role, CancellationToken ct = default)
    {
        if (titleId <= 0 || !Enum.IsDefined(role)) return ArtworkCurationErrors.InvalidRequest();
        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        var result = await repository.StageAutomaticAsync(titleId, role, ct);
        if (result.IsError) return result.Errors;
        await transaction.CommitAsync(ct);
        return result.Value;
    }
}
