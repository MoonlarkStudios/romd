namespace Romd.Admin.Application.Source.Platform;

/// <summary>
///     Resolves a DAT header name to a platform via names, short names,
///     and user-managed name aliases.
/// </summary>
public interface IPlatformHeaderResolver
{
    /// <summary>
    ///     Returns the platform ID the header name unambiguously routes to,
    ///     or null when no platform matches.
    /// </summary>
    Task<int?> ResolvePlatformIdAsync(string datHeaderName, CancellationToken ct = default);
}
