using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Cli;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Generation;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Generation;

/// <summary>
/// The generation pipeline against a shared clone of the real pinned upstream, with the tool simulated.
/// Rejected inputs never reach the tool and nothing tracked is written unless every check passes.
/// </summary>
public sealed class BindingGeneratorTests
{
    private const string Sentinel = "tracked bindings sentinel\n";
    private static readonly DateTime Untouched = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    /// <summary>Write mode publishes the raw output verbatim and the pin-derived contract, recording the exact command.</summary>
    [Fact]
    public void WriteModePublishesRawOutputAndContractWithCommandEvidence()
    {
        using var fixture = new GenerationFixture();
        Result<GenerationResult> result = fixture.Run(check: false, command => fixture.WriteOutput(command, GenerationFixture.CommittedBindings));
        Assert.True(result.Succeeded, result.Succeeded ? "" : result.Failure.Message);
        Assert.Equal(GenerationFixture.CommittedBindings, File.ReadAllText(fixture.Bindings));
        Assert.Equal(GenerationFixture.CommittedContract, File.ReadAllText(fixture.Contract));
        IReadOnlyList<string> invoked = Assert.Single(fixture.Invocations);
        Assert.Equal<string>(invoked, result.Value.Command);
        Assert.Equal<string>(["dotnet", "tool", "run", "ClangSharpPInvokeGenerator", "--", .. GenerationConfiguration.Libchdr.ToolArguments([])], invoked);
        JsonNode evidence = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Output, BindingGenerator.CommandEvidenceFile)))!;
        Assert.Equal<string>(invoked, evidence["command"]!.AsArray().Select(argument => (string)argument!));
        Assert.Equal(BindingGenerator.ToolVersion, (string?)evidence["toolVersion"]);
        Assert.Contains("git -C", File.ReadAllText(Path.Combine(fixture.Output, BindingGenerator.LogFile)), StringComparison.Ordinal);
    }

    /// <summary>Enum portability reaches the real tool boundary as separate literal arguments and raw output stays untouched.</summary>
    [Fact]
    public void EnumPortabilityOptionsReachToolWithoutChangingItsOutput()
    {
        using var fixture = new GenerationFixture();
        Result<GenerationResult> result = fixture.Run(check: false, command =>
        {
            string[] Values(string option) => Enumerable.Range(0, command.Count - 1)
                .Where(index => command[index] == option).Select(index => command[index + 1]).ToArray();
            Assert.Equal<string>(["chd_error=int"], Values("--with-type"));
            Assert.Equal<string>(["unsigned int"], Values("--native-type-names-to-strip"));
            return fixture.WriteOutput(command, GenerationFixture.CommittedBindings);
        });
        Assert.True(result.Succeeded, result.Succeeded ? "" : result.Failure.Message);
        Assert.Equal(GenerationFixture.CommittedBindings, File.ReadAllText(fixture.Bindings));
    }

    /// <summary>Enum annotation stripping must not corrupt fixed-width names or signed callback signatures.</summary>
    [Theory]
    [InlineData("uint32_t")]
    [InlineData("uint64_t")]
    [InlineData("uint64_t (*)(void *)")]
    [InlineData("int (*)(void *, int64_t, int)")]
    public void GeneratedEnumPortabilityPreservesOtherNativeAnnotations(string nativeType) =>
        Assert.Contains($"[NativeTypeName(\"{nativeType}\")]", GenerationFixture.CommittedBindings, StringComparison.Ordinal);

    /// <summary>Check mode passes on matching tracked files and never rewrites them.</summary>
    [Fact]
    public void CheckModeMatchesWithoutWritingTrackedFiles()
    {
        using var fixture = new GenerationFixture();
        fixture.CopyTrackedOutputs();
        File.SetLastWriteTimeUtc(fixture.Bindings, Untouched);
        File.SetLastWriteTimeUtc(fixture.Contract, Untouched);
        Result<GenerationResult> result = fixture.Run(check: true, command => fixture.WriteOutput(command, GenerationFixture.CommittedBindings));
        Assert.True(result.Succeeded, result.Succeeded ? "" : result.Failure.Message);
        Assert.Equal(Untouched, File.GetLastWriteTimeUtc(fixture.Bindings));
        Assert.Equal(Untouched, File.GetLastWriteTimeUtc(fixture.Contract));
    }

    /// <summary>Check mode reports binding and contract drift and leaves the tracked files unchanged.</summary>
    [Theory]
    [InlineData(BindingGenerator.BindingsPath, "Generated binding drift")]
    [InlineData(BindingGenerator.ContractPath, "Generated build-contract drift")]
    public void CheckModeDriftFailsWithoutWriting(string tracked, string message)
    {
        using var fixture = new GenerationFixture();
        fixture.CopyTrackedOutputs();
        string path = Path.Combine(fixture.Root, tracked);
        File.AppendAllText(path, "// drift\n");
        string drifted = File.ReadAllText(path);
        AssertFails(fixture.Run(check: true, command => fixture.WriteOutput(command, GenerationFixture.CommittedBindings)), message);
        Assert.Equal(drifted, File.ReadAllText(path));
    }

    /// <summary>A symlinked output directory cannot redirect writes into the tracked bindings.</summary>
    [Fact]
    public void OutputDirectorySymlinkCannotRedirectWrites()
    {
        using var fixture = new GenerationFixture();
        WriteSentinel(fixture);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Output)!);
        Directory.CreateSymbolicLink(fixture.Output, Path.GetDirectoryName(fixture.Bindings)!);
        AssertFails(fixture.Run(check: true, command => fixture.WriteOutput(command, GenerationFixture.CommittedBindings)), "artifacts");
        Assert.Empty(fixture.Invocations);
        Assert.Equal(Sentinel, File.ReadAllText(fixture.Bindings));
    }

    /// <summary>A stale output file that links to the tracked bindings is removed, never followed, before the tool writes.</summary>
    [Fact]
    public void StaleOutputFileSymlinkIsRemovedNotFollowed()
    {
        using var fixture = new GenerationFixture();
        WriteSentinel(fixture);
        Directory.CreateDirectory(fixture.Output);
        string output = Path.Combine(fixture.Output, GenerationConfiguration.Libchdr.OutputFileName);
        File.CreateSymbolicLink(output, fixture.Bindings);
        AssertFails(fixture.Run(check: true, command => fixture.WriteOutput(command, GenerationFixture.CommittedBindings)), "binding drift");
        Assert.Equal(Sentinel, File.ReadAllText(fixture.Bindings));
        Assert.False(ArtifactsPath.IsLink(output));
    }

    /// <summary>The tool is only ever told to write inside the artifacts output, so it cannot write the tracked bindings.</summary>
    [Fact]
    public void ToolCannotWriteDirectlyToTrackedBindings()
    {
        using var fixture = new GenerationFixture();
        WriteSentinel(fixture);
        AssertFails(fixture.Run(check: true, command => fixture.WriteOutput(command, GenerationFixture.CommittedBindings)), "binding drift");
        IReadOnlyList<string> command = Assert.Single(fixture.Invocations);
        int option = Assert.Single(Enumerable.Range(0, command.Count), index => command[index] == "--output");
        string target = Path.GetFullPath(Path.Combine(fixture.Root, command[option + 1]));
        Assert.True(ArtifactsPath.IsWithin(target, fixture.Output));
        Assert.NotEqual(Path.GetFullPath(fixture.Bindings), target);
        Assert.Equal(Sentinel, File.ReadAllText(fixture.Bindings));
    }

    /// <summary>A successful tool run must produce fresh output; a stale file is deleted first, never reused.</summary>
    [Fact]
    public void SuccessfulToolWithoutFreshOutputCannotReuseStaleBindings()
    {
        using var fixture = new GenerationFixture();
        string stale = Path.Combine(fixture.Output, GenerationConfiguration.Libchdr.OutputFileName);
        Directory.CreateDirectory(fixture.Output);
        File.WriteAllText(stale, GenerationFixture.CommittedBindings);
        AssertFails(fixture.Run(check: false, _ => null), "fresh output");
        Assert.Single(fixture.Invocations);
        Assert.False(File.Exists(stale));
        AssertNothingTrackedWritten(fixture);
    }

    /// <summary>A source change made while the tool runs fails the post-run identity check before anything is written.</summary>
    [Fact]
    public void SourceChangeDuringToolInvocationCannotCommitBindings()
    {
        using var fixture = new GenerationFixture();
        AssertFails(fixture.Run(check: false, command =>
        {
            File.WriteAllText(Path.Combine(fixture.Source, "include/stdint.h"), "unreviewed ABI input introduced during generation");
            return fixture.WriteOutput(command, GenerationFixture.CommittedBindings);
        }), "dirty");
        AssertNothingTrackedWritten(fixture);
    }

    /// <summary>An ignored transitive include, invisible to plain status, still makes the source dirty before the tool runs.</summary>
    [Fact]
    public void IgnoredTransitiveIncludeIsRejectedBeforeInvokingGenerator()
    {
        using var fixture = new GenerationFixture();
        File.AppendAllText(Path.Combine(fixture.Source, ".git/info/exclude"), "\ninclude/stdint.h\n");
        File.WriteAllText(Path.Combine(fixture.Source, "include/stdint.h"), "#include_next <stdint.h>\n#define uint32_t uint64_t\n");
        Assert.Equal("", TestRepository.Git(fixture.Source, "status", "--porcelain"));
        foreach ((string name, string digest) in Authorities.ReadLibchdr(fixture.Root).Value.Pin.Headers)
            Assert.Equal(digest, Digest.Sha256File(Path.Combine(fixture.Source, name)));
        AssertRejectedBeforeTool(fixture, "dirty");
    }

    /// <summary>A tampered entry header fails before the tool runs.</summary>
    [Fact]
    public void TamperedEntryHeaderIsRejectedBeforeInvokingGenerator()
    {
        using var fixture = new GenerationFixture();
        File.AppendAllText(Path.Combine(fixture.Root, GenerationConfiguration.EntryPath), "#include \"unreviewed.h\"\n");
        AssertRejectedBeforeTool(fixture, "verified header");
    }

    /// <summary>The build-info shim must stay self-contained; any include would need an unchecked search root.</summary>
    [Fact]
    public void ShimWithIncludeIsRejectedBeforeInvokingGenerator()
    {
        using var fixture = new GenerationFixture();
        string shim = Path.Combine(fixture.Root, GenerationConfiguration.ShimPath);
        File.WriteAllText(shim, "#include <stdint.h>\n" + File.ReadAllText(shim));
        AssertRejectedBeforeTool(fixture, "self-contained");
    }

    /// <summary>Only the exact pinned ClangSharp version may generate.</summary>
    [Fact]
    public void WrongToolVersionIsRejectedBeforeInvokingGenerator()
    {
        using var fixture = new GenerationFixture();
        string manifest = Path.Combine(fixture.Root, BindingGenerator.ToolManifestPath);
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"21.1.8.4\"", "\"21.1.8.3\"", StringComparison.Ordinal));
        AssertRejectedBeforeTool(fixture, "Generator pin changed");
    }

    /// <summary>Implicit compiler or Git environment overrides fail before any command runs.</summary>
    [Fact]
    public void ImplicitEnvironmentIsRejectedBeforeInvokingGenerator()
    {
        using var fixture = new GenerationFixture();
        var environment = new Dictionary<string, string>(TestRepository.CleanEnvironment, StringComparer.Ordinal) { ["CPATH"] = "unreviewed" };
        AssertFails(fixture.Run(check: false, command => fixture.WriteOutput(command, GenerationFixture.CommittedBindings), environment), "environment overrides");
        Assert.Empty(fixture.Invocations);
        AssertNothingTrackedWritten(fixture);
    }

    /// <summary>A failed tool run is reported and nothing tracked is written.</summary>
    [Fact]
    public void FailedToolRunWritesNothingTracked()
    {
        using var fixture = new GenerationFixture();
        AssertFails(fixture.Run(check: false, _ => new Failure("simulated generator failure")), "simulated generator failure");
        AssertNothingTrackedWritten(fixture);
    }

    /// <summary>Generated functions that disagree with the pinned header fail before anything is written.</summary>
    [Fact]
    public void GeneratedFunctionsDisagreeingWithHeaderFailBeforeWriting()
    {
        using var fixture = new GenerationFixture();
        string renamed = GenerationFixture.CommittedBindings.Replace(" chd_precache(", " chd_precache_unreviewed(", StringComparison.Ordinal);
        AssertFails(fixture.Run(check: false, command => fixture.WriteOutput(command, renamed)), "chd_precache, chd_precache_unreviewed");
        AssertNothingTrackedWritten(fixture);
    }

    /// <summary>The command line passes the selected SDK through and reports the import count.</summary>
    [Fact]
    public void CommandReportsImportsAndUsesTheSelectedSdk()
    {
        using var fixture = new GenerationFixture();
        fixture.CopyTrackedOutputs();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var context = new CommandContext(fixture.Root, output, error, TestRepository.CleanEnvironment);
        int exit = GenerationCommand.Generate(["--check", "--dotnet", "/existing/sdk/dotnet"], context,
            fixture.Tool(command => fixture.WriteOutput(command, GenerationFixture.CommittedBindings)), GenerationFixture.NoHostArguments);
        Assert.Equal(0, exit);
        Assert.Equal("/existing/sdk/dotnet", Assert.Single(fixture.Invocations)[0]);
        Assert.Contains("PASS: 19 internal imports from the exact pinned headers; tracked bindings and contract match", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Unknown arguments fail before anything runs.</summary>
    [Fact]
    public void UnknownArgumentFails()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var context = new CommandContext(TestRepository.Root, output, error, TestRepository.CleanEnvironment);
        Assert.Equal(1, GenerationCommand.Generate(["--write"], context));
        Assert.Contains("Unrecognized argument --write", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>macOS hosts add exactly the existing SDK sysroot; other hosts add nothing.</summary>
    [Fact]
    public void HostArgumentsAddOnlyTheExistingMacSdk()
    {
        using var log = new StringWriter();
        ImmutableArray<string> arguments = BindingGenerator.HostPlatformArguments(TestRepository.CleanEnvironment, log).Value;
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Empty(arguments);
            return;
        }
        string sysroot = Assert.Single(arguments);
        Assert.StartsWith("--additional=-isysroot/", sysroot, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(sysroot["--additional=-isysroot".Length..], "usr/include/stdio.h")));
    }

    private static void WriteSentinel(GenerationFixture fixture)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Bindings)!);
        File.WriteAllText(fixture.Bindings, Sentinel);
    }

    private static void AssertRejectedBeforeTool(GenerationFixture fixture, string message)
    {
        AssertFails(fixture.Run(check: false, command => fixture.WriteOutput(command, GenerationFixture.CommittedBindings)), message);
        Assert.Empty(fixture.Invocations);
        AssertNothingTrackedWritten(fixture);
    }

    private static void AssertNothingTrackedWritten(GenerationFixture fixture)
    {
        Assert.False(File.Exists(fixture.Bindings));
        Assert.False(File.Exists(fixture.Contract));
    }

    private static void AssertFails(Result<GenerationResult> result, string message)
    {
        Assert.False(result.Succeeded);
        Assert.Contains(message, result.Failure.Message, StringComparison.Ordinal);
    }
}
