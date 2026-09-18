namespace Romd.Domain.Source.Platform;

/// <summary>
///     Discriminates the two kinds of platform alias.
/// </summary>
public enum PlatformAliasType
{
    /// <summary>
    ///     An alternate name for the platform ("SNES", "Super Famicom").
    ///     Drives DAT header auto-routing and provider name search.
    /// </summary>
    Name = 0,

    /// <summary>
    ///     An explicit mapping to a metadata provider's platform identifier
    ///     (e.g., IGDB platform ID 19 for SNES).
    /// </summary>
    ProviderMapping = 1
}

/// <summary>
///     A user-manageable alias attached to a platform. Name aliases route
///     uploaded DATs to their platform by header name; provider mappings
///     translate a platform to an external metadata provider's own ID space.
/// </summary>
public sealed class PlatformAlias
{
    private PlatformAlias(
        int id,
        int platformId,
        PlatformAliasType type,
        string value,
        string normalizedValue,
        string? provider,
        DateTimeOffset createdAt)
    {
        Id = id;
        PlatformId = platformId;
        Type = type;
        Value = value;
        NormalizedValue = normalizedValue;
        Provider = provider;
        CreatedAt = createdAt;
    }

    public int Id { get; private set; }

    /// <summary>
    ///     The platform this alias belongs to.
    /// </summary>
    public int PlatformId { get; private set; }

    public PlatformAliasType Type { get; private set; }

    /// <summary>
    ///     Display value: the alias text for <see cref="PlatformAliasType.Name" />,
    ///     or the external platform ID for <see cref="PlatformAliasType.ProviderMapping" />.
    /// </summary>
    public string Value { get; private set; }

    /// <summary>
    ///     Normalized matching key (lowercase, collapsed whitespace).
    /// </summary>
    public string NormalizedValue { get; private set; }

    /// <summary>
    ///     Provider identifier for provider mappings (e.g., "igdb").
    ///     Always null for name aliases.
    /// </summary>
    public string? Provider { get; private set; }

    /// <summary>
    ///     When this alias was added.
    /// </summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    ///     Creates a name alias for a platform.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when value is null or whitespace.</exception>
    public static PlatformAlias CreateName(int platformId, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();

        return new PlatformAlias(
            0,
            platformId,
            PlatformAliasType.Name,
            trimmed,
            PlatformNameNormalizer.Normalize(trimmed),
            null,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    ///     Creates a provider mapping for a platform.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when provider or externalPlatformId is null or whitespace.</exception>
    public static PlatformAlias CreateProviderMapping(int platformId, string provider, string externalPlatformId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalPlatformId);

        var trimmedId = externalPlatformId.Trim();

        return new PlatformAlias(
            0,
            platformId,
            PlatformAliasType.ProviderMapping,
            trimmedId,
            PlatformNameNormalizer.Normalize(trimmedId),
            provider.Trim().ToLowerInvariant(),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    ///     Rehydrates an alias from persistence. Trusts that data is valid.
    /// </summary>
    internal static PlatformAlias Rehydrate(
        int id,
        int platformId,
        PlatformAliasType type,
        string value,
        string normalizedValue,
        string? provider,
        DateTimeOffset createdAt) =>
        new(id, platformId, type, value, normalizedValue, provider, createdAt);
}
