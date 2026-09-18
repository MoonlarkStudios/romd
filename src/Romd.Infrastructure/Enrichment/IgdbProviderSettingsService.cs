using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;

namespace Romd.Infrastructure.Enrichment;

/// <summary>Stores only protected secrets. Plaintext is held only in a scoped runtime snapshot.</summary>
public sealed class IgdbProviderSettingsService : IIgdbProviderSettingsService
{
    private readonly IIgdbProviderSettingsStore _store;
    private readonly IDataProtector _protector;
    private readonly IgdbProviderOptions _deployment;
    private readonly EnrichmentOptions _enrichment;
    private readonly IHttpClientFactory _httpClients;
    private readonly TimeProvider _time;

    public IgdbProviderSettingsService(IIgdbProviderSettingsStore store, IDataProtectionProvider protection,
        IOptions<IgdbProviderOptions> deployment, IOptions<EnrichmentOptions> enrichment,
        IHttpClientFactory httpClients, TimeProvider time)
    {
        _store = store;
        _protector = protection.CreateProtector("romd.metadata-providers.igdb.credentials.v1");
        _deployment = deployment.Value;
        _enrichment = enrichment.Value;
        _httpClients = httpClients;
        _time = time;
    }

    private bool Managed => !string.IsNullOrWhiteSpace(_deployment.ClientId) ||
                            !string.IsNullOrWhiteSpace(_deployment.ClientSecret);

    public async Task<IgdbProviderSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var row = await LoadAsync(ct);
        return ToDto(row, Resolve(row));
    }

    internal async Task<IgdbRuntimeSettings> GetRuntimeAsync(CancellationToken ct = default) =>
        Resolve(await LoadAsync(ct));

    public async Task<ErrorOr<IgdbProviderSettingsDto>> UpdateAsync(
        UpdateIgdbProviderSettingsRequest request, CancellationToken ct = default)
    {
        if (Managed)
        {
            return MetadataProviderErrors.ManagedByDeployment();
        }

        string? clientId = string.IsNullOrWhiteSpace(request.ClientId) ? null : request.ClientId.Trim();
        string? secret = string.IsNullOrWhiteSpace(request.ClientSecret) ? null : request.ClientSecret;
        if (clientId is { Length: > 256 } || clientId?.Any(char.IsControl) == true ||
            secret is { Length: > 4096 } || secret?.Any(char.IsControl) == true)
        {
            return MetadataProviderErrors.InvalidCredentials();
        }
        if (request.ClearClientSecret && secret is not null)
        {
            return MetadataProviderErrors.ConflictingSecretUpdate();
        }

        var row = await LoadAsync(ct);
        if (request.Revision != row.Revision) return MetadataProviderErrors.ConcurrentUpdate();
        if (clientId != row.ClientId && row.ProtectedClientSecret is not null && secret is null && !request.ClearClientSecret)
        {
            return MetadataProviderErrors.ReplacementRequiredForClientId();
        }
        string? protectedSecret = request.ClearClientSecret ? null :
            secret is null ? row.ProtectedClientSecret : _protector.Protect(secret);
        if (request.Enabled && (clientId is null || protectedSecret is null))
        {
            return MetadataProviderErrors.CredentialsRequired();
        }
        if (request.Enabled && secret is null && Resolve(row).ConfigurationError is not null)
        {
            return MetadataProviderErrors.StoredSecretReplacementRequired();
        }

        bool updated = await _store.TryUpdateAsync(row.Revision, row with
        {
            Enabled = request.Enabled,
            ClientId = clientId,
            ProtectedClientSecret = protectedSecret,
            Revision = Guid.NewGuid()
        }, ct);
        if (!updated)
        {
            return MetadataProviderErrors.ConcurrentUpdate();
        }
        return await GetAsync(ct);
    }

    public async Task<IgdbProviderSettingsDto> TestConnectionAsync(CancellationToken ct = default)
    {
        var row = await LoadAsync(ct);
        var settings = Resolve(row);
        DateTimeOffset testedAt = _time.GetUtcNow();
        bool succeeded = false;
        string message;
        if (!settings.HasCredentials)
        {
            message = settings.ConfigurationError ?? "Set both the Twitch Client ID and Client Secret before testing IGDB.";
        }
        else
        {
            (succeeded, message) = await ProbeAsync(settings, ct);
        }

        string fingerprint = Fingerprint(settings);
        await _store.RecordTestAsync(row.Revision, testedAt, succeeded, message, fingerprint, ct);
        return await GetAsync(ct);
    }

    private async Task<(bool Success, string Message)> ProbeAsync(IgdbRuntimeSettings settings, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var client = _httpClients.CreateClient("IgdbConnectionTest");
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = settings.ClientId!,
                ["client_secret"] = settings.ClientSecret!,
                ["grant_type"] = "client_credentials"
            });
            using var tokenResponse = await client.PostAsync("https://id.twitch.tv/oauth2/token", form, timeout.Token);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                return (false, FailureMessage(tokenResponse.StatusCode));
            }
            using var tokenJson = await JsonDocument.ParseAsync(
                await tokenResponse.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (tokenJson.RootElement.ValueKind != JsonValueKind.Object ||
                !tokenJson.RootElement.TryGetProperty("access_token", out var accessToken) ||
                accessToken.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(accessToken.GetString()))
            {
                return (false, "Twitch returned an invalid authentication response.");
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
            {
                Content = new StringContent("fields id; limit 1;")
            };
            request.Headers.Add("Client-ID", settings.ClientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.GetString());
            using var response = await client.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode
                ? (true, "Connected to IGDB successfully.")
                : (false, FailureMessage(response.StatusCode));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "IGDB connection test timed out. Try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException)
        {
            // Never persist or log provider bodies, exceptions, credentials, or tokens.
            return (false, "Could not connect to IGDB. Check the credentials and network connection, then try again.");
        }
    }

    private static string FailureMessage(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest =>
            "IGDB authentication failed. Check the Twitch Client ID and Client Secret.",
        HttpStatusCode.TooManyRequests => "IGDB rate limit reached. Try again later.",
        _ => "IGDB is unavailable. Try again later."
    };

    private Task<IgdbProviderStoredSettings> LoadAsync(CancellationToken ct) => _store.LoadAsync(ct);

    private IgdbRuntimeSettings Resolve(IgdbProviderStoredSettings row)
    {
        bool enabled = Managed ? _enrichment.IsProviderEnabled("igdb") : row.Enabled;
        string? clientId = Managed ? _deployment.ClientId : row.ClientId;
        string? secret = Managed ? _deployment.ClientSecret : null;
        string? error = null;
        if (!Managed && row.ProtectedClientSecret is not null)
        {
            try
            {
                secret = _protector.Unprotect(row.ProtectedClientSecret);
            }
            catch (CryptographicException)
            {
                error = "The stored IGDB secret cannot be decrypted. Restore the data-protection keys or replace the secret.";
            }
        }
        if (error is null && (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret)))
        {
            error = Managed
                ? "Deployment configuration must supply both IGDB Client ID and Client Secret."
                : "Set both the Twitch Client ID and Client Secret to configure IGDB.";
        }
        return new IgdbRuntimeSettings(enabled, clientId, secret, error);
    }

    private IgdbProviderSettingsDto ToDto(IgdbProviderStoredSettings row, IgdbRuntimeSettings settings)
    {
        bool sameTestConfiguration = row.TestConfigurationFingerprint == Fingerprint(settings);
        return new(settings.Enabled, settings.ClientId,
            Managed ? !string.IsNullOrWhiteSpace(_deployment.ClientSecret) : row.ProtectedClientSecret is not null,
            settings.HasCredentials, Managed, settings.ConfigurationError,
            sameTestConfiguration ? row.LastTestedAt : null,
            sameTestConfiguration ? row.LastTestSucceeded : null,
            sameTestConfiguration ? row.LastTestMessage : null) { Revision = row.Revision };
    }

    private static string Fingerprint(IgdbRuntimeSettings settings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{settings.Enabled}\0{settings.ClientId}\0{settings.ClientSecret}\0{settings.ConfigurationError}")));
}

internal sealed class IgdbRuntimeSettings(bool enabled, string? clientId, string? clientSecret, string? configurationError)
{
    public bool Enabled { get; } = enabled;
    public string? ClientId { get; } = clientId;
    public string? ClientSecret { get; } = clientSecret;
    public string? ConfigurationError { get; } = configurationError;
    public bool HasCredentials => ConfigurationError is null &&
                                  !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    public override string ToString() => nameof(IgdbRuntimeSettings);
}
