using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Artwork.Settings;
using Romd.Contracts.Management.Artwork;

namespace Romd.Infrastructure.Artwork;

public sealed class SteamGridDbSettingsService : ISteamGridDbSettingsService, ISteamGridDbCredentials
{
    public const string HttpClientName = "SteamGridDbConnectionTest";
    private readonly ISteamGridDbSettingsStore _store;
    private readonly IDataProtector _protector;
    private readonly SteamGridDbOptions _deployment;
    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _time;

    public SteamGridDbSettingsService(ISteamGridDbSettingsStore store, IDataProtectionProvider protection,
        IOptions<SteamGridDbOptions> deployment, IHttpClientFactory clients, TimeProvider time)
    {
        _store = store;
        _protector = protection.CreateProtector("romd.artwork.steamgriddb.credentials.v1");
        _deployment = deployment.Value;
        _clients = clients;
        _time = time;
    }

    private bool Managed => _deployment.ApiKey is not null;
    private static bool ValidKey(string? key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 4096 &&
        key.All(c => c is >= '!' and <= '~');

    public async Task<SteamGridDbSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var row = await _store.LoadAsync(ct);
        return ToDto(row, Resolve(row));
    }

    public async Task<string?> GetApiKeyAsync(CancellationToken ct = default)
    {
        var settings = Resolve(await _store.LoadAsync(ct));
        return settings.Enabled && settings.Error is null ? settings.Key : null;
    }

    public async Task<ErrorOr<SteamGridDbSettingsDto>> UpdateAsync(UpdateSteamGridDbSettingsRequest request, CancellationToken ct = default)
    {
        if (Managed) return SteamGridDbSettingsErrors.ManagedByDeployment();
        string? key = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey;
        if (key is not null && !ValidKey(key)) return SteamGridDbSettingsErrors.InvalidApiKey();
        if (request.ClearApiKey && key is not null) return SteamGridDbSettingsErrors.ConflictingUpdate();
        var row = await _store.LoadAsync(ct);
        if (request.Revision != row.Revision) return SteamGridDbSettingsErrors.ConcurrentUpdate();
        string? protectedKey = request.ClearApiKey ? null : key is null ? row.ProtectedClientSecret : _protector.Protect(key);
        var updated = row with { Enabled = request.Enabled, ProtectedClientSecret = protectedKey, Revision = Guid.NewGuid() };
        if (request.Enabled && Resolve(updated).Error is not null) return SteamGridDbSettingsErrors.ApiKeyRequired();
        if (!await _store.TryUpdateAsync(request.Revision, updated, ct)) return SteamGridDbSettingsErrors.ConcurrentUpdate();
        return await GetAsync(ct);
    }

    public async Task<SteamGridDbSettingsDto> TestConnectionAsync(CancellationToken ct = default)
    {
        var row = await _store.LoadAsync(ct);
        var settings = Resolve(row);
        var testedAt = _time.GetUtcNow();
        var result = settings.Error is not null ? (false, settings.Error) : await ProbeAsync(settings.Key!, ct);
        await _store.RecordTestAsync(row.Revision, testedAt, result.Item1, result.Item2, Fingerprint(settings), ct);
        return await GetAsync(ct);
    }

    private async Task<(bool, string)> ProbeAsync(string key, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var client = _clients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.steamgriddb.com/api/v2/search/autocomplete/mario");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return (false, response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "SteamGridDB authentication failed. Check the API key.",
                HttpStatusCode.TooManyRequests => "SteamGridDB rate limit reached. Try again later.",
                _ => "SteamGridDB is unavailable. Try again later."
            });
            const int maximumBytes = 256 * 1024;
            if (response.Content.Headers.ContentLength > maximumBytes) return InvalidResponse();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                if (body.Length + read > maximumBytes) return InvalidResponse();
                body.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.True && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? (true, "Connected to SteamGridDB successfully.") : InvalidResponse();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "SteamGridDB connection test timed out. Try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or FormatException)
        {
            return (false, "Could not connect to SteamGridDB. Check the API key and network connection, then try again.");
        }
    }

    private static (bool, string) InvalidResponse() => (false, "SteamGridDB returned an invalid response.");

    private RuntimeSettings Resolve(SteamGridDbStoredSettings row)
    {
        bool enabled = Managed ? _deployment.Enabled : row.Enabled;
        string? key = Managed ? _deployment.ApiKey : null;
        string? error = null;
        if (!Managed && row.ProtectedClientSecret is not null)
        {
            try { key = _protector.Unprotect(row.ProtectedClientSecret); }
            catch (CryptographicException)
            {
                error = "The stored SteamGridDB API key cannot be decrypted. Restore the data-protection keys or replace the API key.";
            }
        }
        if (error is null && !ValidKey(key)) error = Managed
            ? "Deployment configuration must supply a valid SteamGridDB API key."
            : "Set a valid API key to configure SteamGridDB.";
        return new(enabled, key, error);
    }

    private SteamGridDbSettingsDto ToDto(SteamGridDbStoredSettings row, RuntimeSettings settings)
    {
        bool same = row.TestConfigurationFingerprint == Fingerprint(settings);
        return new(row.Revision, settings.Enabled, Managed ? !string.IsNullOrWhiteSpace(_deployment.ApiKey) : row.ProtectedClientSecret is not null,
            settings.Error is null, Managed, settings.Error, same ? row.LastTestedAt : null,
            same ? row.LastTestSucceeded : null, same ? row.LastTestMessage : null);
    }

    private static string Fingerprint(RuntimeSettings settings) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{settings.Enabled}\0{settings.Key}\0{settings.Error}")));

    private sealed class RuntimeSettings(bool enabled, string? key, string? error)
    {
        public bool Enabled { get; } = enabled;
        public string? Key { get; } = key;
        public string? Error { get; } = error;
        public override string ToString() => nameof(RuntimeSettings);
    }
}
