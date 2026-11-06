using System.Collections.Immutable;
using Moonlark.Native.Engineering.Cli;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Native;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Native;

/// <summary>Build ordering, output hygiene, default-log guards and export derivation.</summary>
public sealed class NativeBuildTests
{
    /// <summary>Unix CMake arguments preserve spaces and literal backslashes, including every custom path value.</summary>
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    public void UnixConfigureArgumentsPreserveLiteralBackslashes(string rid)
    {
        const string root = @"/tmp/romd root\literal";
        const string source = @"/tmp/upstream source\literal";
        const string output = @"/tmp/artifacts/output\literal";
        const string compiler = @"/tmp/compiler tools/clang\literal";
        var request = new NativeBuildRequest(root, rid, source, output, compiler, TestRepository.CleanEnvironment);
        var layout = new OutputLayout(output, rid);

        Assert.Equal(
        [
            "cmake", "-S", Path.Combine(root, "native", "libchdr"), "-B", layout.Build, "--preset", rid,
            "-DCMAKE_C_COMPILER=" + compiler, "-DMOONLARK_UPSTREAM=" + source,
            "-DMOONLARK_SOURCE_ROOT=" + root, "-DMOONLARK_NATIVE_OUTPUT=" + layout.Native,
            "-DMOONLARK_PROBE_OUTPUT=" + output, "-DMOONLARK_BUILD_INFO_HEADER=" + layout.BuildInfoHeader,
        ], NativeBuild.ConfigureArguments(request, layout));

        Assert.Equal(root, request.Root);
        Assert.Equal(source, request.Source);
        Assert.Equal(output, request.Output);
        Assert.Equal(compiler, request.Compiler);
        Assert.Same(TestRepository.CleanEnvironment, request.Environment);
        Assert.Equal(output, layout.Output);
    }

    /// <summary>A rebuild that fails its source check removes the old manifest and probe receipt before touching caches.</summary>
    [Fact]
    public void EarlyFailedBuildInvalidatesOldManifest()
    {
        using var root = new NativeRoot();
        (string source, LibchdrPin pin) = SyntheticSource.Create(root.Path);
        root.WriteAuthorityFor(pin);
        File.WriteAllText(Path.Combine(source, "implementation.c"), "unreviewed implementation");
        string output = Path.Combine(root.Path, "artifacts", "selected-output");
        string cache = Path.Combine(output, "build", "CMakeCache.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "sentinel: prepare never ran");
        File.WriteAllText(Path.Combine(output, NativeOutput.ManifestName), "old successful manifest");
        File.WriteAllText(Path.Combine(output, LayoutProbe.ReceiptName), "old probe receipt");
        var request = new NativeBuildRequest(root.Path, NativeRids.Host().Value, source, output, "clang", TestRepository.CleanEnvironment);
        Assert.Contains("dirty", NativeBuild.Run(request, null).Failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(output, NativeOutput.ManifestName)));
        Assert.False(File.Exists(Path.Combine(output, LayoutProbe.ReceiptName)));
        Assert.True(File.Exists(cache));
    }

    /// <summary>A build for another RID is refused after invalidating its receipts.</summary>
    [Fact]
    public void CrossCompilationIsRefused()
    {
        using var root = new NativeRoot();
        string other = NativeRids.Supported.First(rid => rid != NativeRids.Host().Value);
        string output = Path.Combine(root.Path, "artifacts", "cross");
        var request = new NativeBuildRequest(root.Path, other, Path.Combine(root.Path, "missing"), output, "clang", TestRepository.CleanEnvironment);
        Assert.Contains("Cross-compilation", NativeBuild.Run(request, null).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The CMake cache is recreated and symlinked output children are refused.</summary>
    [Fact]
    public void BuildCacheIsRecreatedAndDirectorySymlinksAreRefused()
    {
        using var directory = new TemporaryDirectory();
        string output = Path.Combine(directory.Path, "artifacts", "build-test");
        Directory.CreateDirectory(Path.Combine(output, "build"));
        File.WriteAllText(Path.Combine(output, "build", "CMakeCache.txt"), "unreviewed flags");
        Assert.Null(NativeOutput.Prepare(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(output, "native")));
        Assert.False(Directory.Exists(Path.Combine(output, "build")));
        foreach (string child in (string[])["build", "native", "generated"])
        {
            string path = Path.Combine(output, child);
            if (Directory.Exists(path))
                Directory.Delete(path);
            Directory.CreateSymbolicLink(path, directory.Path);
            Assert.Contains("symlink", NativeOutput.Prepare(output)?.Message, StringComparison.Ordinal);
            Directory.Delete(path);
        }
    }

    /// <summary>The generated include cache cannot retain unrecorded headers.</summary>
    [Fact]
    public void GeneratedIncludeCacheCannotRetainUnrecordedHeaders()
    {
        using var directory = new TemporaryDirectory();
        string output = Path.Combine(directory.Path, "artifacts", "build-test");
        Directory.CreateDirectory(Path.Combine(output, "generated"));
        File.WriteAllText(Path.Combine(output, "generated", "stdint.h"), "unrecorded transitive include");
        Assert.Null(NativeOutput.Prepare(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(output, "generated")));
    }

    /// <summary>A symlinked probe binary is refused; old receipts are removed without following links.</summary>
    [Fact]
    public void InvalidationRemovesReceiptsAndRefusesSymlinkedProbe()
    {
        using var directory = new TemporaryDirectory();
        string sentinel = Path.Combine(directory.Path, "sentinel.txt");
        File.WriteAllText(sentinel, "sentinel");
        var layout = new OutputLayout(directory.Path, "osx-arm64");
        File.CreateSymbolicLink(layout.Manifest, sentinel);
        File.WriteAllText(layout.ProbeReceipt, "old");
        Assert.Null(NativeOutput.Invalidate(layout));
        Assert.False(File.Exists(layout.Manifest) || File.Exists(layout.ProbeReceipt));
        Assert.Equal("sentinel", File.ReadAllText(sentinel));
        File.CreateSymbolicLink(Path.Combine(directory.Path, LayoutProbe.BinaryName), sentinel);
        Assert.Contains("symlink", NativeOutput.Invalidate(layout)?.Message, StringComparison.Ordinal);
    }

    /// <summary>A symlinked artifacts root cannot make the default build log create directories in source.</summary>
    [Fact]
    public void DefaultBuildLogCannotCreateDirectoriesInSource()
    {
        using var directory = new TemporaryDirectory();
        string source = Directory.CreateDirectory(Path.Combine(directory.Path, "src")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(directory.Path, "artifacts"), source);
        (int code, string error) = Run(NativeCommand.Build, directory.Path, "--rid", "osx-arm64");
        Assert.Equal(1, code);
        Assert.Contains("artifacts", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(source, "native")));
    }

    /// <summary>The default build log cannot append through a symlink.</summary>
    [Fact]
    public void DefaultBuildLogCannotAppendThroughSymlink()
    {
        using var directory = new TemporaryDirectory();
        string source = Path.Combine(directory.Path, "src", "existing-source.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "source sentinel\n");
        string log = Path.Combine(directory.Path, "artifacts", "native", "libchdr", "osx-arm64", "build.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.CreateSymbolicLink(log, source);
        Assert.Equal(1, Run(NativeCommand.Build, directory.Path, "--rid", "osx-arm64").Code);
        Assert.Equal("source sentinel\n", File.ReadAllText(source));
    }

    /// <summary>The default verify log cannot append through a symlink either.</summary>
    [Fact]
    public void DefaultVerifyLogCannotAppendThroughSymlink()
    {
        using var directory = new TemporaryDirectory();
        string sentinel = Path.Combine(directory.Path, "sentinel.cs");
        File.WriteAllText(sentinel, "source sentinel\n");
        File.CreateSymbolicLink(Path.Combine(directory.Path, "verify.log"), sentinel);
        Assert.Equal(1, Run(NativeCommand.Verify, directory.Path, "--manifest", Path.Combine(directory.Path, "build-manifest.json")).Code);
        Assert.Equal("source sentinel\n", File.ReadAllText(sentinel));
    }

    /// <summary>Only supported RIDs are accepted.</summary>
    [Fact]
    public void UnsupportedRidIsRejected()
    {
        using var directory = new TemporaryDirectory();
        (int code, string error) = Run(NativeCommand.Build, directory.Path, "--rid", "linux-musl-x64");
        Assert.Equal(1, code);
        Assert.Contains("Unsupported RID", error, StringComparison.Ordinal);
    }

    /// <summary>Exports derive from exports.txt alone; the committed platform lists are no longer inputs.</summary>
    [Fact]
    public void ExportsDeriveFromTheAllowlistAlone()
    {
        using var root = new NativeRoot();
        ImmutableArray<string> expected = NativeAllowlists.ExpectedExports(root.Path, null).Value;
        Assert.Equal(ExportInventory.BuildInfoExport, expected[^1]);
        Assert.False(File.Exists(Path.Combine(root.Path, "native", "libchdr", "exports.osx")));
        string allowlist = Path.Combine(root.Path, ExportInventory.AllowlistPath);
        File.WriteAllText(allowlist, File.ReadAllText(allowlist).Replace("chd_read\n", "zstd_decompress\n", StringComparison.Ordinal));
        Assert.Contains("spelling", NativeAllowlists.ExpectedExports(root.Path, null).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The export allowlist must match the pinned header's exact declaration sequence when a source is given.</summary>
    [Fact]
    public void ExportsMustMatchThePinnedHeaderSequence()
    {
        using var root = new NativeRoot();
        string source = Path.Combine(TestRepository.Root, "native", "libchdr", "upstream");
        Assert.True(NativeAllowlists.ExpectedExports(root.Path, source).Succeeded);
        string allowlist = Path.Combine(root.Path, ExportInventory.AllowlistPath);
        string[] lines = File.ReadAllText(allowlist).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        File.WriteAllText(allowlist, string.Join('\n', [lines[1], lines[0], .. lines[2..]]) + "\n");
        Assert.Contains("pinned public header", NativeAllowlists.ExpectedExports(root.Path, source).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>CMake generates every linker export file from exports.txt; nothing reads the committed platform lists.</summary>
    [Fact]
    public void CMakeListsReadsOnlyTheExportAllowlist()
    {
        string cmake = File.ReadAllText(Path.Combine(TestRepository.Root, "native", "libchdr", "CMakeLists.txt"));
        Assert.Contains("exports.txt", cmake, StringComparison.Ordinal);
        foreach (string committed in (string[])["exports.osx", "exports.map", "exports.def"])
            Assert.DoesNotContain(committed, cmake, StringComparison.Ordinal);
    }

    private static (int Code, string Error) Run(Func<IReadOnlyList<string>, CommandContext, int> command, string root, params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = command(arguments, new CommandContext(root, output, error, TestRepository.CleanEnvironment));
        return (code, error.ToString());
    }
}
