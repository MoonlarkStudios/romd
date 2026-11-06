using Romd.Mount.Cli;
using Shouldly;
using Xunit;

namespace Romd.Mount.Tests;

public sealed class MacCacheCleanupTests
{
    [MacStateAclTests.MacAclFact]
    public void Clear_DirectoryReplacedAfterPreflight_DoesNotFollowReplacement()
    {
        string state = NewState();
        string key = new('a', 64);
        try
        {
            string profile = Profile(state, key);
            string folder = Directory.CreateDirectory(Path.Combine(profile, "content")).FullName;
            File.WriteAllText(Path.Combine(folder, "cached"), "old cache");
            string foreign = Directory.CreateDirectory(Path.Combine(state, "foreign")).FullName;
            File.WriteAllText(Path.Combine(foreign, "cached"), "preserve foreign bytes");
            string parked = Path.Combine(state, "parked");
            Should.Throw<IOException>(() => MacDirectoryCleanup.ClearCache(state, key, default, beforeRemoval: () =>
            {
                Directory.Move(folder, parked);
                Directory.CreateSymbolicLink(folder, foreign);
            }));
            File.ReadAllText(Path.Combine(foreign, "cached")).ShouldBe("preserve foreign bytes");
            File.ReadAllText(Path.Combine(parked, "cached")).ShouldBe("old cache");
            Directory.Delete(folder);
        }
        finally { Directory.Delete(state, true); }
    }

    [MacStateAclTests.MacAclFact]
    public void Clear_RemovesAllSelectedGenerationsAndPreservesOtherProfiles()
    {
        string state = NewState();
        string key = new('a', 64);
        try
        {
            MacDirectoryCleanup.ClearCache(state, key, default).ShouldBe(0);
            Directory.Exists(Path.Combine(state, "cache")).ShouldBeFalse();
            string profile = Profile(state, key);
            foreach (string generation in new[] { "old", "current" })
            {
                string folder = Directory.CreateDirectory(Path.Combine(profile, generation)).FullName;
                File.WriteAllText(Path.Combine(folder, "é content"), "test");
            }
            string other = Profile(state, new string('b', 64));
            File.WriteAllText(Path.Combine(other, "keep"), "other profile");
            File.WriteAllText(Path.Combine(state, "metadata"), "binding");
            MacDirectoryCleanup.ClearCache(state, key, default).ShouldBe(4);
            Directory.EnumerateFileSystemEntries(profile).ShouldBeEmpty();
            File.ReadAllText(Path.Combine(other, "keep")).ShouldBe("other profile");
            File.ReadAllText(Path.Combine(state, "metadata")).ShouldBe("binding");
            MacDirectoryCleanup.ClearCache(state, key, default).ShouldBe(0);
        }
        finally { Directory.Delete(state, true); }
    }

    [MacStateAclTests.MacAclFact]
    public void Clear_PreflightRejectsLinksLimitsAndCancellationWithoutRemovingFiles()
    {
        string state = NewState();
        string key = new('a', 64);
        try
        {
            string profile = Profile(state, key);
            string sentinel = Path.Combine(profile, "preserve");
            File.WriteAllText(sentinel, "bytes");
            string link = Path.Combine(profile, "link");
            Directory.CreateSymbolicLink(link, state);
            Should.Throw<IOException>(() => MacDirectoryCleanup.ClearCache(state, key, default));
            File.ReadAllText(sentinel).ShouldBe("bytes");
            Directory.Delete(link);
            Directory.CreateDirectory(Path.Combine(profile, "nested"));
            Should.Throw<IOException>(() => MacDirectoryCleanup.ClearCache(state, key, default, maximumEntries: 1));
            Should.Throw<IOException>(() => MacDirectoryCleanup.ClearCache(state, key, default, maximumDepth: 0));
            Should.Throw<OperationCanceledException>(() => MacDirectoryCleanup.ClearCache(state, key, new CancellationToken(true)));
            File.ReadAllText(sentinel).ShouldBe("bytes");
            Directory.Delete(profile, true);
            Directory.CreateSymbolicLink(profile, Path.Combine(state, "missing"));
            Should.Throw<IOException>(() => MacDirectoryCleanup.ClearCache(state, key, default));
            File.Delete(profile);
        }
        finally { Directory.Delete(state, true); }
    }

    private static string Profile(string state, string key)
    {
        string parent = PrivateStateDirectory.Ensure(Path.Combine(state, "cache"));
        return PrivateStateDirectory.Ensure(Path.Combine(parent, key));
    }

    private static string NewState() => PrivateStateDirectory.Ensure(Path.Combine(Path.GetTempPath(), "romd-clear-" + Guid.NewGuid().ToString("N")));
}
