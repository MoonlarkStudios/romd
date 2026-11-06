using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Native;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Native;

/// <summary>
/// Inspection parsers and allowlists. Linux (readelf, nm -D) inspectors run here only against canned tool output;
/// their real tools are exercised only on native runners.
/// </summary>
public sealed class ToolOutputTests
{
    private static readonly string[] Exports = ["chd_close", "chd_read", ExportInventory.BuildInfoExport];

    /// <summary>The export verifier rejects missing, extra and duplicate symbols and returns a sorted set.</summary>
    [Fact]
    public void ExportVerifierRejectsMissingExtraAndDuplicateSymbols()
    {
        foreach (string[] symbols in (string[][])[Exports[..^1], [.. Exports, "ZSTD_decompress"], [.. Exports, Exports[0]]])
            Assert.False(NativeAllowlists.ValidateSymbols(symbols, Exports).Succeeded);
        Assert.Equal<string>(["chd_close", "chd_read", ExportInventory.BuildInfoExport], NativeAllowlists.ValidateSymbols([.. Exports.Reverse()], Exports).Value);
    }

    /// <summary>nm output must be defined, recognized and correctly spelled.</summary>
    [Fact]
    public void NmParserRejectsUndefinedAndUnrecognizedOutput()
    {
        Assert.Equal<string>(["chd_read"], ToolOutput.ParseNm("0000000000010000 T _chd_read", mac: true).Value);
        Assert.Equal<string>(["chd_read"], ToolOutput.ParseNm("0000000000010000 T chd_read\n", mac: false).Value);
        Assert.Empty(ToolOutput.ParseNm("", mac: true).Value);
        foreach (string line in (string[])["0000000000000000 U _chd_read", "0000000000000000 w _chd_read", "garbled output", "0000 T chd_read"])
            Assert.False(ToolOutput.ParseNm(line, mac: true).Succeeded, line);
    }

    /// <summary>Codec, relative and substituted dependencies fail on Linux.</summary>
    [Theory]
    [InlineData("libzstd.so.1")]
    [InlineData("libz.so.1")]
    [InlineData("./libc.so.6")]
    [InlineData("/tmp/libSystem.B.dylib")]
    public void DynamicCodecAndRelativeDependenciesFail(string dependency) =>
        Assert.Contains("dependencies", NativeAllowlists.ValidateDependencies([dependency], "linux-x64").Failure.Message, StringComparison.Ordinal);

    /// <summary>Dependencies must be unique, nonempty and exactly spelled; they are returned sorted.</summary>
    [Fact]
    public void DependenciesAreUniqueNonemptyAndExact()
    {
        Assert.Contains("Duplicate", NativeAllowlists.ValidateDependencies(["libc.so.6", "libc.so.6"], "linux-x64").Failure.Message, StringComparison.Ordinal);
        Assert.Contains("system runtime", NativeAllowlists.ValidateDependencies([], "linux-x64").Failure.Message, StringComparison.Ordinal);
        Assert.Equal<string>(["libc.so.6", "libm.so.6"], NativeAllowlists.ValidateDependencies(["libm.so.6", "libc.so.6"], "linux-x64").Value);
        Assert.False(NativeAllowlists.ValidateDependencies(["LIBC.so.6"], "linux-x64").Succeeded);
    }

    /// <summary>The macOS floor uses minos only, and an RPATH fails.</summary>
    [Fact]
    public void MacOsFloorUsesMinosWithoutOtherCommandVersions()
    {
        const string commands = "cmd LC_BUILD_VERSION\n  minos 14.0\n  version 27037.1\n  version 0.0\n";
        Assert.Equal("14.0", ToolOutput.MacOsMinimum(commands).Value);
        Assert.False(ToolOutput.MacOsMinimum(commands.Replace("minos 14.0", "minos 15.0", StringComparison.Ordinal)).Succeeded);
        Assert.False(ToolOutput.MacOsMinimum(commands + "cmd LC_RPATH\n").Succeeded);
        Assert.False(ToolOutput.MacOsMinimum(commands + "  minos 14.0\n").Succeeded);
    }

    /// <summary>The GLIBC ceiling rejects newer, private and named versions, comparing numerically.</summary>
    [Theory]
    [InlineData("Name: GLIBC_2.17\nName: GLIBC_2.31\n", "2.31")]
    [InlineData("Name: GLIBC_2.2.5\nName: GLIBC_2.14\n", "2.14")]
    [InlineData("Name: GLIBC_2.32", null)]
    [InlineData("Name: GLIBC_PRIVATE", null)]
    [InlineData("Name: GLIBC_ABI_DT_RELR", null)]
    [InlineData("", null)]
    public void LinuxGlibcCeilingRejectsNewPrivateAndNamedVersions(string versions, string? maximum)
    {
        Result<string> result = ToolOutput.LinuxGlibcMaximum(versions);
        Assert.Equal(maximum, result.Succeeded ? result.Value : null);
    }

    /// <summary>otool -L must name the loader-relative install name exactly once.</summary>
    [Fact]
    public void MachOInstallNameMustAppearOnce()
    {
        const string linked = "/x/libmoonlark_chdr.dylib:\n\t@loader_path/libmoonlark_chdr.dylib (compatibility version 0.0.0)\n\t/usr/lib/libSystem.B.dylib (compatibility version 1.0.0)";
        Assert.Equal<string>(["/usr/lib/libSystem.B.dylib"], ToolOutput.ParseMachODependencies(linked, "libmoonlark_chdr.dylib").Value);
        Assert.False(ToolOutput.ParseMachODependencies(linked.Replace("@loader_path", "@rpath", StringComparison.Ordinal), "libmoonlark_chdr.dylib").Succeeded);
    }

    /// <summary>Canned macOS output parses exports, dependencies and minos; a universal binary or an invalid signature fails.</summary>
    [Fact]
    public void MacOsInspectorParsesCannedToolsAndFailsClosed()
    {
        const string binary = "/x/libmoonlark_chdr.dylib";
        Dictionary<string, string> tools = new(StringComparer.Ordinal)
        {
            ["lipo -archs"] = "arm64",
            ["nm -gU"] = "0000000000001000 T _chd_close\n0000000000001010 T _chd_read\n0000000000001020 T _moonlark_chdr_build_info",
            ["otool -L"] = binary + ":\n\t@loader_path/libmoonlark_chdr.dylib (compatibility version 0.0.0, current version 0.0.0)\n" +
                "\t/usr/lib/libSystem.B.dylib (compatibility version 1.0.0, current version 1359.0.0)",
            ["otool -l"] = "Load command 9\n      cmd LC_BUILD_VERSION\n  cmdsize 32\n platform 1\n    minos 14.0\n      sdk 27.0\n",
            ["codesign --verify"] = "",
        };
        PlatformFacts facts = BinaryInspection.Platform("osx-arm64", binary, Canned(tools)).Value;
        Assert.Equal(Exports, facts.Symbols);
        Assert.Equal<string>(["/usr/lib/libSystem.B.dylib"], facts.Dependencies);
        Assert.Equal("14.0", (string)facts.Platform["minimumOs"]!);
        tools.Remove("codesign --verify");
        Assert.Contains("code signature", BinaryInspection.Platform("osx-arm64", binary, Canned(tools)).Failure.Message, StringComparison.Ordinal);
        tools["lipo -archs"] = "x86_64 arm64";
        Assert.Contains("architecture", BinaryInspection.Platform("osx-arm64", binary, Canned(tools)).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Canned Linux output parses architecture, exports, needed libraries and the GLIBC maximum.</summary>
    [Fact]
    public void LinuxInspectorParsesCannedReadelfAndNm()
    {
        PlatformFacts facts = BinaryInspection.Platform("linux-x64", "/x/libmoonlark_chdr.so", Canned(Linux())).Value;
        Assert.Equal(Exports, facts.Symbols);
        Assert.Equal<string>(["libm.so.6", "libc.so.6"], facts.Dependencies);
        Assert.Equal("2.17", (string)facts.Platform["maximumRequiredGlibc"]!);
        Assert.Equal("Advanced Micro Devices X86-64", (string)facts.Platform["architecture"]!);
    }

    /// <summary>Canned Linux output with a wrong machine, an executable, an RUNPATH or a newer GLIBC fails.</summary>
    [Theory]
    [InlineData("linux-arm64", "readelf -h", null, "ELF architecture")]
    [InlineData("linux-x64", "readelf -h", "Type:                              EXEC (Executable file)\n  Machine:                           Advanced Micro Devices X86-64", "shared library")]
    [InlineData("linux-x64", "readelf -d", " 0x000000000000001d (RUNPATH)            Library runpath: [/opt]", "RPATH")]
    [InlineData("linux-x64", "readelf --version-info", "  0x0010:   Name: GLIBC_2.34  Flags: none  Version: 3", "GLIBC")]
    public void LinuxInspectorFailsClosed(string rid, string tool, string? replacement, string message)
    {
        Dictionary<string, string> tools = Linux();
        if (replacement is not null)
            tools[tool] = replacement;
        Assert.Contains(message, BinaryInspection.Platform(rid, "/x/libmoonlark_chdr.so", Canned(tools)).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Windows is not a supported inspection target.</summary>
    [Fact]
    public void WindowsInspectionIsUnsupported() =>
        Assert.Contains("Unsupported RID", BinaryInspection.Platform("win-x64", "C:/x/moonlark_chdr.dll", Canned([])).Failure.Message, StringComparison.Ordinal);

    private static Dictionary<string, string> Linux() => new(StringComparer.Ordinal)
    {
        ["readelf -h"] = "ELF Header:\n  Class:                             ELF64\n  Type:                              DYN (Shared object file)\n  Machine:                           Advanced Micro Devices X86-64\n",
        ["nm -D"] = "0000000000001000 T chd_close\n0000000000001010 T chd_read\n0000000000001020 T moonlark_chdr_build_info",
        ["readelf -d"] = "Dynamic section at offset 0x2de0 contains 26 entries:\n  Tag        Type                         Name/Value\n" +
            " 0x0000000000000001 (NEEDED)             Shared library: [libm.so.6]\n 0x0000000000000001 (NEEDED)             Shared library: [libc.so.6]\n" +
            " 0x000000000000000e (SONAME)             Library soname: [libmoonlark_chdr.so]\n",
        ["readelf --version-info"] = "Version needs section '.gnu.version_r' contains 2 entries:\n  000000: Version: 1  File: libm.so.6  Cnt: 1\n" +
            "  0x0010:   Name: GLIBC_2.2.5  Flags: none  Version: 3\n  0x0020: Version: 1  File: libc.so.6  Cnt: 1\n  0x0030:   Name: GLIBC_2.17  Flags: none  Version: 2\n",
    };

    /// <summary>Answers a tool invocation by its first two arguments; an unexpected command fails like a missing tool.</summary>
    private static Func<IReadOnlyList<string>, Result<string>> Canned(Dictionary<string, string> outputs) => command =>
        outputs.TryGetValue(command[0] + " " + command[1], out string? output) || outputs.TryGetValue(command[0], out output)
            ? output
            : new Failure("Unexpected command: " + string.Join(' ', command));
}
