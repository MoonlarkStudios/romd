namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Rate limiter for metadata providers.
///     Ensures providers are not called more frequently than their API limits allow.
/// </summary>
public interface IProviderRateLimiter
{
    /// <summary>
    ///     Acquires a permit to make a request to the specified provider.
    ///     Blocks until a permit is available or cancellation is requested.
    /// </summary>
    /// <param name="providerId">The provider to acquire a permit for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AcquireAsync(string providerId, CancellationToken cancellationToken = default);
}
