using Romd.Mount.Credentials;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MountCredentialStoreTests
{
    [Fact]
    public void CredentialKey_IsStableAndSeparatesOriginInstanceAndProfile()
    {
        using var client = new MountIdentityClient(new Uri("https://server.example"));
        using var other = new MountIdentityClient(new Uri("https://other.example"));
        Guid instance = Guid.NewGuid();
        var bound = new BoundMountIdentity(client, instance);
        string key = new MountCredentialScope(bound, "profile-a").Key;
        key.ShouldBe(new MountCredentialScope(bound, "profile-a").Key);
        key.ShouldNotBe(new MountCredentialScope(bound, "profile-b").Key);
        key.ShouldNotBe(new MountCredentialScope(new BoundMountIdentity(client, Guid.NewGuid()), "profile-a").Key);
        key.ShouldNotBe(new MountCredentialScope(new BoundMountIdentity(other, instance), "profile-a").Key);
        key.ShouldNotContain("profile-a");
    }

    [MacKeychainFact]
    public async Task MacKeychain_SyntheticSecret_RoundTripsRotatesIsolatesAndDeletes()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        using var client = new MountIdentityClient(new Uri("https://keychain-probe.invalid"));
        var bound = new BoundMountIdentity(client, Guid.NewGuid());
        var scope = new MountCredentialScope(bound, "test-" + Guid.NewGuid().ToString("N"));
        var other = new MountCredentialScope(bound, "other-" + Guid.NewGuid().ToString("N"));
        var store = new MacKeychainCredentialStore();
        string initial = Guid.NewGuid().ToString("N"), rotated = Guid.NewGuid().ToString("N");
        try
        {
            (await store.ReadAsync(scope)).ShouldBeNull();
            await store.WriteAsync(scope, initial);
            (await store.ReadAsync(scope) == initial).ShouldBeTrue();
            (await store.ReadAsync(other)).ShouldBeNull();
            await store.WriteAsync(scope, rotated);
            (await store.ReadAsync(scope) == rotated).ShouldBeTrue();
            await store.DeleteAsync(scope);
            (await store.ReadAsync(scope)).ShouldBeNull();
            await store.DeleteAsync(scope);
        }
        finally { await store.DeleteAsync(scope); }
    }

    public sealed class MacKeychainFactAttribute : FactAttribute
    {
        public MacKeychainFactAttribute()
        {
            if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("ROMD_MOUNT_KEYCHAIN_TEST") != "1")
                Skip = "Set ROMD_MOUNT_KEYCHAIN_TEST=1 on macOS to create and remove an isolated synthetic keychain item.";
        }
    }
}
