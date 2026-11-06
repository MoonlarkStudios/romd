using Moonlark.Native.Engineering.Cli;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Generation;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Generation;

/// <summary>Exported builtin headers are complete regular inputs, never a fallback to unrelated host headers.</summary>
public sealed class ClangResourcesTests
{
    /// <summary>A path containing spaces remains one typed option value.</summary>
    [Fact]
    public void ResourceArgumentPreservesPath() =>
        Assert.Equal<string>(["--resource-directory", "/owned/artifacts/clang resource"], ClangResources.Arguments("/owned/artifacts/clang resource"));

    /// <summary>The full tree, including headers included by stddef, is measured.</summary>
    [Fact]
    public void EveryHeaderIsFingerprinted()
    {
        using var root = new TemporaryDirectory();
        string directory = Create(root.Path);
        File.WriteAllText(Path.Combine(directory, "include", "__stddef_size_t.h"), "nested builtin");
        Directory.CreateDirectory(Path.Combine(directory, "include", "nested"));
        File.WriteAllText(Path.Combine(directory, "include", "nested", "builtin.h"), "nested tree");
        var result = ClangResources.Verify(root.Path, directory);
        Assert.True(result.Succeeded);
        Assert.Equal(5, result.Value.Count);
        Assert.Equal(Digest.Sha256("nested tree"u8), result.Value["include/nested/builtin.h"]!.GetValue<string>());
        Assert.Equal(Digest.Sha256("nested builtin"u8), result.Value["include/__stddef_size_t.h"]!.GetValue<string>());
    }

    /// <summary>Missing or empty required builtin headers are rejected before running ClangSharp.</summary>
    [Theory]
    [InlineData("stddef.h", false)]
    [InlineData("stdarg.h", false)]
    [InlineData("stdint.h", false)]
    [InlineData("stddef.h", true)]
    [InlineData("stdarg.h", true)]
    [InlineData("stdint.h", true)]
    public void RequiredHeadersCannotBeMissingOrEmpty(string name, bool empty)
    {
        using var root = new TemporaryDirectory();
        string directory = Create(root.Path);
        string file = Path.Combine(directory, "include", name);
        if (empty)
            File.WriteAllText(file, "");
        else
            File.Delete(file);
        Assert.False(ClangResources.Verify(root.Path, directory).Succeeded);
    }

    /// <summary>Symlinks cannot redirect a resource root, child directory or header.</summary>
    [Theory]
    [InlineData("root")]
    [InlineData("child")]
    [InlineData("header")]
    public void RedirectedResourcesAreRejected(string kind)
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        string directory = Create(root.Path);
        if (kind == "root")
        {
            string link = Path.Combine(root.Path, "artifacts", "redirected");
            Directory.CreateSymbolicLink(link, directory);
            directory = link;
        }
        else if (kind == "child")
            Directory.CreateSymbolicLink(Path.Combine(directory, "include", "redirected"), outside.Path);
        else
        {
            string file = Path.Combine(directory, "include", "stddef.h");
            File.Move(file, Path.Combine(outside.Path, "target"));
            File.CreateSymbolicLink(file, Path.Combine(outside.Path, "target"));
        }
        Assert.False(ClangResources.Verify(root.Path, directory).Succeeded);
    }

    /// <summary>An arbitrary host directory cannot become an unrecorded resource input.</summary>
    [Fact]
    public void OutsideArtifactsIsRejected()
    {
        using var root = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        Assert.False(ClangResources.Verify(root.Path, outside.Path).Succeeded);
    }

    /// <summary>Verified resources reach the tool as one typed option and are logged; generator failure, drift and a
    /// resource change during generation fail without changing tracked files.</summary>
    [Theory]
    [InlineData("success")]
    [InlineData("generator-failure")]
    [InlineData("drift")]
    [InlineData("resource-mutation")]
    public void GenerationUsesVerifiedResources(string scenario)
    {
        using var fixture = new GenerationFixture();
        fixture.CopyTrackedOutputs();
        string resources = Create(fixture.Root);
        Result<GenerationResult> result = BindingGenerator.Run(fixture.Root, "dotnet", check: true, TestRepository.CleanEnvironment, fixture.Tool(command =>
        {
            int option = command.ToList().IndexOf("--resource-directory");
            Assert.True(option >= 0);
            Assert.Equal(resources, command[option + 1]);
            if (scenario == "resource-mutation")
                File.AppendAllText(Path.Combine(resources, "include", "stddef.h"), "changed during generation");
            return scenario == "generator-failure" ? new Failure("generator failed")
                : fixture.WriteOutput(command, GenerationFixture.CommittedBindings + (scenario == "drift" ? "\n// drift\n" : ""));
        }), GenerationFixture.NoHostArguments, resources);
        Assert.Equal(scenario == "success", result.Succeeded);
        Assert.Single(fixture.Invocations);
        Assert.Equal(GenerationFixture.CommittedBindings, File.ReadAllText(fixture.Bindings));
        if (scenario == "resource-mutation")
            Assert.Contains("changed during generation", result.Failure.Message, StringComparison.Ordinal);
        if (result.Succeeded)
            Assert.Contains("Clang resources: ", File.ReadAllText(Path.Combine(fixture.Output, BindingGenerator.LogFile)), StringComparison.Ordinal);
    }

    /// <summary>Resources outside artifacts, incomplete or inside the recreated generation output never reach the tool.</summary>
    [Theory]
    [InlineData("outside", "artifacts")]
    [InlineData("incomplete", "stdarg.h")]
    [InlineData("generation-output", "inside the generation output")]
    public void InvalidResourcesNeverReachTheTool(string scenario, string message)
    {
        using var fixture = new GenerationFixture();
        using var outside = new TemporaryDirectory();
        string resources = scenario switch
        {
            "outside" => Complete(outside.Path),
            "generation-output" => Complete(Path.Combine(fixture.Output, "clang-resource")),
            _ => Create(fixture.Root),
        };
        if (scenario == "incomplete")
            File.Delete(Path.Combine(resources, "include", "stdarg.h"));
        Result<GenerationResult> result = BindingGenerator.Run(fixture.Root, "dotnet", check: true, TestRepository.CleanEnvironment,
            fixture.Tool(_ => new Failure("Unexpected generation")), GenerationFixture.NoHostArguments, resources);
        Assert.Contains(message, result.Failure.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Invocations);
    }

    /// <summary>The generate command passes --clang-resources through as an absolute directory.</summary>
    [Fact]
    public void GenerateCommandAcceptsClangResources()
    {
        using var fixture = new GenerationFixture();
        fixture.CopyTrackedOutputs();
        string resources = Create(fixture.Root);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var context = new CommandContext(fixture.Root, output, error, TestRepository.CleanEnvironment);
        int exit = GenerationCommand.Generate(["--check", "--clang-resources", resources], context,
            fixture.Tool(command => fixture.WriteOutput(command, GenerationFixture.CommittedBindings)), GenerationFixture.NoHostArguments);
        Assert.True(exit == 0, error.ToString());
        IReadOnlyList<string> command = Assert.Single(fixture.Invocations);
        Assert.Equal<string>(ClangResources.Arguments(resources), command.TakeLast(2));
    }

    private static string Create(string root) => Complete(Path.Combine(root, "artifacts", "clang resource"));

    private static string Complete(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "include"));
        foreach (string name in (string[])["stddef.h", "stdarg.h", "stdint.h"])
            File.WriteAllText(Path.Combine(directory, "include", name), "synthetic builtin " + name);
        return directory;
    }
}
