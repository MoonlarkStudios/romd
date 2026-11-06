using Moonlark.Native.Engineering.Core;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.TestSupport;

/// <summary>Fixture Git commands and cleanup preserve exact source bytes and external link targets.</summary>
public sealed class TestRepositoryTests
{
    /// <summary>Fixture checkout ignores host newline preferences without changing the pinned source contract.</summary>
    [Fact]
    public void CheckoutPreservesPinnedBytesUnderCrLfDefaults()
    {
        using var directory = new TemporaryDirectory();
        (string source, LibchdrPin pin) = SyntheticSource.Create(directory.Path);
        string implementation = Path.Combine(source, "implementation.c");
        byte[] expected = File.ReadAllBytes(implementation);
        CrLfDefaults(source);
        File.Delete(implementation);
        TestRepository.Git(source, "checkout", "HEAD", "--", "implementation.c");
        Assert.Equal(expected, File.ReadAllBytes(implementation));
        File.Delete(implementation);
        _ = ProcessRunner.Run(["git", "-C", source, "checkout", "HEAD", "--", "implementation.c"], TestRepository.CleanEnvironment).Value;
        Assert.Equal(expected, File.ReadAllBytes(implementation));
        Result<long> identity = SourceIdentity.Verify(source, pin, TestRepository.CleanEnvironment);
        Assert.True(identity.Succeeded, identity.Succeeded ? "" : identity.Failure.Message);
    }

    /// <summary>Fixture staging preserves mixed text newlines and binary bytes in Git objects.</summary>
    [Fact]
    public void AddPreservesTextAndBinaryBlobBytesUnderCrLfDefaults()
    {
        using var directory = new TemporaryDirectory();
        var (source, _) = SyntheticSource.Create(directory.Path);
        CrLfDefaults(source);
        byte[] text = "first\r\nsecond\n"u8.ToArray();
        byte[] binary = [0, 13, 10, 128, 255, 10];
        File.WriteAllBytes(Path.Combine(source, "mixed.txt"), text);
        File.WriteAllBytes(Path.Combine(source, "binary.dat"), binary);
        TestRepository.Git(source, "add", "mixed.txt", "binary.dat");
        Assert.Equal(Digest.GitBlobSha1(text), TestRepository.Git(source, "rev-parse", ":mixed.txt"));
        Assert.Equal(Digest.GitBlobSha1(binary), TestRepository.Git(source, "rev-parse", ":binary.dat"));
        Assert.Equal(text, File.ReadAllBytes(Path.Combine(source, "mixed.txt")));
        Assert.Equal(binary, File.ReadAllBytes(Path.Combine(source, "binary.dat")));
    }

    /// <summary>A Git-clean newline conversion is still rejected by the unchanged exact-byte source check.</summary>
    [Fact]
    public void SourceIdentityRejectsCrLfBytesEvenWhenGitStatusIsClean()
    {
        using var directory = new TemporaryDirectory();
        (string source, LibchdrPin pin) = SyntheticSource.Create(directory.Path);
        CrLfDefaults(source);
        string implementation = Path.Combine(source, "implementation.c");
        File.Delete(implementation);
        _ = ProcessRunner.Run(["git", "-C", source, "checkout", "HEAD", "--", "implementation.c"], TestRepository.CleanEnvironment).Value;
        Assert.Equal("int checked_source = 1;\r\n"u8.ToArray(), File.ReadAllBytes(implementation));
        Assert.Equal("", ProcessRunner.Run(["git", "-C", source, "status", "--porcelain"], TestRepository.CleanEnvironment).Value);
        Result<long> identity = SourceIdentity.Verify(source, pin, TestRepository.CleanEnvironment);
        Assert.False(identity.Succeeded);
        Assert.Contains("Tracked source bytes", identity.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Cleanup removes actual read-only Git objects and never changes external file or directory links.</summary>
    [Fact]
    public void CleanupRemovesReadOnlyGitObjectsWithoutFollowingLinks()
    {
        using var outside = new TemporaryDirectory();
        string target = Path.Combine(outside.Path, "retained.bin");
        byte[] retained = [0, 1, 13, 10, 255];
        File.WriteAllBytes(target, retained);
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        FileAttributes attributes = File.GetAttributes(target);
        var owned = new TemporaryDirectory();
        string[] objects = [];
        try
        {
            var (source, _) = SyntheticSource.Create(owned.Path);
            objects = Directory.GetFiles(Path.Combine(source, ".git", "objects"), "*", SearchOption.AllDirectories);
            Assert.NotEmpty(objects);
            foreach (string file in objects)
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            Directory.CreateSymbolicLink(Path.Combine(owned.Path, "external-directory"), outside.Path);
            File.CreateSymbolicLink(Path.Combine(owned.Path, "external-file"), target);
            owned.Dispose();
            Assert.False(Directory.Exists(owned.Path));
            Assert.Equal(attributes, File.GetAttributes(target));
            Assert.Equal(retained, File.ReadAllBytes(target));
        }
        finally
        {
            foreach (string file in objects.Where(File.Exists))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            if (Directory.Exists(owned.Path))
                Directory.Delete(owned.Path, recursive: true);
            File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>A replaced temp root is removed as a link without clearing attributes in its external target.</summary>
    [Fact]
    public void CleanupDoesNotTraverseAReplacedRootLink()
    {
        using var outside = new TemporaryDirectory();
        string target = Path.Combine(outside.Path, "retained.bin");
        File.WriteAllText(target, "external bytes");
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        FileAttributes attributes = File.GetAttributes(target);
        var owned = new TemporaryDirectory();
        Directory.Delete(owned.Path);
        Directory.CreateSymbolicLink(owned.Path, outside.Path);
        try
        {
            owned.Dispose();
            Assert.False(Directory.Exists(owned.Path));
            Assert.Equal(attributes, File.GetAttributes(target));
            Assert.Equal("external bytes", File.ReadAllText(target));
        }
        finally
        {
            if (Directory.Exists(owned.Path))
                Directory.Delete(owned.Path);
            File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void CrLfDefaults(string source)
    {
        _ = ProcessRunner.Run(["git", "-C", source, "config", "core.autocrlf", "true"], TestRepository.CleanEnvironment).Value;
        _ = ProcessRunner.Run(["git", "-C", source, "config", "core.eol", "crlf"], TestRepository.CleanEnvironment).Value;
    }
}
