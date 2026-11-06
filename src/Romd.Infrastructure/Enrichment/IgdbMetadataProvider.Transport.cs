using System.Net;

namespace Romd.Infrastructure.Enrichment;

public sealed partial class IgdbMetadataProvider
{
    /// <summary>
    /// Replays one rejected authorization with current credentials. Each attempt owns a new
    /// request/body and uses the configured client, deadlines and provider rate admission.
    /// The caller owns the final response; a second 401 is never replayed here.
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthorizedAsync(string endpoint, string query,
        HttpCompletionOption completion, CancellationToken ct)
    {
        var authorization = await GetAccessTokenAsync(ct);
        var response = await SendAttemptAsync(authorization);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // A late rejection only invalidates the token attached to this request, never
        // a replacement that another scope has already refreshed.
        _accessTokens.Invalidate(authorization.Token);
        response.Dispose();
        ct.ThrowIfCancellationRequested();

        // A long-running provider may have initialized before a curator rotated or
        // disabled its credentials. Recheck that source before issuing the one replay.
        if (_settingsService is not null)
        {
            var settings = await _settingsService.GetRuntimeAsync(ct);
            _options = new IgdbProviderOptions { ClientId = settings.ClientId, ClientSecret = settings.ClientSecret };
            _enabled = settings.Enabled && settings.HasCredentials;
        }
        if (!IsConfigured)
            throw new HttpRequestException("IGDB authentication is unavailable.", null, HttpStatusCode.Unauthorized);

        await _rateLimiter.AcquireAsync(ProviderId, ct);
        authorization = await GetAccessTokenAsync(ct);
        response = await SendAttemptAsync(authorization);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            _accessTokens.Invalidate(authorization.Token);
        return response;

        async Task<HttpResponseMessage> SendAttemptAsync((string ClientId, IgdbAccessTokenCache.AccessToken Token) credentials)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{IgdbApiUrl}/{endpoint}")
            {
                Content = new StringContent(query)
            };
            request.Headers.Add("Client-ID", credentials.ClientId);
            request.Headers.Add("Authorization", $"Bearer {credentials.Token.Token}");
            return await _httpClient.SendAsync(request, completion, ct);
        }
    }
}
