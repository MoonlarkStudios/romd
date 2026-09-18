using ErrorOr;

namespace Romd.Admin.Application.MetadataProviders;

public static class MetadataProviderErrors
{
    public static Error ManagedByDeployment() =>
        Error.Conflict("MetadataProviders.ManagedByDeployment",
            "IGDB credentials are managed by deployment. Remove both deployment credentials to configure IGDB here.");

    public static Error InvalidCredentials() =>
        Error.Validation("MetadataProviders.InvalidCredentials", "IGDB credentials contain invalid characters or exceed the allowed length.");

    public static Error ConflictingSecretUpdate() =>
        Error.Validation("MetadataProviders.ConflictingSecretUpdate", "Choose either replacing or removing the client secret.");

    public static Error ReplacementRequiredForClientId() =>
        Error.Validation("MetadataProviders.SecretRequired", "Replace or remove the client secret when changing the client ID.");

    public static Error CredentialsRequired() =>
        Error.Validation("MetadataProviders.CredentialsRequired", "Set both the Twitch Client ID and Client Secret before enabling IGDB.");

    public static Error StoredSecretReplacementRequired() =>
        Error.Validation("MetadataProviders.SecretRequired", "Replace the stored client secret before enabling IGDB.");

    public static Error ConcurrentUpdate() =>
        Error.Conflict("MetadataProviders.ConcurrentUpdate", "IGDB settings changed. Reload and try again.");
}
