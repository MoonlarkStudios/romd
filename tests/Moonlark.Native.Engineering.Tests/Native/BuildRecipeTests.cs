using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Native;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Native;

/// <summary>Effective configuration, location independence, recipe inputs and toolchain policy.</summary>
public sealed class BuildRecipeTests
{
    /// <summary>Only allowlisted, location-independent cache settings are recorded, sorted ordinally.</summary>
    [Fact]
    public void ConfigurationSelectsOnlyLocationIndependentSettings()
    {
        JsonObject configuration = BuildRecipe.SelectConfiguration(Cache("/first/checkout")).Value;
        Assert.Equal<string>(["BUILD_LTO", "CHDR_VERIFY_BLOCK_CRC", "CMAKE_BUILD_TYPE", "CMAKE_C_FLAGS_RELEASE", "CMAKE_GENERATOR", "MINIZ_STDIO", "MOONLARK_RID"],
            configuration.Select(property => property.Key));
        Assert.Equal("Ninja", (string)configuration["CMAKE_GENERATOR"]!);
    }

    /// <summary>Builds in different directories produce the same configuration and therefore the same buildId.</summary>
    [Fact]
    public void RecipeIsIndependentOfTheBuildLocation()
    {
        using var root = new NativeRoot();
        JsonObject inputs = BuildRecipe.InputDigests(root.Path).Value;
        string[] identities = [.. ((string[])["/first/checkout", "/second/elsewhere"]).Select(location =>
        {
            JsonObject recipe = BuildRecipe.Create(root.Authority, NativeRoot.Rid, BuildRecipe.SelectConfiguration(Cache(location)).Value,
                NativeRoot.Toolchain(), 12345, inputs);
            Assert.Null(BuildRecipe.RejectLocations(recipe, [location, location + "/artifacts/out"]));
            return BuildRecipe.BuildId(recipe);
        })];
        Assert.Equal(identities[0], identities[1]);
    }

    /// <summary>A selected value naming a local path is a named failure instead of a silent identity change.</summary>
    [Fact]
    public void LocationDependentRecipeValuesFail()
    {
        using var root = new NativeRoot();
        JsonObject configuration = NativeRoot.Configuration(NativeRoot.Rid);
        configuration["CMAKE_C_FLAGS"] = "-I" + Path.Combine(root.Path, "include");
        JsonObject recipe = BuildRecipe.Create(root.Authority, NativeRoot.Rid, configuration, NativeRoot.Toolchain(), 12345, BuildRecipe.InputDigests(root.Path).Value);
        Assert.Contains("location-dependent", BuildRecipe.RejectLocations(recipe, [root.Path])?.Message, StringComparison.Ordinal);
    }

    /// <summary>Host paths the build never named (package-manager prefixes, TMPDIR, SDKs) still fail as rooted path tokens.</summary>
    [Theory]
    [InlineData("-I/opt/homebrew/include")]
    [InlineData("/private/var/folders/x/T")]
    [InlineData("--sysroot=/Library/Developer/CommandLineTools/SDKs/MacOSX.sdk")]
    [InlineData("-Wl,-rpath,/usr/local/lib")]
    [InlineData("~/toolchains/clang")]
    public void UnknownRootedHostPathsFail(string value)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var root = new NativeRoot();
        JsonObject configuration = NativeRoot.Configuration(NativeRoot.Rid);
        configuration["CMAKE_C_FLAGS"] = value;
        JsonObject recipe = BuildRecipe.Create(root.Authority, NativeRoot.Rid, configuration, NativeRoot.Toolchain(), 12345, BuildRecipe.InputDigests(root.Path).Value);
        Assert.Contains("location-dependent", BuildRecipe.RejectLocations(recipe, [root.Path])?.Message, StringComparison.Ordinal);
    }

    /// <summary>Repository-relative paths, the upstream URL and tool banners are not locations.</summary>
    [Theory]
    [InlineData("-Wl,-dead_strip")]
    [InlineData("CMake suite maintained and supported by Kitware (kitware.com/cmake).")]
    [InlineData("https://github.com/rtissera/libchdr")]
    public void LocationIndependentValuesPass(string value)
    {
        using var root = new NativeRoot();
        JsonObject configuration = NativeRoot.Configuration(NativeRoot.Rid);
        configuration["CMAKE_C_FLAGS"] = value;
        JsonObject recipe = BuildRecipe.Create(root.Authority, NativeRoot.Rid, configuration, NativeRoot.Toolchain(), 12345, BuildRecipe.InputDigests(root.Path).Value);
        Assert.Null(BuildRecipe.RejectLocations(recipe, [root.Path]));
    }

    /// <summary>Recipe inputs are sorted repository-relative paths covering CMake, presets, shim, probe and the engineering code.</summary>
    [Fact]
    public void RecipeInputsAreSortedRepositoryRelativeDigests()
    {
        using var root = new NativeRoot();
        string[] keys = [.. BuildRecipe.InputDigests(root.Path).Value.Select(property => property.Key)];
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        Assert.All(keys, key => Assert.DoesNotContain('\\', key));
        Assert.Contains("native/libchdr/CMakePresets.json", keys);
        Assert.Contains("native/libchdr/probe/libchdr_layout.c", keys);
        Assert.Contains("tools/Moonlark.Native.Engineering/Native/BuildRecipe.cs", keys);
        Assert.Contains("tools/Moonlark.Native.Engineering/Core/SourceIdentity.cs", keys);
        Assert.DoesNotContain("native/libchdr/exports.osx", keys);
        string preset = Path.Combine(root.Path, "native", "libchdr", "CMakePresets.json");
        File.Delete(preset);
        File.CreateSymbolicLink(preset, Path.Combine(TestRepository.Root, "native", "libchdr", "CMakePresets.json"));
        Assert.Contains("regular file", BuildRecipe.InputDigests(root.Path).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The preset must have applied: RID, required settings, enabled features and the macOS floor.</summary>
    [Theory]
    [InlineData("osx-arm64", "CMAKE_OSX_DEPLOYMENT_TARGET", "15.0", "macOS floor")]
    [InlineData("osx-arm64", "CMAKE_OSX_ARCHITECTURES", null, "lacks")]
    [InlineData("linux-x64", "CHDR_WANT_SUBCODE", "OFF", "subcode")]
    [InlineData("linux-x64", "MOONLARK_RID", "linux-arm64", "RID")]
    public void ConfigurationMustReflectThePresetAndFeatures(string rid, string name, string? value, string message)
    {
        JsonObject configuration = NativeRoot.Configuration(rid);
        Assert.Null(BuildRecipe.ValidateConfiguration(configuration, rid, Authorities.Features));
        if (value is null)
            configuration.Remove(name);
        else
            configuration[name] = value;
        Assert.Contains(message, BuildRecipe.ValidateConfiguration(configuration, rid, Authorities.Features)?.Message, StringComparison.Ordinal);
    }

    /// <summary>Each RID accepts only its compiler family.</summary>
    [Theory]
    [InlineData("AppleClang", "osx-arm64", null)]
    [InlineData("GNU", "linux-arm64", null)]
    [InlineData("GNU", "osx-arm64", "Unsupported C compiler")]
    [InlineData("MSVC", "linux-x64", "Unsupported C compiler")]
    public void CompilerFamilyIsEnforcedPerRid(string id, string rid, string? message)
    {
        Failure? failure = NativeToolchain.CheckCompiler(id, rid);
        if (message is null)
            Assert.Null(failure);
        else
            Assert.Contains(message, failure?.Message, StringComparison.Ordinal);
    }

    /// <summary>The File API reply yields the effective cache and the one C compiler identity.</summary>
    [Fact]
    public void FileApiReplyIsRead()
    {
        using var directory = new TemporaryDirectory();
        CMakeFileApi.WriteQueries(directory.Path);
        Assert.True(File.Exists(Path.Combine(directory.Path, ".cmake", "api", "v1", "query", "cache-v2")));
        Assert.True(File.Exists(Path.Combine(directory.Path, ".cmake", "api", "v1", "query", "toolchains-v1")));
        WriteReply(directory.Path);
        CMakeReply reply = CMakeFileApi.Read(directory.Path).Value;
        Assert.Equal(new CompilerIdentity("AppleClang", "21.0.0", "/usr/bin/clang"), reply.Compiler);
        Assert.Contains(reply.Cache, entry => entry is { Name: "CMAKE_BUILD_TYPE", Type: "STRING", Value: "Release" });
    }

    /// <summary>Missing, duplicated, redirected or unexpected replies fail closed.</summary>
    [Theory]
    [InlineData("none", "exactly one reply index")]
    [InlineData("two-indexes", "exactly one reply index")]
    [InlineData("unsafe-path", "Unsafe")]
    [InlineData("wrong-kind", "Unexpected")]
    [InlineData("no-c-toolchain", "C toolchain")]
    [InlineData("error", "rejected")]
    public void FileApiReplyFailsClosed(string change, string message)
    {
        using var directory = new TemporaryDirectory();
        string reply = Path.Combine(directory.Path, ".cmake", "api", "v1", "reply");
        if (change != "none")
            WriteReply(directory.Path);
        JsonObject index = JsonNode.Parse(File.Exists(Path.Combine(reply, "index-1.json")) ? File.ReadAllText(Path.Combine(reply, "index-1.json")) : "{}")!.AsObject();
        switch (change)
        {
            case "two-indexes":
                File.WriteAllText(Path.Combine(reply, "index-2.json"), index.ToJsonString());
                break;
            case "unsafe-path":
                index["reply"]!["cache-v2"]!["jsonFile"] = "../cache.json";
                break;
            case "wrong-kind":
                File.WriteAllText(Path.Combine(reply, "cache.json"), "{\"kind\":\"codemodel\",\"version\":{\"major\":2},\"entries\":[]}");
                break;
            case "no-c-toolchain":
                File.WriteAllText(Path.Combine(reply, "toolchains.json"), "{\"kind\":\"toolchains\",\"version\":{\"major\":1},\"toolchains\":[]}");
                break;
            case "error":
                index["reply"]!["cache-v2"] = new JsonObject { ["error"] = "unknown request kind" };
                break;
        }
        if (change is "unsafe-path" or "error")
            File.WriteAllText(Path.Combine(reply, "index-1.json"), index.ToJsonString());
        Assert.Contains(message, CMakeFileApi.Read(directory.Path).Failure.Message, StringComparison.Ordinal);
    }

    private static CacheEntry[] Cache(string location) =>
    [
        new("BUILD_LTO", "BOOL", "OFF"),
        new("CHDR_VERIFY_BLOCK_CRC", "BOOL", "ON"),
        new("CHDR_MICROFLAC_SOURCE_DIR", "PATH", location + "/microflac"),
        new("CMAKE_BUILD_TYPE", "STRING", "Release"),
        new("CMAKE_CACHEFILE_DIR", "INTERNAL", location + "/build"),
        new("CMAKE_C_COMPILER", "STRING", location + "/bin/clang"),
        new("CMAKE_C_FLAGS_RELEASE", "STRING", "-O3 -DNDEBUG"),
        new("CMAKE_GENERATOR", "INTERNAL", "Ninja"),
        new("CMAKE_INSTALL_PREFIX", "PATH", "/usr/local"),
        new("MINIZ_STDIO", "BOOL", "OFF"),
        new("MOONLARK_RID", "UNINITIALIZED", "linux-x64"),
        new("MOONLARK_UPSTREAM", "UNINITIALIZED", location + "/native/libchdr/upstream"),
        new("chdr_BINARY_DIR", "STATIC", location + "/build/upstream-build"),
    ];

    private static void WriteReply(string buildDirectory)
    {
        string reply = Directory.CreateDirectory(Path.Combine(buildDirectory, ".cmake", "api", "v1", "reply")).FullName;
        File.WriteAllText(Path.Combine(reply, "cache.json"),
            "{\"kind\":\"cache\",\"version\":{\"major\":2,\"minor\":0},\"entries\":[{\"name\":\"CMAKE_BUILD_TYPE\",\"type\":\"STRING\",\"value\":\"Release\",\"properties\":[]}]}");
        File.WriteAllText(Path.Combine(reply, "toolchains.json"),
            "{\"kind\":\"toolchains\",\"version\":{\"major\":1,\"minor\":1},\"toolchains\":[{\"language\":\"C\",\"compiler\":{\"id\":\"AppleClang\",\"version\":\"21.0.0\",\"path\":\"/usr/bin/clang\"}}]}");
        File.WriteAllText(Path.Combine(reply, "index-1.json"),
            "{\"reply\":{\"cache-v2\":{\"jsonFile\":\"cache.json\"},\"toolchains-v1\":{\"jsonFile\":\"toolchains.json\"}}}");
    }
}
