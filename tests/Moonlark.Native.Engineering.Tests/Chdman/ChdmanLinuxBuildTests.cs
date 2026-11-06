using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Chdman;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Chdman;

/// <summary>Linux chdman recipe contracts, using synthetic tool output and files without executing native tools.</summary>
public sealed class ChdmanLinuxBuildTests
{
    private const string Source = "/source";
    private static readonly string LinkSource = Path.GetFullPath(Source);
    private const string Tool = "/output/native/chdman";
    private const string Commit = "f34f02505e32c1993c6a782b6814232cbfc74e36";
    private const string VersionBanner = "chdman - MAME Compressed Hunks of Data (CHD) manager 0.289 (" + Commit + ")";
    private const string Header = "ELF Header:\n  Class:                             ELF64\n  Data:                              2's complement, little endian\n  Type:                              DYN (Position-Independent Executable file)\n  Machine:                           AArch64\n";
    private const string Programs = "Program Headers:\n  INTERP         0x0000000000000238\n      [Requesting program interpreter: /lib/ld-linux-aarch64.so.1]\n";
    private const string Dynamic = "Dynamic section at offset 0x100:\n 0x0000000000000001 (NEEDED)             Shared library: [libm.so.6]\n 0x0000000000000001 (NEEDED)             Shared library: [libgcc_s.so.1]\n 0x0000000000000001 (NEEDED)             Shared library: [libc.so.6]\n 0x000000006ffffffb (FLAGS_1)            Flags: PIE\n";
    private const string Versions = "Version needs section:\n  Name: GLIBC_2.17\n  Name: GLIBC_2.28\n";
    private const string Sections = "Section Headers:\n  [ 1] .interp           PROGBITS\n  [ 2] .dynsym           DYNSYM\n  [ 3] .gnu.version      VERSYM\n";
    private const string BundledInputs = "ifeq ($(config),release64)\n  LDDEPS += ../../../../linux_clang/bin/x64/Release/libutils.a ../../../../linux_clang/bin/x64/Release/libexpat.a ../../../../linux_clang/bin/x64/Release/lib7z.a ../../../../linux_clang/bin/x64/Release/libocore_sdl.a ../../../../linux_clang/bin/x64/Release/libzlib.a ../../../../linux_clang/bin/x64/Release/libzstd.a ../../../../linux_clang/bin/x64/Release/libflac.a ../../../../linux_clang/bin/x64/Release/libutf8proc.a\nendif\n";

    /// <summary>Both Linux RIDs and the existing Mac RID default only to their native OS/process architecture.</summary>
    [Theory]
    [InlineData("linux", Architecture.X64, "linux-x64")]
    [InlineData("linux", Architecture.Arm64, "linux-arm64")]
    [InlineData("macos", Architecture.Arm64, "osx-arm64")]
    public void NativeHostSelectsTheMatchingRid(string os, Architecture architecture, string rid)
    {
        Assert.Equal(rid, ChdmanBuild.SelectRid(null, os, architecture, architecture).Value);
        Assert.Equal(rid, ChdmanBuild.SelectRid(rid, os, architecture, architecture).Value);
    }

    /// <summary>Unsupported or emulated hosts and mismatched requested RIDs do not qualify.</summary>
    [Theory]
    [InlineData("linux-x64", "linux", Architecture.Arm64, Architecture.Arm64)]
    [InlineData("linux-arm64", "macos", Architecture.Arm64, Architecture.Arm64)]
    [InlineData(null, "linux", Architecture.Arm64, Architecture.X64)]
    [InlineData(null, "macos", Architecture.X64, Architecture.X64)]
    [InlineData(null, "windows", Architecture.X64, Architecture.X64)]
    [InlineData("win-x64", "windows", Architecture.X64, Architecture.X64)]
    [InlineData("linux-musl-x64", "linux", Architecture.X64, Architecture.X64)]
    public void CrossOrUnsupportedHostIsRejected(string? requested, string os, Architecture osArchitecture, Architecture processArchitecture) =>
        Assert.Contains("native host", ChdmanBuild.SelectRid(requested, os, osArchitecture, processArchitecture).Failure.Message, StringComparison.Ordinal);

    /// <summary>GENie uses the Linux Clang route without system codec switches or warning suppression.</summary>
    [Theory]
    [InlineData("linux-x64", "x64")]
    [InlineData("linux-arm64", "arm64")]
    public void LinuxGenerationKeepsBundledCodecsAndMappedSources(string rid, string platform)
    {
        ImmutableArray<string> arguments = ChdmanBuild.GenerateArguments(Source, "21.1.8", rid);
        Assert.Equal(Path.Combine(Source, "3rdparty/genie/bin/linux/genie"), arguments[0]);
        Assert.Contains("--with-tools", arguments);
        Assert.Contains("--osd=sdl", arguments);
        Assert.Contains("--targetos=linux", arguments);
        Assert.Contains("--gcc=linux-clang", arguments);
        Assert.Contains("--PLATFORM=" + platform, arguments);
        Assert.Contains("--CC=clang", arguments);
        Assert.Contains("--CXX=clang++", arguments);
        Assert.Contains("--STRIP_SYMBOLS=1", arguments);
        Assert.Contains(arguments, value => value.StartsWith("--ARCHOPTS=", StringComparison.Ordinal)
            && value.Contains("-ffile-prefix-map=/source=/_/mame", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, value => value.Contains("with-system", StringComparison.Ordinal)
            || value.Contains("NOWERROR", StringComparison.Ordinal) || value.Contains("with-emulator", StringComparison.Ordinal));
        Assert.Equal<string>(
        [
            Path.Combine(Source, "3rdparty/genie/bin/linux/genie"), "--with-tools",
            "--target=mame", "--subtarget=mame", "--osd=sdl", "--targetos=linux",
            "--PLATFORM=" + platform, "--build-dir=build", "--gcc=linux-clang",
            "--gcc_version=21.1.8", "--CC=clang", "--CXX=clang++", "--OPTIMIZE=3", "--NOASM=1",
            "--SYMBOLS=0", "--STRIP_SYMBOLS=1", "--SEPARATE_BIN=1",
            "--NO_USE_MIDI=1", "--NO_X11=1", "--NO_USE_XINPUT=1",
            "--ARCHOPTS=-ffile-prefix-map=/source=/_/mame -fdebug-prefix-map=/source=/_/mame -ffunction-sections -fdata-sections",
            "--LDOPTS=-static-libstdc++ -Wl,--as-needed,--gc-sections,--fatal-warnings", "gmake",
        ], arguments);
    }

    /// <summary>Common C/C++ section splitting remains beside the separately scoped genuine-header include option.</summary>
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public void LinuxCommonSectionOptionsRemainAlongsideExplicitCppHeaders(string rid)
    {
        ImmutableArray<string> arguments = ChdmanBuild.GenerateArguments(Source, "21.1.8", rid, "/sdl-headers/include");
        Assert.Equal("--ARCHOPTS=-ffile-prefix-map=/source=/_/mame -fdebug-prefix-map=/source=/_/mame -ffunction-sections -fdata-sections",
            Assert.Single(arguments, value => value.StartsWith("--ARCHOPTS=", StringComparison.Ordinal)));
        Assert.Equal("--ARCHOPTS_CXX=-I/sdl-headers/include",
            Assert.Single(arguments, value => value.StartsWith("--ARCHOPTS_CXX=", StringComparison.Ordinal)));
        Assert.Equal("gmake", arguments[^1]);
    }

    /// <summary>The recorded final link uses only bundled archives and a static C++ runtime.</summary>
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public void LinuxLinkRequiresStaticCppRuntimeAndOnlyChdman(string rid)
    {
        ImmutableArray<string> arguments = ChdmanBuild.LinkArguments(1, rid, Source);
        Assert.Contains("build/projects/sdl/mame/gmake-linux-clang", arguments);
        Assert.Contains("-j1", arguments);
        Assert.Equal("chdman", arguments[^1]);
        Assert.Contains(arguments, value => value.StartsWith("ALL_LDFLAGS=", StringComparison.Ordinal)
            && value.Contains("-static-libstdc++", StringComparison.Ordinal)
            && value.Contains("--fatal-warnings", StringComparison.Ordinal));
        Assert.Contains(arguments, value => value.StartsWith("LIBS=$(LDDEPS)", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, value => value.Contains("-lSDL", StringComparison.Ordinal)
            || value.Contains("-lfontconfig", StringComparison.Ordinal) || value.Contains("-lz ", StringComparison.Ordinal));
        Assert.Equal<string>(
        [
            "make", "-C", "build/projects/sdl/mame/gmake-linux-clang", "config=release64", "-j1",
            "LIBS=$(LDDEPS) -lpthread -ldl -lm -lutil",
            "ALL_LDFLAGS=-static-libstdc++ -Wl,--as-needed,--gc-sections,--fatal-warnings,--build-id=sha1,-Map," + Path.Combine(Source, "build/chdman.link.map"),
            "chdman",
        ], arguments);
    }

    /// <summary>The existing Mac command vectors remain byte-for-byte equal through the RID overloads.</summary>
    [Fact]
    public void ExistingMacCommandsRemainEqual()
    {
        Assert.Equal<string>(ChdmanBuild.GenerateArguments(Source, "21.0.0"), ChdmanBuild.GenerateArguments(Source, "21.0.0", "osx-arm64"));
        Assert.Equal<string>(ChdmanBuild.LinkArguments(4), ChdmanBuild.LinkArguments(4, "osx-arm64", Source));
    }

    /// <summary>The source-derived archive set excludes system codecs and emulator libraries.</summary>
    [Fact]
    public void GeneratedLinkInputsRequireExactlyTheBundledArchives()
    {
        string externalArchive = Path.Combine(Path.GetPathRoot(LinkSource)!, "usr", "lib", "libzlib.a");
        Assert.True(Path.IsPathFullyQualified(LinkSource));
        Assert.True(Path.IsPathFullyQualified(externalArchive));
        Assert.False(ArtifactsPath.IsWithin(externalArchive, LinkSource));
        Assert.Null(ChdmanBuild.ValidateLinuxLinkInputs(BundledInputs, LinkSource));
        Assert.Contains("bundled", ChdmanBuild.ValidateLinuxLinkInputs(BundledInputs.Replace("libzlib.a", "libSDL2.a", StringComparison.Ordinal), LinkSource)?.Message, StringComparison.Ordinal);
        Assert.Contains("bundled", ChdmanBuild.ValidateLinuxLinkInputs(BundledInputs.Replace("../../../../linux_clang/bin/x64/Release/libzlib.a ", "", StringComparison.Ordinal), LinkSource)?.Message, StringComparison.Ordinal);
        Assert.Contains("bundled", ChdmanBuild.ValidateLinuxLinkInputs(BundledInputs.Replace("libutf8proc.a\n", "libutf8proc.a " + externalArchive + "\n", StringComparison.Ordinal), LinkSource)?.Message, StringComparison.Ordinal);
    }

    /// <summary>An inactive debug configuration cannot supply an archive missing from selected release64.</summary>
    [Fact]
    public void InactiveConfigurationCannotCompleteSelectedReleaseArchives()
    {
        const string debug = "ifeq ($(config),debug64)\n  LDDEPS += ../../../../linux_clang/bin/x64/Debug/libzlib.a\nendif\n";
        string incompleteRelease = BundledInputs.Replace("../../../../linux_clang/bin/x64/Release/libzlib.a ", "", StringComparison.Ordinal);
        Assert.Null(ChdmanBuild.ValidateLinuxLinkInputs(BundledInputs + debug, LinkSource));
        Assert.Contains("bundled", ChdmanBuild.ValidateLinuxLinkInputs(incompleteRelease + debug, LinkSource)?.Message, StringComparison.Ordinal);
    }

    /// <summary>Dependencies belonging solely to inactive configurations do not alter selected release64 inputs.</summary>
    [Fact]
    public void InactiveConfigurationLibrariesDoNotChangeSelectedReleaseInputs() =>
        Assert.Null(ChdmanBuild.ValidateLinuxLinkInputs(BundledInputs
            + "ifeq ($(config),debug64)\n  LDDEPS += /usr/lib/libSDL2.a\nendif\n", LinkSource));

    /// <summary>Only the pinned generator's one complete, plain release64 archive addition is accepted.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unclosed")]
    [InlineData("unscoped")]
    [InlineData("conditional")]
    [InlineData("assignment")]
    [InlineData("definition")]
    public void UnsupportedSelectedConfigurationShapesAreRejected(string mutation)
    {
        string makefile = mutation switch
        {
            "missing" => BundledInputs.Replace("release64", "debug64", StringComparison.Ordinal),
            "duplicate" => BundledInputs + BundledInputs,
            "unclosed" => BundledInputs.Replace("\nendif\n", "\n", StringComparison.Ordinal),
            "unscoped" => BundledInputs.Replace("ifeq ($(config),release64)\n", "", StringComparison.Ordinal).Replace("endif\n", "", StringComparison.Ordinal),
            "conditional" => BundledInputs.Replace("  LDDEPS", "ifdef UNRECORDED_INPUT\n  LDDEPS", StringComparison.Ordinal)
                .Replace("endif\n", "endif\nendif\n", StringComparison.Ordinal),
            "assignment" => BundledInputs.Replace("LDDEPS +=", "LDDEPS =", StringComparison.Ordinal),
            "definition" => BundledInputs.Replace("  LDDEPS", "define ARCHIVE_TEXT\n  LDDEPS", StringComparison.Ordinal)
                .Replace("endif\n", "endef\nendif\n", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.Contains("bundled", ChdmanBuild.ValidateLinuxLinkInputs(makefile, LinkSource)?.Message, StringComparison.Ordinal);
    }

    /// <summary>A native ARM executable with an allowed dependency set and exact source banner is accepted.</summary>
    [Fact]
    public void LinuxExecutableInspectionRecordsMeasuredFacts()
    {
        JsonObject inspected = Inspect().Value;
        Assert.Equal("AArch64", inspected["architecture"]!.GetValue<string>());
        Assert.Equal("/lib/ld-linux-aarch64.so.1", inspected["interpreter"]!.GetValue<string>());
        Assert.Equal("2.28", inspected["maximumRequiredGlibc"]!.GetValue<string>());
        Assert.Equal(VersionBanner, inspected["version"]!.GetValue<string>());
        Assert.Equal(["libm.so.6", "libgcc_s.so.1", "libc.so.6"], inspected["dependencies"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    /// <summary>The native x64 route requires its own architecture and interpreter.</summary>
    [Fact]
    public void LinuxX64ExecutableHasItsOwnInterpreter()
    {
        JsonObject inspected = Inspect("linux-x64", Header.Replace("AArch64", "Advanced Micro Devices X86-64", StringComparison.Ordinal),
            Programs.Replace("/lib/ld-linux-aarch64.so.1", "/lib64/ld-linux-x86-64.so.2", StringComparison.Ordinal)).Value;
        Assert.Equal("/lib64/ld-linux-x86-64.so.2", inspected["interpreter"]!.GetValue<string>());
    }

    /// <summary>The requested RID's exact interpreter may also be a measured dynamic dependency.</summary>
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public void LinuxInterpreterDependencyIsRecorded(string rid)
    {
        string machine = rid == "linux-arm64" ? "AArch64" : "Advanced Micro Devices X86-64";
        string interpreter = rid == "linux-arm64" ? "/lib/ld-linux-aarch64.so.1" : "/lib64/ld-linux-x86-64.so.2";
        string loader = Path.GetFileName(interpreter);
        JsonObject inspected = Inspect(rid, Header.Replace("AArch64", machine, StringComparison.Ordinal),
            Programs.Replace("/lib/ld-linux-aarch64.so.1", interpreter, StringComparison.Ordinal),
            Dynamic + " 0x0000000000000001 (NEEDED)             Shared library: [" + loader + "]\n").Value;
        Assert.Equal(machine, inspected["architecture"]!.GetValue<string>());
        Assert.Equal(interpreter, inspected["interpreter"]!.GetValue<string>());
        Assert.Equal(["libm.so.6", "libgcc_s.so.1", "libc.so.6", loader],
            inspected["dependencies"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    /// <summary>A loader belonging to the other Linux RID cannot join an otherwise valid dependency set.</summary>
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public void OppositeLinuxInterpreterDependencyIsRejected(string rid)
    {
        string machine = rid == "linux-arm64" ? "AArch64" : "Advanced Micro Devices X86-64";
        string interpreter = rid == "linux-arm64" ? "/lib/ld-linux-aarch64.so.1" : "/lib64/ld-linux-x86-64.so.2";
        string oppositeLoader = rid == "linux-arm64" ? "ld-linux-x86-64.so.2" : "ld-linux-aarch64.so.1";
        Result<JsonObject> inspected = Inspect(rid, Header.Replace("AArch64", machine, StringComparison.Ordinal),
            Programs.Replace("/lib/ld-linux-aarch64.so.1", interpreter, StringComparison.Ordinal),
            Dynamic + " 0x0000000000000001 (NEEDED)             Shared library: [" + oppositeLoader + "]\n");
        Assert.Contains("dependency", inspected.Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Executable identity is distinct from a shared object and from a cross-architecture binary.</summary>
    [Theory]
    [InlineData("header", "AArch64", "Advanced Micro Devices X86-64", "architecture")]
    [InlineData("dynamic", "Flags: PIE", "Flags: NOW", "executable")]
    [InlineData("header", "ELF64", "ELF32", "ELF64")]
    [InlineData("programs", "/lib/ld-linux-aarch64.so.1", "/unreviewed/ld.so", "interpreter")]
    [InlineData("programs", "INTERP", "UNKNOWN", "interpreter")]
    [InlineData("sections", ".dynsym", ".symtab", "stripped")]
    [InlineData("sections", ".dynsym", ".debug_info", "stripped")]
    [InlineData("dynamic", "libgcc_s.so.1", "libstdc++.so.6", "dependency")]
    [InlineData("dynamic", "libm.so.6", "libz.so.1", "dependency")]
    [InlineData("dynamic", "libm.so.6", "libSDL2-2.0.so.0", "dependency")]
    [InlineData("versions", "GLIBC_2.28", "GLIBC_2.32", "GLIBC")]
    [InlineData("versions", "GLIBC_2.28", "GLIBC_PRIVATE", "GLIBC")]
    [InlineData("banner", "0.289", "0.290", "banner")]
    [InlineData("banner", Commit, "f34f025", "banner")]
    [InlineData("banner", Commit + ")", Commit + "-dirty)", "banner")]
    public void UnqualifiedLinuxFactsAreRejected(string field, string from, string to, string message)
    {
        string header = Header, programs = Programs, dynamic = Dynamic, versions = Versions, sections = Sections, banner = VersionBanner;
        switch (field)
        {
            case "header":
                header = header.Replace(from, to, StringComparison.Ordinal);
                break;
            case "programs":
                programs = programs.Replace(from, to, StringComparison.Ordinal);
                break;
            case "dynamic":
                dynamic = dynamic.Replace(from, to, StringComparison.Ordinal);
                break;
            case "versions":
                versions = versions.Replace(from, to, StringComparison.Ordinal);
                break;
            case "sections":
                sections = sections.Replace(from, to, StringComparison.Ordinal);
                break;
            case "banner":
                banner = banner.Replace(from, to, StringComparison.Ordinal);
                break;
        }
        Assert.Contains(message, Inspect("linux-arm64", header, programs, dynamic, versions, sections, banner).Failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Any run path or missing interpreter is rejected even when the remaining facts match.</summary>
    [Theory]
    [InlineData("RPATH")]
    [InlineData("RUNPATH")]
    public void LinuxRunPathsAreRejected(string tag) =>
        Assert.Contains("RPATH", Inspect(dynamic: Dynamic + " 0x000000000000001d (" + tag + ") Library runpath: [/tmp]\n").Failure.Message, StringComparison.Ordinal);

    /// <summary>A failed inspector command cannot be treated as empty successful output.</summary>
    [Fact]
    public void FailedLinuxInspectorCommandIsRetained()
    {
        Result<JsonObject> result = ChdmanBuild.InspectLinux(Tool, "linux-arm64", Pin(), command => new Failure("readelf failed with diagnostic"));
        Assert.Equal("readelf failed with diagnostic", result.Failure.Message);
    }

    /// <summary>Older readelf wording does not change a PIE's interpreter and dynamic executable flag.</summary>
    [Fact]
    public void OlderReadelfSharedObjectDescriptionStillRequiresPieFacts() =>
        Assert.True(Inspect(header: Header.Replace("Position-Independent Executable file", "Shared object file", StringComparison.Ordinal)).Succeeded);

    /// <summary>Mutated, removed and redirected recorded inputs prevent final success.</summary>
    [Fact]
    public void ChangedInputsCannotProduceSuccess()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "recipe.cs");
        File.WriteAllText(path, "original");
        JsonArray inputs = ChdmanBuild.CaptureInputs([path]).Value;
        Assert.Null(ChdmanBuild.VerifyInputs(inputs));
        File.WriteAllText(path, "tampered");
        Assert.Contains("changed", ChdmanBuild.VerifyInputs(inputs)?.Message, StringComparison.Ordinal);
        File.Delete(path);
        Assert.Contains("changed", ChdmanBuild.VerifyInputs(inputs)?.Message, StringComparison.Ordinal);
        File.WriteAllText(path + ".other", "original");
        File.CreateSymbolicLink(path, path + ".other");
        Assert.Contains("changed", ChdmanBuild.VerifyInputs(inputs)?.Message, StringComparison.Ordinal);
    }

    /// <summary>A failed fresh invocation invalidates an earlier success receipt before input validation.</summary>
    [Fact]
    public void InvalidRequestCannotLeaveAStaleSuccessReceipt()
    {
        using var directory = new TemporaryDirectory();
        string output = Path.Combine(directory.Path, "artifacts", "tools", "chdman");
        Directory.CreateDirectory(output);
        string manifest = Path.Combine(output, ChdmanBuild.ManifestName);
        File.WriteAllText(manifest, "previous success");
        var request = new ChdmanBuildRequest(Path.Combine(directory.Path, "absent.tar.gz"), output, 0, Path.Combine(directory.Path, "build.log"));
        Assert.False(ChdmanBuild.Run(directory.Path, new Dictionary<string, string>(StringComparer.Ordinal), request).Succeeded);
        Assert.False(File.Exists(manifest));
    }

    private static Result<JsonObject> Inspect(string rid = "linux-arm64", string header = Header, string programs = Programs,
        string dynamic = Dynamic, string versions = Versions, string sections = Sections, string banner = VersionBanner) =>
        ChdmanBuild.InspectLinux(Tool, rid, Pin(), command => command[0] == Tool ? banner + "\n" : command[1] switch
        {
            "-h" => header,
            "-l" => programs,
            "-d" => dynamic,
            "--version-info" => versions,
            "-S" => sections,
            _ => new Failure("Unexpected inspection command: " + ProcessRunner.Display(command)),
        });

    private static MamePin Pin() => new(new JsonObject(), "mame0289", Commit, "0.289", 1, new string('0', 64));
}
