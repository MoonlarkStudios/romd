namespace Romd.Mount.Credentials;

/// <summary>
/// Single-owner token broker. All requests for one running mount must share this instance.
/// Cross-process mount exclusion must be supplied by the mount supervisor.
/// </summary>
public sealed class MountTokenSession
{
    private readonly BoundMountIdentity _identity;
    private readonly MountCredentialScope _scope;
    private readonly IMountCredentialStore _store;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1);
    private MountTokens? _tokens;
    private bool _needsPersistence;
    private long _receivedAt;

    public MountTokenSession(BoundMountIdentity identity, MountCredentialScope scope, IMountCredentialStore store,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(store);
        if (identity.InstanceId != scope.InstanceId || identity.Origin != scope.Origin)
            throw new ArgumentException("Credential scope does not match the bound server.", nameof(scope));
        _identity = identity;
        _scope = scope;
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns a cached valid token or renews once. Pass the rejected token after a 401;
    /// overlapping rejections of the same token reuse the replacement rather than rotating again.
    /// A rotated token is never exposed until its refresh token has been persisted successfully.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(string? rejectedAccessToken = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Remember rejection even if the following discovery attempt is offline.
            if (!_needsPersistence && _tokens?.AccessToken == rejectedAccessToken) _tokens = null;
            await _identity.CheckAsync(ct);
            await PersistAsync(ct);
            if (_tokens is not null && _tokens.AccessToken != rejectedAccessToken &&
                _clock.GetElapsedTime(_receivedAt).TotalSeconds < Math.Max(1, _tokens.ExpiresInSeconds - 30))
                return _tokens.AccessToken;
            if (_tokens?.AccessToken == rejectedAccessToken) _tokens = null;

            string? refresh = await _store.ReadAsync(_scope, ct);
            if (refresh is null) throw new MountIdentityException(IdentityFailure.SignInRequired);
            // Start the lifetime before the network call so slow refreshes cannot extend it.
            long started = _clock.GetTimestamp();
            var replacement = await _identity.RefreshAsync(refresh, ct);
            _tokens = replacement;
            _receivedAt = started;
            _needsPersistence = true;
            await PersistAsync(ct);
            if (_clock.GetElapsedTime(_receivedAt).TotalSeconds >= replacement.ExpiresInSeconds)
                throw new MountIdentityException(IdentityFailure.Expired);
            return replacement.AccessToken;
        }
        finally { _gate.Release(); }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (!_needsPersistence) return;
        await _store.WriteAsync(_scope, _tokens!.RefreshToken, ct);
        _needsPersistence = false;
    }
}
