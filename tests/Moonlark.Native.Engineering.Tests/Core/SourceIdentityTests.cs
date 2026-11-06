using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Core;

/// <summary>Source identity fails closed on every way a checkout can differ from its pin.</summary>
public sealed class SourceIdentityTests
{
    /// <summary>An exact clean checkout yields its commit epoch.</summary>
    [Fact]
    public void CleanExactSourceAndHeaderHashesPass()
    {
        using var directory = new TemporaryDirectory();
        (string source, LibchdrPin pin) = SyntheticSource.Create(directory.Path);
        Result<long> epoch = SourceIdentity.Verify(source, pin, TestRepository.CleanEnvironment);
        Assert.True(epoch.Succeeded);
        Assert.True(epoch.Value > 0);
    }

    /// <summary>A different HEAD commit is rejected.</summary>
    [Fact]
    public void WrongCommitFailsBeforeCompilation()
    {
        using var directory = new TemporaryDirectory();
        (string source, LibchdrPin pin) = SyntheticSource.Create(directory.Path);
        AssertFails(source, pin with { Commit = new string('0', 40) }, "pinned commit");
    }

    /// <summary>A modified tracked file makes the checkout dirty.</summary>
    [Fact]
    public void ModifiedTrackedSourceFails()
    {
        using var directory = new TemporaryDirectory();
        (string source, LibchdrPin pin) = SyntheticSource.Create(directory.Path);
        File.WriteAllText(Path.Combine(source, Authorities.HeaderPaths[0]), "modified");
        AssertFails(source, pin, "dirty");
    }

    /// <summary>Index flags that hide edits from status cannot hide them from blob comparison.</summary>
    [Theory]
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    public void IndexFlagsCannotHideModifiedImplementation(string flag)
    {
        using var directory = new TemporaryDirectory();
        (string source, LibchdrPin pin) = SyntheticSource.Create(directory.Path);
        TestRepository.Git(source, "update-index", flag, "implementation.c");
        File.WriteAllText(Path.Combine(source, "implementation.c"), "int checked_source = 0;\n");
        Assert.Equal("", TestRepository.Git(source, "status", "--porcelain"));
        AssertFails(source, pin, "Tracked source bytes");
    }

    /// <summary>Untracked and ignored files are both unreviewed inputs.</summary>
    [Theory]
    [InlineData("extra.c")]
    [InlineData("ignored")]
    public void UntrackedAndIgnoredInputsBothFail(string name)
    {
        using var directory = new TemporaryDirectory();
        (string source, LibchdrPin pin) = SyntheticSource.Create(directory.Path);
        File.WriteAllText(Path.Combine(source, name), "unreviewed");
        AssertFails(source, pin, "dirty");
    }

    /// <summary>A clean checkout still fails when an ABI header digest disagrees with the pin.</summary>
    [Fact]
    public void CleanSourceWithWrongHeaderDigestFails()
    {
        using var directory = new TemporaryDirectory();
        (string source, LibchdrPin pin) = SyntheticSource.Create(directory.Path);
        var headers = new Dictionary<string, string>(pin.Headers, StringComparer.Ordinal) { [Authorities.HeaderPaths[0]] = new string('0', 64) };
        AssertFails(source, pin with { Headers = headers }, "header digest");
    }

    /// <summary>Implicit environment overrides fail before any git command runs.</summary>
    [Fact]
    public void ImplicitEnvironmentFailsBeforeAnyGitCommand()
    {
        using var directory = new TemporaryDirectory();
        (string source, LibchdrPin pin) = SyntheticSource.Create(directory.Path);
        var environment = new Dictionary<string, string>(TestRepository.CleanEnvironment, StringComparer.Ordinal) { ["GIT_DIR"] = "elsewhere" };
        using var log = new StringWriter();
        Result<long> result = SourceIdentity.Verify(source, pin, environment, log);
        Assert.Contains("environment overrides", result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal("", log.ToString());
    }

    private static void AssertFails(string source, LibchdrPin pin, string message)
    {
        Result<long> result = SourceIdentity.Verify(source, pin, TestRepository.CleanEnvironment);
        Assert.False(result.Succeeded);
        Assert.Contains(message, result.Failure.Message, StringComparison.Ordinal);
    }
}
