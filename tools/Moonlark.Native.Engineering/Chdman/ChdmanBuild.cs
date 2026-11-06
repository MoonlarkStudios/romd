using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Native;

namespace Moonlark.Native.Engineering.Chdman;

internal sealed record ChdmanBuildRequest(string Archive, string Output, int Jobs, string LogPath, string? Rid = null, string? SdlIncludeRoot = null);

/// <summary>Builds only the pinned MAME chdman into ignored local artifacts on its native supported host.</summary>
internal static partial class ChdmanBuild
{
    internal const string RecipeDirectory = "tools/Moonlark.Native.Engineering/Chdman";
    internal const string ManifestName = "build-manifest.json";
    internal const int ReceiptSchemaVersion = 2;

    /// <summary>
    /// The explicit build-recipe identity recorded beside every chdman. Bump it by hand whenever the recipe changes meaning:
    /// GENie or make arguments, toolchain selection, inspection or the receipt's evidence.
    /// </summary>
    internal const int RecipeVersion = 1;
    private const string BinaryPath = "build/osx_clang/bin/x64/Release/chdman";
    private const string Banner = "chdman - MAME Compressed Hunks of Data (CHD) manager ";

    private static readonly FrozenSet<string> MakeOverrides =
        new[] { "MAKEFLAGS", "MFLAGS", "GENIE_FLAGS", "MAKEFILES", "MAKEOVERRIDES", "GNUMAKEFLAGS" }.ToFrozenSet(StringComparer.Ordinal);

    // Upstream make variables can also be inherited by name. Supply only process/runtime
    // essentials so unrelated shell variables cannot set them.
    private static readonly FrozenSet<string> Essentials =
        new[] { "PATH", "HOME", "TMPDIR", "TMP", "TEMP", "USER", "LOGNAME", "SHELL", "SOURCE_DATE_EPOCH" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AllowedDependencies =
        new[] { "/usr/lib/libSystem.B.dylib", "/usr/lib/libc++.1.dylib" }.ToFrozenSet(StringComparer.Ordinal);

    private sealed record Toolchain(string Compiler, string Sdk, string MakeVersion, string ClangVersion, JsonObject? Linux = null, JsonArray? Inputs = null);

    private sealed record Inspection(ImmutableArray<string> Dependencies, string Version);

    /// <summary>Requires both the operating system and executing process to match the requested native RID.</summary>
    internal static Result<string> SelectRid(string? requested, string os, Architecture osArchitecture, Architecture processArchitecture) =>
        (os, osArchitecture, processArchitecture) switch
        {
            ("macos", Architecture.Arm64, Architecture.Arm64) when requested is null or "osx-arm64" => "osx-arm64",
            ("linux", Architecture.X64, Architecture.X64) when requested is null or "linux-x64" => "linux-x64",
            ("linux", Architecture.Arm64, Architecture.Arm64) when requested is null or "linux-arm64" => "linux-arm64",
            _ => new Failure($"chdman requires a supported native host matching its RID: {os}/{osArchitecture}/{processArchitecture}/{requested}"),
        };

    /// <summary>This host's chdman RID, used for the default output directory.</summary>
    internal static Result<string> HostRid() => SelectRid(null, HostSystem, RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture);

    private static string HostSystem => OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "unsupported";

    /// <summary>Native Linux uses bundled GENie and bundled codecs with explicit Clang tools and mapped sources.</summary>
    internal static ImmutableArray<string> GenerateArguments(string source, string clangVersion, string rid) =>
        rid == "osx-arm64" ? GenerateArguments(source, clangVersion) :
        [
            Path.Combine(source, "3rdparty/genie/bin/linux/genie"), "--with-tools",
            "--target=mame", "--subtarget=mame", "--osd=sdl", "--targetos=linux",
            "--PLATFORM=" + LinuxPlatform(rid), "--build-dir=build", "--gcc=linux-clang",
            "--gcc_version=" + clangVersion, "--CC=clang", "--CXX=clang++", "--OPTIMIZE=3", "--NOASM=1",
            "--SYMBOLS=0", "--STRIP_SYMBOLS=1", "--SEPARATE_BIN=1",
            "--NO_USE_MIDI=1", "--NO_X11=1", "--NO_USE_XINPUT=1",
            "--ARCHOPTS=-ffile-prefix-map=" + source + "=/_/mame -fdebug-prefix-map=" + source + "=/_/mame -ffunction-sections -fdata-sections",
            "--LDOPTS=-static-libstdc++ -Wl,--as-needed,--gc-sections,--fatal-warnings", "gmake",
        ];

    /// <summary>Adds only the documented C++ include option for an explicitly captured Linux SDL header tree.</summary>
    internal static ImmutableArray<string> GenerateArguments(string source, string clangVersion, string rid, string? sdlIncludeRoot)
    {
        ImmutableArray<string> arguments = GenerateArguments(source, clangVersion, rid);
        if (sdlIncludeRoot is null)
            return arguments;
        if (rid == "osx-arm64")
            throw new ArgumentException("Explicit SDL header inputs are supported only on Linux", nameof(sdlIncludeRoot));
        return arguments.Insert(arguments.Length - 1, "--ARCHOPTS_CXX=-I" + sdlIncludeRoot);
    }

    /// <summary>SDL's emulator link defaults are overridden only for the chdman target; codec archives remain LDDEPS.</summary>
    internal static ImmutableArray<string> LinkArguments(int jobs, string rid, string source) => rid == "osx-arm64" ? LinkArguments(jobs) :
        [
            "make", "-C", LinuxProjectDirectory, "config=release64",
            "-j" + jobs.ToString(CultureInfo.InvariantCulture), "LIBS=$(LDDEPS) -lpthread -ldl -lm -lutil",
            "ALL_LDFLAGS=-static-libstdc++ -Wl,--as-needed,--gc-sections,--fatal-warnings,--build-id=sha1,-Map," + Path.Combine(source, "build/chdman.link.map"),
            "chdman",
        ];

    /// <summary>Inspects executable ELF identity, a closed dynamic dependency set, stripping and the complete source banner.</summary>
    internal static Result<JsonObject> InspectLinux(string binary, string rid, MamePin pin,
        Func<IReadOnlyList<string>, Result<string>> run)
    {
        if (rid is not ("linux-x64" or "linux-arm64"))
            return new Failure("Unsupported Linux chdman RID: " + rid);
        string machine = rid == "linux-arm64" ? "AArch64" : "Advanced Micro Devices X86-64";
        string expectedInterpreter = rid == "linux-arm64" ? "/lib/ld-linux-aarch64.so.1" : "/lib64/ld-linux-x86-64.so.2";
        Result<string> header = run(["readelf", "-h", binary]);
        if (!header.Succeeded)
            return header.Failure;
        if (Check.That(Elf64().IsMatch(header.Value) && ElfLittleEndian().IsMatch(header.Value), "Expected little-endian ELF64 chdman") is { } elf64)
            return elf64;
        if (Check.That(ToolOutput.IsElfMachine(header.Value, machine), "Unexpected chdman ELF architecture") is { } architecture)
            return architecture;
        bool executable = ElfExecutable().IsMatch(header.Value);
        bool dynamicExecutable = ToolOutput.IsElfSharedLibrary(header.Value);
        if (Check.That(executable || dynamicExecutable, "Expected an ELF executable") is { } type)
            return type;
        Result<string> programs = run(["readelf", "-l", binary]);
        if (!programs.Succeeded)
            return programs.Failure;
        string[] interpreters = [.. ElfInterpreter().Matches(programs.Value).Select(match => match.Groups[1].Value)];
        if (Check.That(ElfInterpSegment().Count(programs.Value) == 1 && interpreters is [var interpreter]
            && interpreter == expectedInterpreter, "Unexpected or missing chdman ELF interpreter") is { } loader)
            return loader;
        Result<string> dynamic = run(["readelf", "-d", binary]);
        if (!dynamic.Succeeded)
            return dynamic.Failure;
        if (Check.That(!dynamicExecutable || ElfPie().IsMatch(dynamic.Value), "Expected an ELF PIE executable, not a shared library") is { } pie)
            return pie;
        if (Check.That(!ToolOutput.HasElfRunPath(dynamic.Value), "chdman contains an RPATH/RUNPATH") is { } rpath)
            return rpath;
        ImmutableArray<string> dependencies = ToolOutput.ElfNeeded(dynamic.Value);
        if (Check.That(dependencies.Length > 0 && dependencies.Contains("libc.so.6", StringComparer.Ordinal)
            && dependencies.Distinct(StringComparer.Ordinal).Count() == dependencies.Length
            && dependencies.All(name => LinuxDependencies.Contains(name) || name == Path.GetFileName(expectedInterpreter)), "Unexpected chdman dynamic dependency: " + dynamic.Value) is { } dependency)
            return dependency;
        Result<string> versions = run(["readelf", "--version-info", binary]);
        if (!versions.Succeeded)
            return versions.Failure;
        Result<string> maximum = ToolOutput.LinuxGlibcMaximum(versions.Value);
        if (!maximum.Succeeded)
            return maximum.Failure;
        Result<string> sections = run(["readelf", "-S", binary]);
        if (!sections.Succeeded)
            return sections.Failure;
        if (Check.That(!ElfUnstrippedSection().IsMatch(sections.Value), "chdman must be stripped") is { } stripped)
            return stripped;
        Result<string> banner = run([binary, "listtemplates"]);
        if (!banner.Succeeded)
            return banner.Failure;
        string version = Lines(banner.Value)[0];
        if (Check.That(version == Banner + pin.Version + " (" + pin.Commit + ")", "chdman exact source banner mismatch") is { } source)
            return source;
        return new JsonObject
        {
            ["architecture"] = machine,
            ["interpreter"] = interpreters[0],
            ["elfType"] = executable ? "EXEC" : "DYN/PIE",
            ["maximumRequiredGlibc"] = maximum.Value,
            ["glibcBaseline"] = NativePolicy.LinuxMaximumGlibc,
            ["stripped"] = true,
            ["dependencies"] = Strings(dependencies),
            ["version"] = version,
        };
    }

    /// <summary>Every generated archive input stays inside the extracted source and belongs to chdman's bundled set.</summary>
    internal static Failure? ValidateLinuxLinkInputs(string makefile, string source)
    {
        string project = Path.Combine(source, LinuxProjectDirectory);
        Result<ImmutableArray<string>> selected = SelectedLinuxLinkInputs(makefile);
        if (!selected.Succeeded)
            return selected.Failure;
        if (selected.Value.Length == 0)
            return new Failure("Missing selected bundled chdman archive inputs");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string input in selected.Value)
        {
            string name = Path.GetFileName(input);
            if (!BundledArchives.Contains(name) || !ArtifactsPath.IsWithin(Path.GetFullPath(input, project), source))
                return new Failure("Unexpected non-bundled chdman archive input: " + input);
            names.Add(name);
        }
        return Check.That(names.SetEquals(BundledArchives), "Missing bundled chdman archive inputs");
    }

    private sealed record MakeConditional(string? Configuration, bool Alternate);

    /// <summary>Selects plain LDDEPS additions from GENie's one native release64 block, never an inactive configuration.</summary>
    private static Result<ImmutableArray<string>> SelectedLinuxLinkInputs(string makefile)
    {
        var scopes = new Stack<MakeConditional>();
        ImmutableArray<string>.Builder inputs = ImmutableArray.CreateBuilder<string>();
        int selectedBlocks = 0;
        bool definition = false;
        foreach (string raw in Lines(makefile))
        {
            string line = raw.Trim();
            if (line.StartsWith("define ", StringComparison.Ordinal))
            {
                if (definition)
                    return new Failure("Unsupported nested make definition in bundled chdman recipe");
                definition = true;
                continue;
            }
            if (line == "endef")
            {
                if (!definition)
                    return new Failure("Unmatched make definition in bundled chdman recipe");
                definition = false;
                continue;
            }
            if (definition)
                continue;
            Match configuration = LinuxConfiguration().Match(line);
            if (configuration.Success)
            {
                string name = configuration.Groups[1].Value;
                if (name == "release64" && ++selectedBlocks != 1)
                    return new Failure("Duplicate selected bundled chdman configuration");
                scopes.Push(new MakeConditional(name, false));
                continue;
            }
            if (MakeCondition().IsMatch(line))
            {
                scopes.Push(new MakeConditional(null, false));
                continue;
            }
            if (line == "else" || line.StartsWith("else ", StringComparison.Ordinal))
            {
                if (!scopes.TryPop(out MakeConditional? current) || current.Alternate)
                    return new Failure("Unmatched make conditional in bundled chdman recipe");
                scopes.Push(current with { Alternate = true });
                continue;
            }
            if (line == "endif")
            {
                if (!scopes.TryPop(out _))
                    return new Failure("Unmatched make conditional in bundled chdman recipe");
                continue;
            }
            Match dependencies = LinuxLdDeps().Match(line);
            if (!dependencies.Success)
                continue;
            // A known false config guard makes the complete nested block inactive.
            if (scopes.Any(scope => scope.Configuration is { } name && (name == "release64") == scope.Alternate))
                continue;
            if (scopes.Count != 1 || scopes.Peek().Configuration != "release64" || scopes.Peek().Alternate
                || dependencies.Groups[1].Value != "+=")
                return new Failure("Unsupported conditional or assignment for selected bundled chdman archives");
            inputs.AddRange(MakeToken().Matches(dependencies.Groups[2].Value).Select(token => token.Value.Trim('"')));
        }
        if (selectedBlocks != 1 || scopes.Count != 0 || definition)
            return new Failure("Missing or unclosed selected bundled chdman configuration");
        return inputs.ToImmutable();
    }

    /// <summary>Records exact regular input bytes from one handle; callers pass resolved tools rather than aliases.</summary>
    internal static Result<JsonArray> CaptureInputs(IEnumerable<string> paths)
    {
        var inputs = new JsonArray();
        foreach (string path in paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || ArtifactsPath.IsLink(path))
                return new Failure("chdman input must be a regular absolute file: " + path);
            using FileStream stream = File.OpenRead(path);
            long bytes = stream.Length;
            string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
            if (stream.Length != bytes)
                return new Failure("chdman input changed during capture: " + path);
            inputs.Add(new JsonObject { ["path"] = path, ["bytes"] = bytes, ["sha256"] = digest });
        }
        return inputs;
    }

    /// <summary>Changed bytes, deletion or path substitution prevent writing any success receipt.</summary>
    internal static Failure? VerifyInputs(JsonArray inputs)
    {
        foreach (JsonNode? node in inputs)
        {
            if (node is not JsonObject input || input["path"] is not JsonValue pathNode || !pathNode.TryGetValue(out string? path)
                || input["bytes"] is not JsonValue bytesNode || !bytesNode.TryGetValue(out long bytes)
                || input["sha256"] is not JsonValue digestNode || !digestNode.TryGetValue(out string? digest)
                || path is null || digest is null)
                return new Failure("Malformed chdman input identity");
            if (!File.Exists(path) || ArtifactsPath.IsLink(path) || new FileInfo(path).Length != bytes || Digest.Sha256File(path) != digest)
                return new Failure("chdman input changed before receipt: " + path);
        }
        return null;
    }

    private const string LinuxProjectDirectory = "build/projects/sdl/mame/gmake-linux-clang";

    private static readonly FrozenSet<string> LinuxDependencies =
        new[] { "libc.so.6", "libm.so.6", "libpthread.so.0", "libdl.so.2", "libutil.so.1", "libgcc_s.so.1" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> BundledArchives =
        new[] { "libutils.a", "libexpat.a", "lib7z.a", "libocore_sdl.a", "libzlib.a", "libzstd.a", "libflac.a", "libutf8proc.a" }.ToFrozenSet(StringComparer.Ordinal);

    private static string LinuxPlatform(string rid) => rid switch
    {
        "linux-x64" => "x64",
        "linux-arm64" => "arm64",
        _ => throw new ArgumentOutOfRangeException(nameof(rid), rid, "Unsupported Linux chdman RID"),
    };

    internal static Result<JsonObject> Run(string root, IReadOnlyDictionary<string, string> environment, ChdmanBuildRequest request)
    {
        Result<string> output = ArtifactsPath.ValidateDirectory(request.Output, root);
        if (!output.Succeeded)
            return output.Failure;
        // A safe output path identifies this attempt; failed host/jobs/pin/archive checks must not retain old success.
        Directory.CreateDirectory(output.Value);
        File.Delete(Path.Combine(output.Value, ManifestName));
        Result<string> rid = SelectRid(request.Rid, HostSystem, RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture);
        if (!rid.Succeeded)
            return rid.Failure;
        if (Check.That(request.Jobs is >= 1 and <= 32, "Jobs must be between 1 and 32") is { } jobs)
            return jobs;
        Result<JsonObject?> sdlHeaders = ChdmanSdlHeaders.Capture(request.SdlIncludeRoot, rid.Value);
        if (!sdlHeaders.Succeeded)
            return sdlHeaders.Failure;
        Result<MamePin> pin = MamePin.Read(root);
        if (!pin.Succeeded)
            return pin.Failure;
        if (SourceArchive.Validate(request.Archive, pin.Value) is { } archive)
            return archive;
        string recipe = RecipeSha256(root);
        Result<JsonArray> recipeInputs = CaptureInputs(RecipeSources(root).Select(path => Path.Combine(root, path))
            .Append(Path.Combine(root, MamePin.PinPath)).Append(Path.GetFullPath(request.Archive)));
        if (!recipeInputs.Succeeded)
            return recipeInputs.Failure;
        if (PrepareOutput(output.Value) is { } prepared)
            return prepared;
        Result<ExtractedSource> source = SourceArchive.Extract(request.Archive, Path.Combine(output.Value, "source"), pin.Value);
        if (!source.Succeeded)
            return source.Failure;
        if (rid.Value != "osx-arm64" && Check.That(!source.Value.Source.Any(char.IsWhiteSpace) && !source.Value.Source.Contains(',', StringComparison.Ordinal),
            "Linux chdman source path must not contain whitespace or commas") is { } path)
            return path;
        Result<JsonArray> sourceInputs = CaptureSourceInputs(source.Value.Source);
        if (!sourceInputs.Succeeded)
            return sourceInputs.Failure;
        string sourceInputsPath = Path.Combine(output.Value, "source-inputs.json");
        File.WriteAllText(sourceInputsPath, ReceiptJson.Serialize(new JsonObject { ["inputs"] = sourceInputs.Value.DeepClone() }));
        Result<JsonArray> sourceEvidence = CaptureInputs([sourceInputsPath]);
        if (!sourceEvidence.Succeeded)
            return sourceEvidence.Failure;
        Result<IReadOnlyDictionary<string, string>> env = MakeEnvironment(environment, source.Value.Epoch);
        if (!env.Succeeded)
            return env.Failure;
        Result<JsonObject> receipt;
        using (var log = new StreamWriter(request.LogPath, append: false))
            receipt = Build(pin.Value, source.Value, output.Value, request.Jobs, recipe, env.Value, log, rid.Value, request.SdlIncludeRoot);
        if (!receipt.Succeeded)
            return receipt.Failure;
        if (VerifyInputs(recipeInputs.Value) is { } recipeChanged)
            return recipeChanged;
        if (Check.That(RecipeSha256(root) == recipe, "chdman recipe source set changed before receipt") is { } sourceSet)
            return sourceSet;
        if (VerifySourceInputs(sourceInputs.Value) is { } sourceChanged)
            return sourceChanged;
        if (VerifyInputs(sourceEvidence.Value) is { } sourceEvidenceChanged)
            return sourceEvidenceChanged;
        receipt.Value["recipeInputs"] = recipeInputs.Value.DeepClone();
        receipt.Value["sourceInputs"] = new JsonObject
        {
            ["path"] = "source-inputs.json",
            ["sha256"] = Digest.Sha256File(sourceInputsPath),
            ["members"] = sourceInputs.Value.Count,
        };
        if (sdlHeaders.Value is { } capturedHeaders)
        {
            if (ChdmanSdlHeaders.Verify(capturedHeaders) is { } headersChanged)
                return headersChanged;
            receipt.Value["sdlHeaders"] = capturedHeaders.DeepClone();
        }
        File.WriteAllText(Path.Combine(output.Value, ManifestName), ReceiptJson.Serialize(receipt.Value));
        return receipt.Value;
    }

    /// <summary>Allowlist-only make environment: implicit tool inputs and make/GENie overrides fail, everything else is dropped.</summary>
    internal static Result<IReadOnlyDictionary<string, string>> MakeEnvironment(IReadOnlyDictionary<string, string> environment, long epoch)
    {
        Result<IReadOnlyDictionary<string, string>> created = BuildEnvironment.Create(environment, epoch);
        if (!created.Succeeded)
            return created.Failure;
        if (Check.That(!created.Value.Keys.Any(MakeOverrides.Contains), "Unrecorded make/GENie environment override") is { } makeOverride)
            return makeOverride;
        Dictionary<string, string> result = created.Value.Where(pair => Essentials.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        result["LC_ALL"] = "C";
        result["TZ"] = "UTC";
        return result;
    }

    /// <summary>The bundled GENie invocation: tools only, fatal warnings and bundled codecs kept, prefix-mapped sources.</summary>
    internal static ImmutableArray<string> GenerateArguments(string source, string clangVersion) =>
    [
        Path.Combine(source, "3rdparty/genie/bin/darwin/genie"), "--with-tools",
        "--target=mame", "--subtarget=mame", "--osd=mac", "--targetos=macosx",
        "--PLATFORM=arm64", "--build-dir=build", "--gcc=osx-clang",
        "--gcc_version=" + clangVersion, "--OPTIMIZE=3", "--NOASM=1",
        "--SYMBOLS=0", "--STRIP_SYMBOLS=1", "--SEPARATE_BIN=1",
        "--ARCHOPTS=-ffile-prefix-map=" + source + "=/_/mame -mmacosx-version-min=14.0",
        "--LDOPTS=-mmacosx-version-min=14.0 -Wl,-dead_strip,-dead_strip_dylibs", "gmake",
    ];

    /// <summary>mac.lua adds emulator frameworks globally. A recorded, explicit tool link uses the same bundled archives, retaining only chdman's imports.</summary>
    internal static ImmutableArray<string> LinkArguments(int jobs) =>
    [
        "make", "-C", "build/projects/mac/mame/gmake-osx-clang", "config=release64",
        "-j" + jobs.ToString(CultureInfo.InvariantCulture), "LIBS=$(LDDEPS) -lpthread",
        "ALL_LDFLAGS=-m64 -arch arm64 -mmacosx-version-min=14.0 -Wl,-dead_strip,-dead_strip_dylibs,-fatal_warnings,-reproducible", "chdman",
    ];

    /// <summary>
    /// SHA-256 of the <c>shasum -a 256</c> listing (lowercase digest, two spaces, path, LF) of <see cref="RecipeSources"/>:
    /// <c>LC_ALL=C shasum -a 256 tools/Moonlark.Native.Engineering/Chdman/*.cs | shasum -a 256</c> from the repository root.
    /// </summary>
    internal static string RecipeSha256(string root) =>
        Digest.Sha256(Encoding.UTF8.GetBytes(string.Concat(RecipeSources(root)
            .Select(path => Digest.Sha256File(Path.Combine(root, path)) + "  " + path + "\n"))));

    /// <summary>Every C# source under the recipe directory, as repository-relative '/' paths in ordinal order.</summary>
    internal static ImmutableArray<string> RecipeSources(string root) =>
    [
        .. Directory.EnumerateFiles(Path.Combine(root, RecipeDirectory), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal),
    ];

    private static Failure? PrepareOutput(string output)
    {
        Directory.CreateDirectory(output);
        File.Delete(Path.Combine(output, ManifestName));
        foreach (string name in (string[])["source", "native"])
        {
            string child = Path.Combine(output, name);
            if (Check.That(!ArtifactsPath.IsLink(child), "Output child cannot be a symlink: " + name) is { } link)
                return link;
            if (Directory.Exists(child))
                Directory.Delete(child, recursive: true);
        }
        return null;
    }

    private static Result<JsonObject> Build(MamePin pin, ExtractedSource source, string output, int jobs, string recipe,
        IReadOnlyDictionary<string, string> environment, TextWriter log, string rid, string? sdlIncludeRoot)
    {
        Result<Toolchain> toolchain = rid == "osx-arm64" ? IdentifyToolchain(source.Source, environment, log) : IdentifyLinuxToolchain(source.Source, environment, log, rid);
        if (!toolchain.Succeeded)
            return toolchain.Failure;
        bool linux = rid != "osx-arm64";
        ImmutableArray<string> generate = GenerateArguments(source.Source, toolchain.Value.ClangVersion, rid, sdlIncludeRoot);
        ImmutableArray<string> link = LinkArguments(jobs, rid, source.Source);
        string platform = linux ? LinuxPlatform(rid) : "arm64";
        ImmutableArray<string>[] steps =
        [
            linux
                ? ["make", "-C", "3rdparty/genie/build/gmake.linux", "-f", "genie.make", "-j" + jobs.ToString(CultureInfo.InvariantCulture),
                    "CC=clang", "CXX=clang++", "CFLAGS=-ffile-prefix-map=" + source.Source + "=/_/mame", "CXXFLAGS=-ffile-prefix-map=" + source.Source + "=/_/mame"]
                : ["make", "-C", "3rdparty/genie/build/gmake.darwin", "-f", "genie.make", "-j" + jobs.ToString(CultureInfo.InvariantCulture)],
            ["make", "build/generated/version.cpp", linux ? "OSD=sdl" : "OSD=mac", "EMULATOR=0", "TOOLS=1", "PLATFORM=" + platform,
                "OVERRIDE_CC=clang", "OVERRIDE_CXX=clang++", "NEW_GIT_VERSION=" + pin.Commit],
            generate,
        ];
        foreach (ImmutableArray<string> step in steps)
        {
            Result<string> result = ProcessRunner.Run(step, environment, log, source.Source);
            if (!result.Succeeded)
                return result.Failure;
        }
        JsonArray? generated = null;
        if (linux)
        {
            string project = Path.Combine(source.Source, LinuxProjectDirectory);
            if (ValidateLinuxLinkInputs(File.ReadAllText(Path.Combine(project, "chdman.make")), source.Source) is { } inputs)
                return inputs;
            Result<JsonArray> captured = CaptureInputs(Directory.EnumerateFiles(project, "*.make").Append(Path.Combine(project, "Makefile"))
                .Append(Path.Combine(source.Source, "3rdparty/genie/bin/linux/genie")));
            if (!captured.Succeeded)
                return captured.Failure;
            generated = captured.Value;
        }
        Result<string> linked = ProcessRunner.Run(link, environment, log, source.Source);
        if (!linked.Succeeded)
            return linked.Failure;
        Result<string> tool = Install(source.Source, output, rid);
        if (!tool.Succeeded)
            return tool.Failure;
        if (!linux)
        {
            Result<Inspection> inspection = Inspect(tool.Value, pin, source.Source, environment, log);
            if (!inspection.Succeeded)
                return inspection.Failure;
            return Receipt(pin, toolchain.Value, recipe, source.Epoch, generate, link, tool.Value, inspection.Value, rid);
        }
        Result<string> stripped = ProcessRunner.Run(["strip", "--strip-all", tool.Value], environment, log, source.Source);
        if (!stripped.Succeeded)
            return stripped.Failure;
        Result<JsonArray> binary = CaptureInputs([tool.Value]);
        if (!binary.Succeeded)
            return binary.Failure;
        Result<JsonObject> platformFacts = InspectLinux(tool.Value, rid, pin, command => ProcessRunner.Run(command, environment, log, source.Source));
        if (!platformFacts.Succeeded)
            return platformFacts.Failure;
        if (VerifyInputs(binary.Value) is { } binaryChanged)
            return binaryChanged;
        if (VerifyInputs(generated!) is { } generatedChanged)
            return generatedChanged;
        if (VerifyInputs(toolchain.Value.Inputs!) is { } toolsChanged)
            return toolsChanged;
        if (VerifyToolPaths(toolchain.Value.Linux!["toolPaths"]!.AsObject(), environment) is { } toolPathChanged)
            return toolPathChanged;
        Result<string> selectedRuntime = ProcessRunner.Run(["clang++", "-print-file-name=libstdc++.a"], environment, log, source.Source);
        if (!selectedRuntime.Succeeded)
            return selectedRuntime.Failure;
        if (Check.That(Path.GetFullPath(selectedRuntime.Value) == toolchain.Value.Linux!["staticCppRuntimePath"]!.GetValue<string>(),
            "Static C++ runtime selection changed before receipt") is { } runtimeChanged)
            return runtimeChanged;
        ImmutableArray<string> dependencies = [.. platformFacts.Value["dependencies"]!.AsArray().Select(node => node!.GetValue<string>())];
        var inspected = new Inspection(dependencies, platformFacts.Value["version"]!.GetValue<string>());
        JsonObject receipt = Receipt(pin, toolchain.Value, recipe, source.Epoch, generate, link, tool.Value, inspected, rid);
        receipt.Remove("macosSdk");
        receipt["toolchain"] = toolchain.Value.Linux!.DeepClone();
        receipt["toolInputs"] = toolchain.Value.Inputs!.DeepClone();
        receipt["generatedInputs"] = generated!.DeepClone();
        receipt["platform"] = platformFacts.Value;
        string map = Path.Combine(source.Source, "build/chdman.link.map");
        receipt["linkMap"] = new JsonObject { ["path"] = "source/" + pin.ArchivePrefix + "/build/chdman.link.map", ["sha256"] = Digest.Sha256File(map), ["bytes"] = new FileInfo(map).Length };
        return receipt;
    }

    private static Result<Toolchain> IdentifyToolchain(string source, IReadOnlyDictionary<string, string> environment, TextWriter log)
    {
        Result<string> compiler = ProcessRunner.Run(["clang", "--version"], environment, log, source);
        if (!compiler.Succeeded)
            return compiler.Failure;
        Result<string> sdk = ProcessRunner.Run(["xcrun", "--show-sdk-version"], environment, log, source);
        if (!sdk.Succeeded)
            return sdk.Failure;
        Result<string> make = ProcessRunner.Run(["make", "--version"], environment, log, source);
        if (!make.Succeeded)
            return make.Failure;
        Match match = ClangVersion().Match(compiler.Value);
        if (Check.That(match.Success, "Cannot identify clang version") is { } unknown)
            return unknown;
        return new Toolchain(compiler.Value, sdk.Value, Lines(make.Value)[0], match.Groups[1].Value);
    }

    private static Result<Toolchain> IdentifyLinuxToolchain(string source, IReadOnlyDictionary<string, string> environment, TextWriter log, string rid)
    {
        var recorded = new JsonObject();
        (string Name, string[] Arguments)[] probes =
        [
            ("clang", ["--version"]), ("clang++", ["--version"]), ("make", ["--version"]), ("ar", ["--version"]),
            ("ld", ["--version"]), ("strip", ["--version"]), ("readelf", ["--version"]), ("pkg-config", ["--version"]), ("python3", ["--version"]),
        ];
        var toolPaths = new JsonObject();
        var inputPaths = new List<string>();
        foreach ((string name, string[] arguments) in probes)
        {
            string? executable = ProcessRunner.ResolveExecutable(name, environment);
            if (executable is null)
                return new Failure("Missing Linux chdman tool: " + name);
            string resolved = ArtifactsPath.Resolve(executable);
            toolPaths[name] = resolved;
            inputPaths.Add(resolved);
            Result<string> probe = ProcessRunner.Run([name, .. arguments], environment, log, source);
            if (!probe.Succeeded)
                return probe.Failure;
            recorded[name] = probe.Value;
            inputPaths.AddRange(ClangConfiguration().Matches(probe.Value).Select(match => match.Groups[1].Value));
        }
        string compiler = recorded["clang"]!.GetValue<string>();
        Match version = ClangVersion().Match(compiler);
        if (!version.Success || !ClangVersion().IsMatch(recorded["clang++"]!.GetValue<string>()))
            return new Failure("Cannot identify Linux Clang compiler version");
        Result<string> machine = ProcessRunner.Run(["clang++", "-dumpmachine"], environment, log, source);
        if (!machine.Succeeded)
            return machine.Failure;
        if (Check.That(machine.Value.StartsWith(rid == "linux-arm64" ? "aarch64-" : "x86_64-", StringComparison.Ordinal),
            "Linux Clang target does not match its native host") is { } target)
            return target;
        Result<string> staticRuntime = ProcessRunner.Run(["clang++", "-print-file-name=libstdc++.a"], environment, log, source);
        if (!staticRuntime.Succeeded)
            return staticRuntime.Failure;
        if (Check.That(Path.IsPathFullyQualified(staticRuntime.Value) && File.Exists(staticRuntime.Value) && !ArtifactsPath.IsLink(staticRuntime.Value),
            "Linux chdman requires the selected static libstdc++.a") is { } runtime)
            return runtime;
        using (FileStream stream = File.OpenRead(staticRuntime.Value))
        {
            Span<byte> magic = stackalloc byte[8];
            if (Check.That(stream.Read(magic) == 8 && magic.SequenceEqual("!<arch>\n"u8), "Static C++ runtime must be a complete regular archive") is { } archive)
                return archive;
        }
        string runtimePath = Path.GetFullPath(staticRuntime.Value);
        inputPaths.Add(runtimePath);
        Result<string> selectedLinker = ProcessRunner.Run(["clang++", "-print-prog-name=ld"], environment, log, source);
        if (!selectedLinker.Succeeded)
            return selectedLinker.Failure;
        string? linker = ProcessRunner.ResolveExecutable(selectedLinker.Value, environment);
        if (linker is null || !File.Exists(linker))
            return new Failure("Cannot identify Clang's selected linker");
        inputPaths.Add(ArtifactsPath.Resolve(linker));
        Result<JsonArray> inputs = CaptureInputs(inputPaths);
        if (!inputs.Succeeded)
            return inputs.Failure;
        recorded["target"] = machine.Value;
        recorded["toolPaths"] = toolPaths;
        recorded["selectedLinkerPath"] = ArtifactsPath.Resolve(linker);
        recorded["staticCppRuntimePath"] = runtimePath;
        recorded["staticCppRuntimeSha256"] = Digest.Sha256File(runtimePath);
        return new Toolchain(compiler, "", Lines(recorded["make"]!.GetValue<string>())[0], version.Groups[1].Value, recorded, inputs.Value);
    }

    private static Failure? VerifyToolPaths(JsonObject paths, IReadOnlyDictionary<string, string> environment)
    {
        foreach ((string name, JsonNode? path) in paths)
        {
            string? actual = ProcessRunner.ResolveExecutable(name, environment);
            if (actual is null || ArtifactsPath.Resolve(actual) != path!.GetValue<string>())
                return new Failure("Linux chdman tool path changed before receipt: " + name);
        }
        return null;
    }

    private static Result<JsonArray> CaptureSourceInputs(string source)
    {
        var files = new List<string>();
        var links = new JsonArray();
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                if (new FileInfo(path).LinkTarget is { } target)
                    links.Add(new JsonObject { ["path"] = path, ["linkTarget"] = target });
                else if (Directory.Exists(path))
                    pending.Push(path);
                else
                    files.Add(path);
            }
        }
        Result<JsonArray> captured = CaptureInputs(files);
        if (!captured.Succeeded)
            return captured.Failure;
        foreach (JsonNode? link in links)
            captured.Value.Add(link!.DeepClone());
        return captured;
    }

    private static Failure? VerifySourceInputs(JsonArray inputs)
    {
        var regular = new JsonArray();
        foreach (JsonNode? node in inputs)
        {
            if (node?["linkTarget"] is JsonValue target)
            {
                string path = node["path"]!.GetValue<string>();
                if (new FileInfo(path).LinkTarget != target.GetValue<string>())
                    return new Failure("chdman source link changed before receipt: " + path);
            }
            else
                regular.Add(node!.DeepClone());
        }
        return VerifyInputs(regular);
    }

    private static Result<string> Install(string source, string output, string rid)
    {
        string binary = Path.Combine(source, rid == "osx-arm64" ? BinaryPath : "build/linux_clang/bin/x64/Release/chdman");
        if (Check.That(File.Exists(binary) && !ArtifactsPath.IsLink(binary), "chdman regular output was not produced") is { } missing)
            return missing;
        string native = Path.Combine(output, "native");
        Directory.CreateDirectory(native);
        string tool = Path.Combine(native, "chdman");
        File.Copy(binary, tool);
        // Python's copy2 keeps the access and modification times.
        File.SetLastAccessTimeUtc(tool, File.GetLastAccessTimeUtc(binary));
        File.SetLastWriteTimeUtc(tool, File.GetLastWriteTimeUtc(binary));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return tool;
    }

    private static Result<Inspection> Inspect(string tool, MamePin pin, string source, IReadOnlyDictionary<string, string> environment, TextWriter log)
    {
        Result<string> dependencies = ProcessRunner.Run(["otool", "-L", tool], environment, log, source);
        if (!dependencies.Succeeded)
            return dependencies.Failure;
        ImmutableArray<string> paths = [.. Lines(dependencies.Value).Skip(1).Select(line => line.Trim().Split(' ')[0])];
        if (Check.That(paths.All(AllowedDependencies.Contains), "Unexpected dynamic dependency: " + dependencies.Value) is { } dependency)
            return dependency;
        Result<string> architecture = ProcessRunner.Run(["lipo", "-archs", tool], environment, log, source);
        if (!architecture.Succeeded)
            return architecture.Failure;
        if (Check.That(architecture.Value == "arm64", "Unexpected binary architecture") is { } arm64)
            return arm64;
        Result<string> banner = ProcessRunner.Run([tool, "listtemplates"], environment, log, source);
        if (!banner.Succeeded)
            return banner.Failure;
        string version = Lines(banner.Value)[0];
        if (Check.That(version == Banner + pin.Version + " (" + pin.Commit + ")", "chdman exact source banner mismatch") is { } mismatch)
            return mismatch;
        return new Inspection(paths, version);
    }

    /// <summary>The location-independent identity is the binary SHA-256, the source pin, recipeVersion and rid; other fields are evidence.</summary>
    private static JsonObject Receipt(MamePin pin, Toolchain toolchain, string recipe, long epoch, ImmutableArray<string> generate,
        ImmutableArray<string> link, string tool, Inspection inspection, string rid) => new()
        {
            ["schemaVersion"] = ReceiptSchemaVersion,
            ["recipeVersion"] = RecipeVersion,
            ["qualification"] = "local-unqualified",
            ["attestation"] = null,
            ["rid"] = rid,
            ["pin"] = pin.Document.DeepClone(),
            ["compiler"] = toolchain.Compiler,
            ["macosSdk"] = toolchain.Sdk,
            ["makeVersion"] = toolchain.MakeVersion,
            ["recipeSha256"] = recipe,
            ["sourceDateEpoch"] = epoch,
            ["arguments"] = Strings(generate.Skip(1)),
            ["linkArguments"] = Strings(link),
            ["binary"] = new JsonObject
            {
                ["path"] = "native/chdman",
                ["sha256"] = Digest.Sha256File(tool),
                ["bytes"] = new FileInfo(tool).Length,
                ["dependencies"] = Strings(inspection.Dependencies),
            },
            ["version"] = inspection.Version,
        };

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    /// <summary>Python <c>str.splitlines()</c> for the line endings tools emit.</summary>
    private static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    [GeneratedRegex(@"clang version ([0-9]+\.[0-9]+\.[0-9]+)")]
    private static partial Regex ClangVersion();

    [GeneratedRegex(@"^\s*Class:\s+ELF64\s*$", RegexOptions.Multiline)]
    private static partial Regex Elf64();

    [GeneratedRegex(@"^\s*Data:\s+2's complement, little endian\s*$", RegexOptions.Multiline)]
    private static partial Regex ElfLittleEndian();

    [GeneratedRegex(@"Type:\s+EXEC\b")]
    private static partial Regex ElfExecutable();

    [GeneratedRegex(@"^\s+INTERP\s", RegexOptions.Multiline)]
    private static partial Regex ElfInterpSegment();

    [GeneratedRegex(@"\[Requesting program interpreter: ([^\]]+)\]")]
    private static partial Regex ElfInterpreter();

    [GeneratedRegex(@"\(FLAGS_1\).*\bPIE\b")]
    private static partial Regex ElfPie();

    [GeneratedRegex(@"\s(?:\.symtab|\.debug_[A-Za-z0-9_.]+|\.zdebug_[A-Za-z0-9_.]+)\s")]
    private static partial Regex ElfUnstrippedSection();

    [GeneratedRegex(@"^\s*LDDEPS\s*(\+=|:=|=)\s*(.*)$", RegexOptions.Multiline)]
    private static partial Regex LinuxLdDeps();

    [GeneratedRegex(@"\Aifeq \(\$\(config\),([A-Za-z0-9_.-]+)\)\z")]
    private static partial Regex LinuxConfiguration();

    [GeneratedRegex(@"\A(?:ifeq|ifneq|ifdef|ifndef)\s")]
    private static partial Regex MakeCondition();

    [GeneratedRegex(@"""[^""\r\n]+""|\S+")]
    private static partial Regex MakeToken();

    [GeneratedRegex(@"^Configuration file: (.+)$", RegexOptions.Multiline)]
    private static partial Regex ClangConfiguration();
}
