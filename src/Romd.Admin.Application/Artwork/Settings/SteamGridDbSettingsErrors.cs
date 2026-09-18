using ErrorOr;

namespace Romd.Admin.Application.Artwork.Settings;

public static class SteamGridDbSettingsErrors
{
    public static Error ManagedByDeployment() => Error.Conflict("SteamGridDb.ManagedByDeployment",
        "SteamGridDB is managed by deployment. Remove the deployment API key to configure it here.");
    public static Error ConcurrentUpdate() => Error.Conflict("SteamGridDb.ConcurrentUpdate", "Settings changed. Reload and try again.");
    public static Error InvalidApiKey() => Error.Validation("SteamGridDb.InvalidApiKey", "The API key contains invalid characters or exceeds the allowed length.");
    public static Error ConflictingUpdate() => Error.Validation("SteamGridDb.ConflictingUpdate", "Choose either replacing or removing the API key.");
    public static Error ApiKeyRequired() => Error.Validation("SteamGridDb.ApiKeyRequired", "Set a readable API key before enabling SteamGridDB.");
}
