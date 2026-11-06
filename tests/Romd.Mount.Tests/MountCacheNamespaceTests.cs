using Romd.Mount.Cli;
using Romd.Mount.Credentials;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MountCacheNamespaceTests
{
    [TokenCommandTests.UnixFact]
    public void Rotation_IsGlobalToCredentialScopeAndNeverReusesPreviousCache()
    {
        string root = PrivateStateDirectory.Ensure(Path.Combine(Path.GetTempPath(), "romd-namespace-" + Guid.NewGuid().ToString("N")));
        try
        {
            string registry = Path.Combine(root, "registry");
            string scope = new('a', 64);
            string first = MountCacheNamespace.GetOrCreateKey(registry, scope);
            first.ShouldNotBe(scope); // Legacy profile-only cache must never be selected.
            MountCacheNamespace.GetOrCreateKey(registry, scope).ShouldBe(first);
            foreach (string name in new[] { "state-one", "state-two" })
            {
                string state = PrivateStateDirectory.Ensure(Path.Combine(root, name));
                string parent = PrivateStateDirectory.Ensure(Path.Combine(state, "cache"));
                string profile = PrivateStateDirectory.Ensure(Path.Combine(parent, scope));
                string oldCache = PrivateStateDirectory.Ensure(Path.Combine(profile, first));
                File.WriteAllText(Path.Combine(oldCache, "previous-account-content"), "old bytes");
            }
            string second = MountCacheNamespace.Rotate(registry, scope);
            second.ShouldNotBe(first);
            MountCacheNamespace.GetOrCreateKey(registry, scope).ShouldBe(second);
            foreach (string name in new[] { "state-one", "state-two" })
            {
                string state = Path.Combine(root, name);
                CacheInfo.Read(state, scope, default, generation: second).Files.ShouldBe(0);
                CacheInfo.Read(state, scope, default).Files.ShouldBe(1);
                File.ReadAllText(Path.Combine(state, "cache", scope, first, "previous-account-content")).ShouldBe("old bytes");
            }
            // Lost metadata must select a fresh cache, never fall back to an old namespace.
            File.Delete(Path.Combine(registry, scope + ".txt"));
            string third = MountCacheNamespace.GetOrCreateKey(registry, scope);
            third.ShouldNotBe(first);
            third.ShouldNotBe(second);
            Directory.GetFiles(registry).Length.ShouldBe(1);
            MountCacheNamespace.GetOrCreateKey(registry, new string('b', 64)).ShouldNotBe(third);
        }
        finally { Directory.Delete(root, true); }
    }

    [TokenCommandTests.UnixFact]
    public void InvalidOrLinkedRecord_FailsClosed()
    {
        string root = PrivateStateDirectory.Ensure(Path.Combine(Path.GetTempPath(), "romd-namespace-" + Guid.NewGuid().ToString("N")));
        string scope = new('a', 64);
        string record = Path.Combine(root, scope + ".txt");
        try
        {
            foreach (string bad in new[] { "", new string('x', 64), new string('a', 65) })
            {
                File.WriteAllText(record, bad);
                Should.Throw<IOException>(() => MountCacheNamespace.GetOrCreateKey(root, scope));
            }
            File.Delete(record);
            File.CreateSymbolicLink(record, Path.Combine(root, "absent"));
            Should.Throw<IOException>(() => MountCacheNamespace.GetOrCreateKey(root, scope));
            Should.Throw<IOException>(() => MountCacheNamespace.Rotate(root, scope));
        }
        finally { Directory.Delete(root, true); }
    }
}
