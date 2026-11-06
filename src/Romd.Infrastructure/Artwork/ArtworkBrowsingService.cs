using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.MetadataProviders;

namespace Romd.Infrastructure.Artwork;

/// <summary>Transient title/user-bound browsing references; no permanent association or media retention.</summary>
public sealed class ArtworkBrowsingService : IArtworkBrowsingService
{
    private readonly IReadOnlyDictionary<string, IArtworkProviderBrowser> _browsers;
    private readonly IArtworkAssetSource _source;
    private readonly IArtworkImageProcessor _processor;
    private readonly ArtworkPreviewCache _cache;
    private readonly TimeProvider _time;
    private readonly IDataProtector _candidateProtector;
    private readonly IDataProtector _cursorProtector;
    private readonly ITitleProviderMatchService? _matches;
    private readonly Romd.Admin.Application.Titles.ITitleMediaReader? _titleMedia;
    private readonly IArtworkCurationRepository? _curation;
    private readonly IArtworkAcquisitionStore? _acquisition;

    public ArtworkBrowsingService(IEnumerable<IArtworkProviderBrowser> browsers, IArtworkAssetSource source,
        IArtworkImageProcessor processor, IDataProtectionProvider protection, ArtworkPreviewCache cache, TimeProvider time,
        ITitleProviderMatchService? matches = null, Romd.Admin.Application.Titles.ITitleMediaReader? titleMedia = null,
        IArtworkCurationRepository? curation = null, IArtworkAcquisitionStore? acquisition = null)
    {
        _browsers = browsers.ToDictionary(browser => browser.Capabilities.ProviderId, StringComparer.Ordinal);
        _matches = matches;
        _titleMedia = titleMedia;
        _curation = curation;
        _acquisition = acquisition;
        _source = source;
        _processor = processor;
        _cache = cache;
        _time = time;
        _candidateProtector = protection.CreateProtector("ROMD.Artwork.Candidate.v1");
        _cursorProtector = protection.CreateProtector("ROMD.Artwork.Cursor.v1");
    }

    public async Task<IReadOnlyList<ArtworkBrowsingCapabilities>> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        var result = new List<ArtworkBrowsingCapabilities>();
        foreach (var browser in _browsers.Values)
            result.Add(new(browser.Capabilities, await browser.IsAvailableAsync(ct)));
        return result;
    }

    public async Task<ErrorOr<ArtworkCandidatePage>> BrowseAsync(int titleId, Guid adminUserId, string providerId,
        string gameId, ProviderArtworkQuery query, string? cursor = null, CancellationToken ct = default)
    {
        if (!ValidScope(titleId, adminUserId, providerId))
            return ArtworkProviderErrors.InvalidRequest();
        var revision = _matches is null ? null : await _matches.GetLinkRevisionAsync(titleId, providerId, gameId, ct);
        if (_matches is not null && revision is null)
            return ProviderMatchErrors.Conflict;
        var fingerprint = Fingerprint(providerId, gameId, query);
        var page = 0;
        if (cursor is not null)
        {
            var decoded = Unprotect<CursorPayload>(_cursorProtector, cursor);
            if (decoded is null || decoded.TitleId != titleId || decoded.UserId != adminUserId ||
                decoded.ExpiresAt <= _time.GetUtcNow() || decoded.Fingerprint != fingerprint || decoded.Page < 0)
                return ArtworkProviderErrors.InvalidRequest();
            page = decoded.Page;
        }
        if (!await _browsers[providerId].IsAvailableAsync(ct))
            return ArtworkProviderErrors.NotConfigured();
        var result = await _browsers[providerId].GetCandidatesAsync(gameId, query, page, ct);
        if (result.IsError)
            return result.Errors;
        HashSet<string> mediaSourceUrls = _titleMedia is null ? [] : (await _titleMedia.ListAsync(titleId, ct))
            .Select(media => media.SourceUrl).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var expires = _time.GetUtcNow().AddMinutes(10);
        var excluded = _curation is null ? [] : await _curation.GetExcludedUrlsAsync(titleId, ct);
        var rejected = _acquisition is null ? [] : await _acquisition.GetRejectedCandidatesAsync(titleId, query.Role, providerId, gameId, ct);
        var items = result.Value.Items.Where(candidate => !excluded.Contains(candidate.AssetUrl.AbsoluteUri) && !rejected.Contains(candidate.ProviderAssetId)).Select(candidate =>
        {
            var trusted = new TrustedArtworkCandidate(providerId, candidate.ProviderGameId, candidate.ProviderAssetId,
                candidate.Role, candidate.AssetUrl.AbsoluteUri, candidate.PreviewUrl.AbsoluteUri,
                candidate.Attribution, candidate.SourcePageUrl, candidate.MediaType);
            var reference = _candidateProtector.Protect(JsonSerializer.Serialize(
                new CandidatePayload(titleId, adminUserId, expires, trusted, revision)));
            return new ArtworkCandidatePreview(reference, ArtworkCandidateIds.Of(trusted.TrustedAssetUrl),
                candidate.ProviderAssetId, candidate.Width,
                candidate.Height, candidate.Style, candidate.Attribution, candidate.Role, candidate.SourcePageUrl, candidate.MediaType,
                mediaSourceUrls.Contains(candidate.AssetUrl.AbsoluteUri));
        }).ToArray();
        var nextCursor = result.Value.NextPage is { } next
            ? _cursorProtector.Protect(JsonSerializer.Serialize(new CursorPayload(titleId, adminUserId, expires, fingerprint, next)))
            : null;
        return new ArtworkCandidatePage(items, nextCursor);
    }

    public async Task<ErrorOr<TrustedArtworkCandidate>> ValidateCandidateAsync(int titleId, Guid adminUserId,
        string candidateReference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var payload = Unprotect<CandidatePayload>(_candidateProtector, candidateReference);
        if (payload is null || payload.Candidate is null || payload.TitleId != titleId || payload.UserId != adminUserId ||
            payload.ExpiresAt <= _time.GetUtcNow() || !ValidScope(titleId, adminUserId, payload.Candidate.ProviderId))
            return ArtworkProviderErrors.InvalidRequest();
        if (!await _browsers[payload.Candidate.ProviderId].IsAvailableAsync(ct))
            return ArtworkProviderErrors.NotConfigured();
        if (_matches is not null && (payload.Revision is null || payload.Revision !=
            await _matches.GetLinkRevisionAsync(titleId, payload.Candidate.ProviderId, payload.Candidate.GameId, ct)))
            return ProviderMatchErrors.Conflict;
        if (_curation is not null && (await _curation.GetExcludedUrlsAsync(titleId, ct)).Contains(payload.Candidate.TrustedAssetUrl))
            return ArtworkProviderErrors.InvalidRequest();
        if (_acquisition is not null && (await _acquisition.GetRejectedCandidatesAsync(titleId, payload.Candidate.Role,
            payload.Candidate.ProviderId, payload.Candidate.GameId, ct)).Contains(payload.Candidate.AssetId))
            return ArtworkProviderErrors.InvalidRequest();
        return payload.Candidate;
    }

    public async Task<ErrorOr<ArtworkPreviewImage>> PreviewAsync(int titleId, Guid adminUserId,
        string candidateReference, CancellationToken ct = default)
    {
        var result = await ValidateCandidateAsync(titleId, adminUserId, candidateReference, ct);
        if (result.IsError)
            return result.Errors;
        var candidate = result.Value;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(candidate))));
        await _cache.Gate.WaitAsync(ct);
        try
        {
            if (_cache.Get(key) is { } cached)
                return cached;
            var downloaded = await _source.DownloadPreviewAsync(candidate.ProviderId, candidate.Role, candidate.TrustedPreviewUrl, ct);
            if (downloaded.IsError)
                return downloaded.Errors;
            var processed = await _processor.ProcessAsync(downloaded.Value.Bytes, candidate.Role, ct);
            if (processed.IsError)
                return processed.Errors;
            var preview = processed.Value.Variants.FirstOrDefault(variant => variant.Name == "preview")
                ?? processed.Value.Variants.FirstOrDefault(variant => variant.Name == "thumb");
            if (preview is null)
                return ArtworkImageErrors.ProcessingFailed();
            var image = new ArtworkPreviewImage(preview.EncodedBytes, preview.ContentType);
            _cache.Put(key, image);
            return image;
        }
        finally { _cache.Gate.Release(); }
    }

    private bool ValidScope(int titleId, Guid userId, string providerId) =>
        titleId > 0 && userId != Guid.Empty && _browsers.ContainsKey(providerId);
    private static string Fingerprint(string providerId, string gameId, ProviderArtworkQuery query) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { providerId, gameId, query }))));
    private static T? Unprotect<T>(IDataProtector protector, string reference) where T : class
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 16000)
            return null;
        try
        {
            // Data-protection proofs have one Base64URL spelling; alternate encodings never become valid cursors.
            if (WebEncoders.Base64UrlEncode(WebEncoders.Base64UrlDecode(reference)) != reference)
                return null;
            return JsonSerializer.Deserialize<T>(protector.Unprotect(reference));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException)
        { return null; }
    }
    private sealed record CandidatePayload(int TitleId, Guid UserId, DateTimeOffset ExpiresAt, TrustedArtworkCandidate Candidate, Guid? Revision = null);
    private sealed record CursorPayload(int TitleId, Guid UserId, DateTimeOffset ExpiresAt, string Fingerprint, int Page);
}
