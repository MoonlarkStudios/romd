using ErrorOr;

namespace Romd.Admin.Application.Source.Platform;

/// <summary>
///     Errors for platform and platform-alias operations.
/// </summary>
public static class PlatformErrors
{
    public static Error ShortNameTaken(string shortName) =>
        Error.Conflict("Platform.ShortNameTaken", $"A platform with short name '{shortName}' already exists");

    public static Error InvalidShortName(string shortName) =>
        Error.Validation(
            "Platform.InvalidShortName",
            $"Short name '{shortName}' must contain only lowercase letters, digits, hyphens, or underscores");

    public static Error AliasAlreadyExists(string value) =>
        Error.Conflict("Platform.AliasAlreadyExists", $"Alias '{value}' is already in use");

    public static Error ProviderMappingAlreadyExists(string provider) =>
        Error.Conflict(
            "Platform.ProviderMappingAlreadyExists",
            $"A mapping for provider '{provider}' already exists on this platform — remove it first");

    public static Error AliasNotFound(int aliasId) =>
        Error.NotFound("Platform.AliasNotFound", $"Alias with ID {aliasId} not found on this platform");

    public static Error InvalidAliasType(string type) =>
        Error.Validation("Platform.InvalidAliasType", $"Alias type must be 'name' or 'provider', got '{type}'");
}
