using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

/// <summary>What platform tools report about a binary, before allowlist validation.</summary>
internal sealed record PlatformFacts(ImmutableArray<string> Symbols, ImmutableArray<string> Dependencies, JsonObject Platform);

/// <summary>Validated inspection result recorded in, and later compared with, the manifest.</summary>
internal sealed record Inspection(ImmutableArray<string> Symbols, ImmutableArray<string> Dependencies, JsonObject Platform);

/// <summary>The host RID and in-process build-info reader inspection depends on; tests substitute both to drive every RID's inspector.</summary>
internal sealed record InspectionHost(Func<Result<string>> Rid, Func<string, Result<JsonObject>> ReadBuildInfo)
{
    internal static InspectionHost Native { get; } = new(NativeRids.Host, NativeBuildInfo.ReadFromLibrary);
}

/// <summary>Inspects an actual binary on its native host: architecture, exports, dependencies, platform floor and build-info.</summary>
internal static class BinaryInspection
{
    internal static Result<Inspection> Inspect(string binary, string rid, IReadOnlyList<string> exports, JsonObject expectedInfo,
        IReadOnlyDictionary<string, string> environment, TextWriter? log) =>
        Inspect(binary, rid, exports, expectedInfo, environment, log, InspectionHost.Native);

    internal static Result<Inspection> Inspect(string binary, string rid, IReadOnlyList<string> exports, JsonObject expectedInfo,
        IReadOnlyDictionary<string, string> environment, TextWriter? log, InspectionHost inspectionHost)
    {
        Result<string> host = inspectionHost.Rid();
        if (!host.Succeeded)
            return host.Failure;
        if (Check.That(rid == host.Value, "Binary inspection requires its native host; no cross-platform qualification") is { } cross)
            return cross;
        if (Check.That(File.Exists(binary) && !ArtifactsPath.IsLink(binary), "Native binary must be a regular file") is { } regular)
            return regular;
        if (Check.That(Path.GetFileName(binary) == NativeRids.LibraryFileName(rid), "Unexpected native binary filename") is { } name)
            return name;
        Result<PlatformFacts> facts = Platform(rid, binary, command => ProcessRunner.Run(command, environment, log));
        if (!facts.Succeeded)
            return facts.Failure;
        Result<ImmutableArray<string>> symbols = NativeAllowlists.ValidateSymbols(facts.Value.Symbols, exports);
        if (!symbols.Succeeded)
            return symbols.Failure;
        Result<ImmutableArray<string>> dependencies = NativeAllowlists.ValidateDependencies(facts.Value.Dependencies, rid);
        if (!dependencies.Succeeded)
            return dependencies.Failure;
        Result<JsonObject> actual = inspectionHost.ReadBuildInfo(binary);
        if (!actual.Succeeded)
            return actual.Failure;
        if (Check.That(JsonFields.SameCanonical(actual.Value, expectedInfo), "Native build-info differs from the pinned build recipe") is { } info)
            return info;
        log?.Write("Native build-info: " + JsonFields.Compact(actual.Value) + "\n");
        log?.Flush();
        return new Inspection(symbols.Value, dependencies.Value, facts.Value.Platform);
    }

    /// <summary>Runs the RID's inspectors through <paramref name="run"/>, so canned tool output can drive non-host parsers.</summary>
    internal static Result<PlatformFacts> Platform(string rid, string binary, Func<IReadOnlyList<string>, Result<string>> run) => rid switch
    {
        "osx-arm64" => MacOs(binary, run),
        "linux-x64" or "linux-arm64" => Linux(rid, binary, run),
        _ => new Failure("Unsupported RID: " + rid),
    };

    private static Result<PlatformFacts> MacOs(string binary, Func<IReadOnlyList<string>, Result<string>> run)
    {
        Result<string> architectures = run(["lipo", "-archs", binary]);
        if (!architectures.Succeeded)
            return architectures.Failure;
        string[] found = architectures.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (Check.That(found is ["arm64"], $"Unexpected Mach-O architecture: [{string.Join(", ", found)}]") is { } architecture)
            return architecture;
        Result<ImmutableArray<string>> symbols = run(["nm", "-gU", binary]).Then(text => ToolOutput.ParseNm(text, mac: true));
        if (!symbols.Succeeded)
            return symbols.Failure;
        Result<ImmutableArray<string>> dependencies = run(["otool", "-L", binary]).Then(text => ToolOutput.ParseMachODependencies(text, Path.GetFileName(binary)));
        if (!dependencies.Succeeded)
            return dependencies.Failure;
        Result<string> minimum = run(["otool", "-l", binary]).Then(ToolOutput.MacOsMinimum);
        if (!minimum.Succeeded)
            return minimum.Failure;
        // A modified page would otherwise kill this process when the build-info export is read in-process.
        if (Check.That(run(["codesign", "--verify", "--strict", binary]).Succeeded,
            "Mach-O code signature is invalid; refusing to load the binary") is { } signature)
            return signature;
        return new PlatformFacts(symbols.Value, dependencies.Value, new JsonObject { ["architecture"] = "arm64", ["minimumOs"] = minimum.Value });
    }

    private static Result<PlatformFacts> Linux(string rid, string binary, Func<IReadOnlyList<string>, Result<string>> run)
    {
        string architecture = rid == "linux-arm64" ? "AArch64" : "Advanced Micro Devices X86-64";
        Result<string> header = run(["readelf", "-h", binary]);
        if (!header.Succeeded)
            return header.Failure;
        if (Check.That(ToolOutput.IsElfMachine(header.Value, architecture), "Unexpected ELF architecture") is { } machine)
            return machine;
        if (Check.That(ToolOutput.IsElfSharedLibrary(header.Value), "Expected an ELF shared library") is { } shared)
            return shared;
        Result<ImmutableArray<string>> symbols = run(["nm", "-D", "--defined-only", binary]).Then(text => ToolOutput.ParseNm(text, mac: false));
        if (!symbols.Succeeded)
            return symbols.Failure;
        Result<string> dynamic = run(["readelf", "-d", binary]);
        if (!dynamic.Succeeded)
            return dynamic.Failure;
        if (Check.That(!ToolOutput.HasElfRunPath(dynamic.Value), "Native binary contains an RPATH/RUNPATH") is { } rpath)
            return rpath;
        Result<string> maximum = run(["readelf", "--version-info", binary]).Then(ToolOutput.LinuxGlibcMaximum);
        if (!maximum.Succeeded)
            return maximum.Failure;
        return new PlatformFacts(symbols.Value, ToolOutput.ElfNeeded(dynamic.Value), new JsonObject
        {
            ["architecture"] = architecture,
            ["maximumRequiredGlibc"] = maximum.Value,
            ["glibcBaseline"] = NativePolicy.LinuxMaximumGlibc,
        });
    }
}
