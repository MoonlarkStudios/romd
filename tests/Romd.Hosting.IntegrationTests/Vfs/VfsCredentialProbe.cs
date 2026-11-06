using Romd.Mount;
using Romd.Mount.Credentials;

namespace Romd.Hosting.IntegrationTests.Vfs;

/// <summary>Owns one disposable synthetic credential scope, never an existing user profile.</summary>
internal sealed class VfsCredentialProbe(IMountCredentialStore store, MountCredentialScope scope,
    MountProfileLease lease, string lockDirectory) : IAsyncDisposable
{
    public IMountCredentialStore Store { get; } = store;
    public MountCredentialScope Scope { get; } = scope;

    public static async Task<VfsCredentialProbe> CreateAsync(BoundMountIdentity identity, string refresh, bool keychain)
    {
        IMountCredentialStore store;
        if (keychain)
        {
            if (!OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException();
            store = new MacKeychainCredentialStore();
        }
        else
            store = new MemoryStore();
        var scope = new MountCredentialScope(identity, "integration-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(Path.GetTempPath(), "romd-credential-probe-" + Guid.NewGuid().ToString("N"));
        var lease = MountProfileLease.Acquire(directory, scope.Key);
        var probe = new VfsCredentialProbe(store, scope, lease, directory);
        try
        { await store.WriteAsync(scope, refresh); return probe; }
        catch { await probe.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        { await Store.DeleteAsync(Scope); }
        finally
        {
            lease.Dispose();
            Directory.Delete(lockDirectory, true);
        }
    }

    private sealed class MemoryStore : IMountCredentialStore
    {
        private string? _value;
        public Task<string?> ReadAsync(MountCredentialScope scope, CancellationToken ct = default) => Task.FromResult(_value);
        public Task WriteAsync(MountCredentialScope scope, string refreshToken, CancellationToken ct = default)
        { _value = refreshToken; return Task.CompletedTask; }
        public Task DeleteAsync(MountCredentialScope scope, CancellationToken ct = default)
        { _value = null; return Task.CompletedTask; }
    }
}
