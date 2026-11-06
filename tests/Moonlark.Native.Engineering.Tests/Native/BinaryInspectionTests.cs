using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Native;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Native;

/// <summary>
/// Whole-method inspection per RID: real processes run fake platform tools found only on an explicit PATH, while the host
/// RID and the in-process build-info reader are substituted, so each RID branch is composed exactly as on its native host.
/// </summary>
public sealed class BinaryInspectionTests
{
    /// <summary>Every supported RID.</summary>
    public static TheoryData<string> Rids { get; } = new(NativeRids.Supported);

    /// <summary>Each RID's tool output is parsed, validated against both allowlists and the build-info, and recorded normalized.</summary>
    [Theory]
    [MemberData(nameof(Rids))]
    public void EachRidComposesToolsAllowlistsAndBuildInfo(string rid)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var inspection = new InspectionCase(rid);
        Inspection result = inspection.Inspect().Value;
        Assert.Equal<string>(CannedTools.Exports, result.Symbols);
        string[] dependencies = rid switch
        {
            "osx-arm64" => ["/usr/lib/libSystem.B.dylib"],
            _ => ["libc.so.6", "libm.so.6"],
        };
        Assert.Equal<string>(dependencies, result.Dependencies);
        Assert.True(JsonNode.DeepEquals(CannedTools.Platform(rid), result.Platform), result.Platform.ToJsonString());
        Assert.Equal<string>([inspection.Binary], inspection.Reads);
        Assert.Contains("Native build-info: " + JsonFields.Compact(InspectionCase.Info), inspection.Log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A binary is inspected only on its own host; nothing runs or loads for another RID.</summary>
    [Theory]
    [MemberData(nameof(Rids))]
    public void InspectionRequiresTheBinarysNativeHost(string rid)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var inspection = new InspectionCase(rid);
        string other = NativeRids.Supported.First(candidate => candidate != rid);
        Assert.Contains("requires its native host", inspection.Inspect(host: other).Failure.Message, StringComparison.Ordinal);
        Assert.Empty(inspection.Reads);
    }

    /// <summary>The production overload inspects only this machine's RID and loads the binary in-process for real.</summary>
    [Fact]
    public void DefaultHostIsThisMachineAndItsInProcessLoader()
    {
        if (OperatingSystem.IsWindows())
            return;
        string host = NativeRids.Host().Value;
        using var foreign = new InspectionCase(NativeRids.Supported.First(rid => rid != host));
        Assert.Contains("requires its native host", foreign.InspectOnThisHost().Failure.Message, StringComparison.Ordinal);
        using var native = new InspectionCase(host);
        Assert.Contains("Native binary could not be loaded", native.InspectOnThisHost().Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>An unresolvable host is reported as is.</summary>
    [Fact]
    public void UnsupportedHostFailureIsReturned()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var inspection = new InspectionCase("osx-arm64");
        Assert.Equal("Unsupported native host: synthetic", inspection.Inspect(host: new Failure("Unsupported native host: synthetic")).Failure.Message);
    }

    /// <summary>A missing binary or a symlink in its place is refused before any tool runs.</summary>
    [Theory]
    [MemberData(nameof(Rids))]
    public void BinaryMustBeARegularFile(string rid)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var inspection = new InspectionCase(rid);
        string real = inspection.Binary + ".real";
        File.Move(inspection.Binary, real);
        Assert.Contains("regular file", inspection.Inspect().Failure.Message, StringComparison.Ordinal);
        File.CreateSymbolicLink(inspection.Binary, real);
        Assert.Contains("regular file", inspection.Inspect().Failure.Message, StringComparison.Ordinal);
        Assert.Empty(inspection.Reads);
    }

    /// <summary>Another RID's library filename is refused even when every tool would accept it.</summary>
    [Theory]
    [MemberData(nameof(Rids))]
    public void BinaryMustHaveTheRidsLibraryFileName(string rid)
    {
        if (OperatingSystem.IsWindows())
            return;
        string other = NativeRids.LibraryFileName(NativeRids.Supported.First(candidate => NativeRids.LibraryFileName(candidate) != NativeRids.LibraryFileName(rid)));
        using var inspection = new InspectionCase(rid, fileName: other);
        Assert.Contains("Unexpected native binary filename", inspection.Inspect().Failure.Message, StringComparison.Ordinal);
        Assert.Empty(inspection.Reads);
    }

    /// <summary>Reported exports must be exactly the allowlist; an extra or missing export stops inspection before loading.</summary>
    [Theory]
    [MemberData(nameof(Rids))]
    public void ExportsMustBeExactlyTheAllowlist(string rid)
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (string[] symbols in (string[][])[[.. CannedTools.Exports, "ZSTD_decompress"], CannedTools.Exports[..^1]])
        {
            using var inspection = new InspectionCase(rid, symbols: symbols);
            Assert.Contains("Export mismatch", inspection.Inspect().Failure.Message, StringComparison.Ordinal);
            Assert.Empty(inspection.Reads);
        }
    }

    /// <summary>A dynamic codec dependency stops inspection before loading.</summary>
    [Theory]
    [InlineData("osx-arm64", "/usr/local/lib/libzstd.1.dylib")]
    [InlineData("linux-x64", "libzstd.so.1")]
    [InlineData("linux-arm64", "libz.so.1")]
    public void DependenciesMustBeAllowlisted(string rid, string codec)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var inspection = new InspectionCase(rid, dependencies: [.. CannedTools.Dependencies(rid), codec]);
        Assert.Contains("Unexpected dynamic dependencies: [" + codec + "]", inspection.Inspect().Failure.Message, StringComparison.Ordinal);
        Assert.Empty(inspection.Reads);
    }

    /// <summary>The loaded build-info must equal the recipe's; a load failure is returned.</summary>
    [Theory]
    [MemberData(nameof(Rids))]
    public void BuildInfoMustEqualTheRecipe(string rid)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var inspection = new InspectionCase(rid);
        JsonObject other = InspectionCase.Info;
        other["buildId"] = "other recipe";
        Assert.Contains("Native build-info differs", inspection.Inspect(buildInfo: other).Failure.Message, StringComparison.Ordinal);
        Assert.Equal("Native binary could not be loaded: synthetic",
            inspection.Inspect(buildInfo: new Failure("Native binary could not be loaded: synthetic")).Failure.Message);
    }

    /// <summary>A failing platform tool or rejected platform fact stops inspection before the binary is loaded in-process.</summary>
    [Theory]
    [InlineData("osx-arm64", "codesign --verify", "invalid signature", 1, "code signature is invalid")]
    [InlineData("osx-arm64", "lipo -archs", "x86_64 arm64", 0, "Unexpected Mach-O architecture")]
    [InlineData("linux-x64", "readelf --version-info", "  0x0010:   Name: GLIBC_2.34  Flags: none  Version: 3", 0, "GLIBC floor exceeds 2.31: 2.34")]
    [InlineData("linux-x64", "readelf -d", " 0x000000000000001d (RUNPATH)            Library runpath: [/opt]", 0, "RPATH/RUNPATH")]
    [InlineData("linux-arm64", "readelf -h", "  Type:                              DYN (Shared object file)\n  Machine:                           Advanced Micro Devices X86-64", 0,
        "Unexpected ELF architecture")]
    public void PlatformFailuresStopBeforeTheBinaryIsLoaded(string rid, string tool, string output, int exitCode, string message)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var inspection = new InspectionCase(rid);
        inspection.Replies[tool] = (inspection.Replies[tool].Command, new CannedReply(output, exitCode));
        Assert.Contains(message, inspection.Inspect().Failure.Message, StringComparison.Ordinal);
        Assert.Empty(inspection.Reads);
    }
}

/// <summary>A synthetic binary named for its RID, canned tool replies for it, and a recording build-info reader.</summary>
internal sealed class InspectionCase : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly List<string> _reads = [];

    internal InspectionCase(string rid, string? fileName = null, IReadOnlyList<string>? symbols = null, IReadOnlyList<string>? dependencies = null)
    {
        Rid = rid;
        Binary = Path.Combine(_directory.Path, "native", fileName ?? NativeRids.LibraryFileName(rid));
        Directory.CreateDirectory(Path.GetDirectoryName(Binary)!);
        File.WriteAllBytes(Binary, "synthetic binary; never loaded"u8.ToArray());
        // Reported in reverse so the result's ordinal sort is observable.
        Replies = CannedTools.For(rid, Binary, symbols ?? [.. CannedTools.Exports.Reverse()], dependencies ?? CannedTools.Dependencies(rid));
    }

    /// <summary>The build-info the recipe expects and the substituted reader returns by default.</summary>
    internal static JsonObject Info => new() { ["abiVersion"] = 1, ["buildId"] = "synthetic recipe" };

    internal string Rid { get; }

    internal string Binary { get; }

    internal Dictionary<string, (string[] Command, CannedReply Reply)> Replies { get; }

    /// <summary>Paths the build-info reader was asked to load, in order.</summary>
    internal IReadOnlyList<string> Reads => _reads;

    internal StringWriter Log { get; } = new();

    [UnsupportedOSPlatform("windows")]
    internal Result<Inspection> Inspect(Result<string>? host = null, Result<JsonObject>? buildInfo = null)
    {
        var inspectionHost = new InspectionHost(() => host ?? Rid, binary =>
        {
            _reads.Add(binary);
            return buildInfo ?? Info;
        });
        return BinaryInspection.Inspect(Binary, Rid, CannedTools.Exports, Info, InstallTools(), Log, inspectionHost);
    }

    /// <summary>Inspects through the production overload: this machine's RID and the real in-process loader.</summary>
    [UnsupportedOSPlatform("windows")]
    internal Result<Inspection> InspectOnThisHost() => BinaryInspection.Inspect(Binary, Rid, CannedTools.Exports, Info, InstallTools(), Log);

    public void Dispose()
    {
        Log.Dispose();
        _directory.Dispose();
    }

    [UnsupportedOSPlatform("windows")]
    private IReadOnlyDictionary<string, string> InstallTools() => FakeTools.OnlyOnPath(FakeTools.Install(Path.Combine(_directory.Path, "tools"), Replies.Values));
}
