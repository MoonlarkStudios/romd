namespace Romd.Mount;

/// <summary>
/// An instance-bound identity session. Rechecks discovery before each credential-bearing
/// exchange. The owner keeps the underlying HTTP client alive and disposes it afterward.
/// This detects instance replacement; it is not certificate pinning or an atomic server lease.
/// </summary>
public sealed class BoundMountIdentity
{
    private readonly MountIdentityClient _client;
    public Guid InstanceId { get; }
    public Uri Origin => _client.Origin;

    internal BoundMountIdentity(MountIdentityClient client, Guid instanceId)
    {
        _client = client;
        InstanceId = instanceId;
    }

    public async Task<DeviceLogin> BeginLoginAsync(CancellationToken ct = default)
    {
        await CheckAsync(ct);
        return await _client.BeginLoginAsync(ct);
    }

    public async Task<MountTokens> PollLoginAsync(string deviceCode, CancellationToken ct = default)
    {
        await CheckAsync(ct);
        return await _client.PollLoginAsync(deviceCode, ct);
    }

    public async Task<MountTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        await CheckAsync(ct);
        return await _client.RefreshAsync(refreshToken, ct);
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        if (await _client.DiscoverInstanceAsync(ct) != InstanceId)
            throw new MountIdentityException(IdentityFailure.ServerChanged);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        await CheckAsync(ct);
        await _client.RevokeAsync(refreshToken, ct);
    }
}
