using Moonlark.Native.Engineering.Cli;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Generation;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Moonlark.Native.Engineering.Upstream;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Upstream;

/// <summary>End-to-end pin updates against a synthetic upstream with an injected regeneration step: all or nothing.</summary>
public sealed class UpstreamUpdateTests
{
    /// <summary>The update moves the checkout, rewrites only the identity values and the export list, then regenerates once against the new pin.</summary>
    [Fact]
    public void UpdateMovesPinAndRegeneratesEveryDerivedFile()
    {
        using var fixture = new UpstreamFixture();
        List<string> observed = [];
        Result<UpstreamUpdateResult> result = UpstreamUpdate.Run(fixture.Root, fixture.SecondCommit, TestRepository.CleanEnvironment, root =>
        {
            LibchdrPin pin = Authorities.ReadLibchdr(root).Value.Pin;
            observed.Add(pin.Commit);
            return SourceIdentity.Verify(Path.Combine(root, GenerationConfiguration.SourcePath), pin, TestRepository.CleanEnvironment) is { Succeeded: false } identity
                ? identity.Failure : null;
        });
        Assert.True(result.Succeeded, result.Succeeded ? "" : result.Failure.Message);
        Assert.Equal(new UpstreamUpdateResult(fixture.FirstCommit, fixture.SecondCommit, fixture.SecondVersion, ExportsChanged: true), result.Value);
        Assert.Equal<string>([fixture.SecondCommit], observed);
        Assert.Equal(fixture.SecondCommit, fixture.Head);
        Assert.StartsWith("v0.3.0-1-g", fixture.SecondVersion, StringComparison.Ordinal);
        Assert.Equal(UpstreamFixture.Substitute(fixture.Before[Authorities.PinPath], fixture.FirstCommit, fixture.SecondCommit, fixture.FirstVersion,
            fixture.SecondVersion, (fixture.FirstDigests[0], fixture.SecondDigests[0]), (fixture.FirstDigests[1], fixture.SecondDigests[1])),
            fixture.Read(Authorities.PinPath));
        Assert.Equal(UpstreamFixture.Substitute(fixture.Before[Authorities.PropsPath], fixture.FirstCommit, fixture.SecondCommit, fixture.FirstVersion,
            fixture.SecondVersion), fixture.Read(Authorities.PropsPath));
        Assert.Equal(UpstreamFixture.SecondExports, fixture.Read(ExportInventory.AllowlistPath));
    }

    /// <summary>Updating to the current pin is a no-op for every tracked file.</summary>
    [Fact]
    public void UpdateToTheCurrentPinChangesNothing()
    {
        using var fixture = new UpstreamFixture();
        Result<UpstreamUpdateResult> result = UpstreamUpdate.Run(fixture.Root, fixture.FirstCommit, TestRepository.CleanEnvironment, _ => null);
        Assert.True(result.Succeeded, result.Succeeded ? "" : result.Failure.Message);
        Assert.False(result.Value.ExportsChanged);
        Assert.Equal(fixture.Before, fixture.Tracked());
    }

    /// <summary>A commit absent from the local checkout is never fetched; the exact fetch command is printed instead.</summary>
    [Fact]
    public void MissingCommitPrintsTheFetchCommandAndChangesNothing()
    {
        using var fixture = new UpstreamFixture();
        string missing = new('a', 40);
        AssertUnchanged(fixture, missing, $"git -C native/libchdr/upstream fetch --tags https://github.com/rtissera/libchdr {missing}");
    }

    /// <summary>Untracked and ignored files are both refused before the checkout moves.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirtyCheckoutIsRefused(bool ignored)
    {
        using var fixture = new UpstreamFixture();
        if (ignored)
            File.AppendAllText(Path.Combine(fixture.Source, ".git/info/exclude"), "\nunreviewed.c\n");
        File.WriteAllText(Path.Combine(fixture.Source, "unreviewed.c"), "int unreviewed;\n");
        AssertUnchanged(fixture, fixture.SecondCommit, "dirty");
    }

    /// <summary>A checkout that is not at the current pin is refused, so the rollback target and changelog range are exact.</summary>
    [Fact]
    public void CheckoutAwayFromThePinIsRefused()
    {
        using var fixture = new UpstreamFixture();
        fixture.Git("checkout", "--quiet", "--detach", fixture.SecondCommit);
        AssertUnchanged(fixture, fixture.SecondCommit, "does not match pinned commit", fixture.SecondCommit);
    }

    /// <summary>Anything but a full lowercase SHA-1 fails before any git command, which also blocks option and ref injection.</summary>
    [Theory]
    [InlineData("HEAD")]
    [InlineData("607694c")]
    [InlineData("--upload-pack=touch")]
    [InlineData("607694CA0812EDFC9CC2030C64634FC2393668DE")]
    public void NonShaCommitIsRejected(string commit)
    {
        using var fixture = new UpstreamFixture();
        AssertUnchanged(fixture, commit, "full 40-character lowercase SHA-1");
    }

    /// <summary>A failing regeneration after the first write restores every tracked file and the previous checkout.</summary>
    [Fact]
    public void FailingGeneratorRollsBackEverything()
    {
        using var fixture = new UpstreamFixture();
        Result<UpstreamUpdateResult> result = UpstreamUpdate.Run(fixture.Root, fixture.SecondCommit, TestRepository.CleanEnvironment, root =>
        {
            File.WriteAllText(Path.Combine(root, BindingGenerator.BindingsPath), "// half-written bindings\n");
            return new Failure("simulated generator failure");
        });
        Assert.False(result.Succeeded);
        Assert.Contains($"rolled back to {fixture.FirstCommit}", result.Failure.Message, StringComparison.Ordinal);
        Assert.Contains("simulated generator failure", result.Failure.Message, StringComparison.Ordinal);
        AssertRestored(fixture);
    }

    /// <summary>An unexpected exception during regeneration still rolls back before propagating to the command boundary.</summary>
    [Fact]
    public void ThrowingGeneratorRollsBackAndPropagates()
    {
        using var fixture = new UpstreamFixture();
        Assert.Throws<IOException>(() => UpstreamUpdate.Run(fixture.Root, fixture.SecondCommit, TestRepository.CleanEnvironment, root =>
        {
            File.WriteAllText(Path.Combine(root, BindingGenerator.ContractPath), "// half-written contract\n");
            throw new IOException("simulated disk failure");
        }));
        AssertRestored(fixture);
    }

    /// <summary>The command prints the remaining manual steps, including the upstream log range and changed export lists.</summary>
    [Fact]
    public void CommandPrintsTheRemainingManualSteps()
    {
        using var fixture = new UpstreamFixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var context = new CommandContext(fixture.Root, output, error, TestRepository.CleanEnvironment);
        List<string> sdks = [];
        int exit = UpstreamCommand.Update(["--commit", fixture.SecondCommit, "--dotnet", "/existing/sdk/dotnet"], context, dotnet =>
        {
            sdks.Add(dotnet);
            return _ => null;
        });
        Assert.Equal(0, exit);
        Assert.Equal<string>(["/existing/sdk/dotnet"], sdks);
        string printed = output.ToString();
        Assert.Contains($"git -C native/libchdr/upstream log --oneline {fixture.FirstCommit}..{fixture.SecondCommit}", printed, StringComparison.Ordinal);
        Assert.Contains("The export list changed: native build regenerates the platform export files", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("exports.map", printed, StringComparison.Ordinal);
        Assert.Contains("reviewed upstream diff, including bundled codec changes, in the change description", printed, StringComparison.Ordinal);
        Assert.Contains("PASS: pin, props, exports.txt, bindings and contract regenerated for " + fixture.SecondVersion, printed, StringComparison.Ordinal);
    }

    /// <summary>The commit is required.</summary>
    [Fact]
    public void CommandRequiresTheCommit()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var context = new CommandContext(TestRepository.Root, output, error, TestRepository.CleanEnvironment);
        Assert.Equal(1, UpstreamCommand.Update([], context, _ => _ => new Failure("must not regenerate")));
        Assert.Contains("Missing required option --commit", error.ToString(), StringComparison.Ordinal);
    }

    private static void AssertUnchanged(UpstreamFixture fixture, string commit, string message, string? head = null)
    {
        int regenerations = 0;
        Result<UpstreamUpdateResult> result = UpstreamUpdate.Run(fixture.Root, commit, TestRepository.CleanEnvironment, _ =>
        {
            regenerations++;
            return null;
        });
        Assert.False(result.Succeeded);
        Assert.Contains(message, result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, regenerations);
        Assert.Equal(fixture.Before, fixture.Tracked());
        Assert.Equal(head ?? fixture.FirstCommit, fixture.Head);
    }

    private static void AssertRestored(UpstreamFixture fixture)
    {
        Assert.Equal(fixture.Before, fixture.Tracked());
        Assert.Equal(fixture.FirstCommit, fixture.Head);
        Assert.True(SourceIdentity.Verify(fixture.Source, Authorities.ReadLibchdr(fixture.Root).Value.Pin, TestRepository.CleanEnvironment).Succeeded);
    }
}
