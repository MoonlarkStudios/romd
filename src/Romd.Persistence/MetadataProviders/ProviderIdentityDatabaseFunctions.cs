using Microsoft.EntityFrameworkCore;

namespace Romd.Persistence.MetadataProviders;

/// <summary>
/// The shared database input fingerprint and trust predicate for provider discovery and
/// catalog assessment. These inspect current facts, including source activation, rather
/// than a cached trust bit. Functions run with the caller's ordinary database permissions.
/// </summary>
public static class ProviderIdentityDatabaseFunctions
{
    public static string Fingerprint(int titleId, string policyVersion) =>
        throw new InvalidOperationException("This function must be evaluated by PostgreSQL.");

    public static bool IsTrusted(int titleId, string providerId, string policyVersion) =>
        throw new InvalidOperationException("This function must be evaluated by PostgreSQL.");

    public static void Configure(ModelBuilder builder)
    {
        builder.HasDbFunction(typeof(ProviderIdentityDatabaseFunctions).GetMethod(nameof(Fingerprint))!)
            .HasName("provider_identity_fingerprint").HasSchema(PostgreSqlConfiguration.SchemaName);
        builder.HasDbFunction(typeof(ProviderIdentityDatabaseFunctions).GetMethod(nameof(IsTrusted))!)
            .HasName("provider_identity_trusted").HasSchema(PostgreSqlConfiguration.SchemaName);
    }

}
