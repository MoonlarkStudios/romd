namespace Romd.Contracts.Management.Models;

/// <summary>
///     An alias attached to a platform: either an alternate name
///     (type "name") or a metadata provider mapping (type "provider").
/// </summary>
public sealed record PlatformAlias
{
    public required string Id { get; init; }

    /// <summary>
    ///     "name" or "provider".
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    ///     Provider identifier (e.g., "igdb") when Type is "provider"; otherwise null.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    ///     The alias text, or the provider's external platform ID.
    /// </summary>
    public required string Value { get; init; }
}

/// <summary>
///     Request to add an alias to a platform.
/// </summary>
public sealed record AddPlatformAliasRequest
{
    /// <summary>
    ///     "name" or "provider".
    /// </summary>
    public required string Type { get; init; }

    public required string Value { get; init; }

    /// <summary>
    ///     Required when Type is "provider".
    /// </summary>
    public string? Provider { get; init; }
}
