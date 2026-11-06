namespace Romd.Contracts.Management.Artwork;

public sealed record UpdateSteamGridDbSettingsRequest(bool Enabled, string? ApiKey, bool ClearApiKey = false)
{
    public override string ToString() => nameof(UpdateSteamGridDbSettingsRequest);
}
