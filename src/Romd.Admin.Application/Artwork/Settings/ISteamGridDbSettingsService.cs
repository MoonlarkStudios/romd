using ErrorOr;
using Romd.Contracts.Management.Artwork;

namespace Romd.Admin.Application.Artwork.Settings;

public interface ISteamGridDbSettingsService
{
    Task<SteamGridDbSettingsDto> GetAsync(CancellationToken ct = default);
    Task<ErrorOr<SteamGridDbSettingsDto>> UpdateAsync(UpdateSteamGridDbSettingsRequest request, CancellationToken ct = default);
    Task<SteamGridDbSettingsDto> TestConnectionAsync(CancellationToken ct = default);
}
