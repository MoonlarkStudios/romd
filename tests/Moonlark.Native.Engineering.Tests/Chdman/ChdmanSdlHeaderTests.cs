using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Chdman;
using Moonlark.Native.Engineering.Cli;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Chdman;

/// <summary>Explicit SDL header input custody and real generator argument contracts using only synthetic files.</summary>
public sealed class ChdmanSdlHeaderTests
{
    /// <summary>Omitting the optional input preserves the previous Linux command and absent header receipt data.</summary>
    [Fact]
    public void AbsentHeadersPreservePreviousLinuxBehavior()
    {
        Assert.Null(ChdmanSdlHeaders.Capture(null, "linux-arm64").Value);
        Assert.Equal<string>(ChdmanBuild.GenerateArguments("/source", "21.1.8", "linux-arm64"),
            ChdmanBuild.GenerateArguments("/source", "21.1.8", "linux-arm64", null));
        var request = new ChdmanBuildRequest("archive", "output", 1, "log");
        Assert.Null(request.SdlIncludeRoot);
    }

    /// <summary>Both native Linux RIDs record every regular file and directory under the explicit include root.</summary>
    [Theory]
    [InlineData("linux-arm64")]
    [InlineData("linux-x64")]
    public void CapturesTheWholeNativeLinuxHeaderTree(string rid)
    {
        using var directory = new TemporaryDirectory();
        string root = CreateHeaders(ArtifactsPath.Resolve(directory.Path));
        JsonObject captured = ChdmanSdlHeaders.Capture(root, rid).Value!;
        Assert.Equal(root, captured["includeRoot"]!.GetValue<string>());
        Assert.Equal<string>(new[] { root, Path.Combine(root, "SDL2"), Path.Combine(root, "SDL2", "detail"), Path.Combine(root, "SDL2", "empty") }.Order(StringComparer.Ordinal),
            captured["directories"]!.AsArray().Select(node => node!.GetValue<string>()));
        JsonArray inputs = captured["inputs"]!.AsArray();
        Assert.Equal(3, inputs.Count);
        Assert.Equal<string>(new[] { Path.Combine(root, "SDL2", "SDL.h"), Path.Combine(root, "SDL2", "SDL_config.h"), Path.Combine(root, "SDL2", "detail", "SDL_internal.h") }.Order(StringComparer.Ordinal),
            inputs.Select(node => node!["path"]!.GetValue<string>()));
        Assert.All(inputs, node => Assert.Equal(8L, node!["bytes"]!.GetValue<long>()));
        Assert.Null(ChdmanSdlHeaders.Verify(captured));
    }

    /// <summary>A real canonical temp tree with the observed Windows short-name component is a valid explicit input.</summary>
    [Fact]
    public void CanonicalTempRootWithShortNameComponentIsAccepted()
    {
        using var directory = new TemporaryDirectory();
        string root = CreateHeaders(Path.Combine(ArtifactsPath.Resolve(directory.Path), "RUNNER~1", "AppData", "Local", "Temp"));
        Result<JsonObject?> captured = ChdmanSdlHeaders.Capture(root, "linux-arm64");
        Assert.True(captured.Succeeded);
        JsonObject headers = captured.Value!;
        Assert.Equal(root, headers["includeRoot"]!.GetValue<string>());
        Assert.Equal(3, headers["inputs"]!.AsArray().Count);
        Assert.Null(ChdmanSdlHeaders.Verify(headers));
        Assert.Contains("--ARCHOPTS_CXX=-I" + root, ChdmanBuild.GenerateArguments("/source", "21.1.8", "linux-arm64", root));
    }

    /// <summary>The real documented C++ option carries exactly the supplied include root without adding libraries.</summary>
    [Theory]
    [InlineData("linux-arm64")]
    [InlineData("linux-x64")]
    public void ExplicitHeadersAddOnlyTheDocumentedCppIncludeOption(string rid)
    {
        const string includeRoot = "/genuine-stage/include";
        const string includeOption = "--ARCHOPTS_CXX=-I" + includeRoot;
        ImmutableArray<string> previous = ChdmanBuild.GenerateArguments("/source", "21.1.8", rid);
        ImmutableArray<string> actual = ChdmanBuild.GenerateArguments("/source", "21.1.8", rid, includeRoot);
        Assert.Contains(includeOption, actual);
        Assert.Equal(1, actual.Count(value => value == includeOption));
        Assert.Equal("gmake", actual[^1]);
        Assert.Equal<string>(previous, actual.Where(value => value != includeOption));
        Assert.DoesNotContain(actual, value => value.Contains("-lSDL", StringComparison.Ordinal) || value.Contains("SDL_INSTALL_ROOT", StringComparison.Ordinal));
    }

    /// <summary>The nullable overload preserves every original Mac argument.</summary>
    [Fact]
    public void AbsentHeadersPreserveExactMacArguments()
    {
        Assert.Null(ChdmanSdlHeaders.Capture(null, "osx-arm64").Value);
        Assert.Equal<string>(ChdmanBuild.GenerateArguments("/source", "21.1.8"),
            ChdmanBuild.GenerateArguments("/source", "21.1.8", "osx-arm64", null));
    }

    /// <summary>An explicit Linux-only input is rejected for the Mac route.</summary>
    [Fact]
    public void MacRejectsExplicitLinuxHeaderInputs()
    {
        using var directory = new TemporaryDirectory();
        string root = CreateHeaders(ArtifactsPath.Resolve(directory.Path));
        Result<JsonObject?> captured = ChdmanSdlHeaders.Capture(root, "osx-arm64");
        Assert.False(captured.Succeeded);
        Assert.Contains("Linux", captured.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Explicit paths must exist and be canonical, absolute and safe for generated Make compiler options.</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("relative")]
    [InlineData("absent")]
    [InlineData("space")]
    [InlineData("comma")]
    [InlineData("dollar")]
    [InlineData("quote")]
    [InlineData("parent")]
    public void UnsafeOrMissingIncludeRootsAreRejected(string mutation)
    {
        using var directory = new TemporaryDirectory();
        string root = CreateHeaders(ArtifactsPath.Resolve(directory.Path));
        string candidate = mutation switch
        {
            "empty" => "",
            "relative" => "relative/include",
            "absent" => Path.Combine(ArtifactsPath.Resolve(directory.Path), "absent"),
            "space" => CreateHeaders(Path.Combine(ArtifactsPath.Resolve(directory.Path), "stage space")),
            "comma" => CreateHeaders(Path.Combine(ArtifactsPath.Resolve(directory.Path), "stage,suffix")),
            "dollar" => CreateHeaders(Path.Combine(ArtifactsPath.Resolve(directory.Path), "stage$(injected)")),
            "quote" => root + "\"suffix",
            "parent" => Path.Combine(root, "..", "include"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Result<JsonObject?> captured = ChdmanSdlHeaders.Capture(candidate, "linux-arm64");
        Assert.False(captured.Succeeded);
        Assert.Contains("SDL", captured.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Both the public entry header and real configured platform header are required.</summary>
    [Theory]
    [InlineData("SDL.h")]
    [InlineData("SDL_config.h")]
    public void MissingRequiredHeaderIsRejected(string name)
    {
        using var directory = new TemporaryDirectory();
        string root = CreateHeaders(ArtifactsPath.Resolve(directory.Path));
        File.Delete(Path.Combine(root, "SDL2", name));
        Result<JsonObject?> captured = ChdmanSdlHeaders.Capture(root, "linux-arm64");
        Assert.False(captured.Succeeded);
        Assert.Contains("SDL", captured.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Linked ancestors, include roots and nested directories cannot redirect the recorded closure.</summary>
    [Theory]
    [InlineData("ancestor")]
    [InlineData("root")]
    [InlineData("nested")]
    public void SymlinkedDirectoriesAreRejected(string mutation)
    {
        using var directory = new TemporaryDirectory();
        string stage = ArtifactsPath.Resolve(directory.Path);
        string root = CreateHeaders(stage);
        string candidate = root;
        string alias = Path.Combine(stage, "alias");
        if (mutation == "ancestor")
        {
            Directory.CreateSymbolicLink(alias, stage);
            candidate = Path.Combine(alias, "include");
        }
        else if (mutation == "root")
        {
            Directory.CreateSymbolicLink(alias, root);
            candidate = alias;
        }
        else
        {
            string nested = Path.Combine(root, "SDL2", "detail");
            Directory.Delete(nested, recursive: true);
            Directory.CreateDirectory(alias);
            Directory.CreateSymbolicLink(nested, alias);
        }
        Result<JsonObject?> captured = ChdmanSdlHeaders.Capture(candidate, "linux-arm64");
        Assert.False(captured.Succeeded);
        Assert.Contains("SDL", captured.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A linked header cannot substitute for a genuine regular input even if its bytes match.</summary>
    [Fact]
    public void SymlinkedHeaderIsRejected()
    {
        using var directory = new TemporaryDirectory();
        string stage = ArtifactsPath.Resolve(directory.Path);
        string root = CreateHeaders(stage);
        ReplaceHeaderWithLink(root, stage);
        Result<JsonObject?> captured = ChdmanSdlHeaders.Capture(root, "linux-arm64");
        Assert.False(captured.Succeeded);
        Assert.Contains("SDL", captured.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Changed bytes, file additions/deletions and directory additions invalidate the original capture.</summary>
    [Theory]
    [InlineData("content")]
    [InlineData("deleted")]
    [InlineData("added")]
    [InlineData("directory")]
    public void ChangedHeaderTreeCannotReachTheReceipt(string mutation)
    {
        using var directory = new TemporaryDirectory();
        string root = CreateHeaders(ArtifactsPath.Resolve(directory.Path));
        JsonObject captured = ChdmanSdlHeaders.Capture(root, "linux-arm64").Value!;
        string extra = Path.Combine(root, "SDL2", "detail", "SDL_internal.h");
        switch (mutation)
        {
            case "content":
                File.WriteAllText(extra, "changedB");
                break;
            case "deleted":
                File.Delete(extra);
                break;
            case "added":
                File.WriteAllText(extra + ".added.h", "headersA");
                break;
            case "directory":
                Directory.CreateDirectory(Path.Combine(root, "SDL2", "new-directory"));
                break;
        }
        Assert.Contains("changed", ChdmanSdlHeaders.Verify(captured)?.Message, StringComparison.Ordinal);
    }

    /// <summary>Replacing recorded files or directories with symlinks fails the final custody check.</summary>
    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    public void RedirectedTreeCannotReachTheReceipt(string mutation)
    {
        using var directory = new TemporaryDirectory();
        string stage = ArtifactsPath.Resolve(directory.Path);
        string root = CreateHeaders(stage);
        JsonObject captured = ChdmanSdlHeaders.Capture(root, "linux-arm64").Value!;
        if (mutation == "file")
            ReplaceHeaderWithLink(root, stage);
        else
        {
            string nested = Path.Combine(root, "SDL2", "detail");
            string external = Path.Combine(stage, "external");
            Directory.CreateDirectory(external);
            File.Copy(Path.Combine(nested, "SDL_internal.h"), Path.Combine(external, "SDL_internal.h"));
            Directory.Delete(nested, recursive: true);
            Directory.CreateSymbolicLink(nested, external);
        }
        Assert.Contains("changed", ChdmanSdlHeaders.Verify(captured)?.Message, StringComparison.Ordinal);
    }

    /// <summary>The real CLI forwards explicit header input, rejects missing prerequisites and clears stale success before native execution.</summary>
    [Fact]
    public void ExplicitCliInputRejectsMissingHeadersBeforeNativeExecution()
    {
        using var directory = new TemporaryDirectory();
        string stage = ArtifactsPath.Resolve(directory.Path);
        string output = Path.Combine(stage, "artifacts/tools/chdman");
        Directory.CreateDirectory(output);
        string manifest = Path.Combine(output, ChdmanBuild.ManifestName);
        File.WriteAllText(manifest, "stale success");
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = new CommandContext(stage, stdout, stderr, new Dictionary<string, string>(StringComparer.Ordinal));
        int result = ChdmanCommand.Build(["--archive", Path.Combine(stage, "absent.tar.gz"), "--log", Path.Combine(stage, "build.log"),
            "--output", output, "--jobs", "1", "--sdl-include", Path.Combine(stage, "absent-headers")], context);
        Assert.Equal(1, result);
        Assert.Contains(OperatingSystem.IsWindows() ? "native host" : "SDL", stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(manifest));
        Assert.False(File.Exists(Path.Combine(stage, "build.log")));
    }

    private static string CreateHeaders(string stage)
    {
        string root = Path.Combine(stage, "include");
        Directory.CreateDirectory(Path.Combine(root, "SDL2", "detail"));
        Directory.CreateDirectory(Path.Combine(root, "SDL2", "empty"));
        foreach (string name in new[] { "SDL.h", "SDL_config.h", "detail/SDL_internal.h" })
            File.WriteAllText(Path.Combine(root, "SDL2", name), "headersA");
        return root;
    }

    private static void ReplaceHeaderWithLink(string root, string stage)
    {
        string header = Path.Combine(root, "SDL2", "SDL.h");
        string target = Path.Combine(stage, "external-header.h");
        File.Copy(header, target);
        File.Delete(header);
        File.CreateSymbolicLink(header, target);
    }
}
