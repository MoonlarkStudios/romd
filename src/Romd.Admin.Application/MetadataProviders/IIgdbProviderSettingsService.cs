using ErrorOr;
using Romd.Contracts.Management.MetadataProviders;

namespace Romd.Admin.Application.MetadataProviders;

public interface IIgdbProviderSettingsService
{
    Task<IgdbProviderSettingsDto> GetAsync(CancellationToken ct = default);
    Task<ErrorOr<IgdbProviderSettingsDto>> UpdateAsync(UpdateIgdbProviderSettingsRequest request, CancellationToken ct = default);
    Task<IgdbProviderSettingsDto> TestConnectionAsync(CancellationToken ct = default);
}
