using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

internal sealed record NativeBuildRequest(string Root, string Rid, string Source, string Output, string Compiler,
    IReadOnlyDictionary<string, string> Environment);

internal sealed record NativeBuildResult(string Manifest, string ProbeReceipt);

/// <summary>
/// Builds the pinned libchdr for this host. Order matters: old receipts are invalidated first; the recipe is computed
/// from CMake's effective configuration before compilation, because its buildId is compiled into the binary.
/// </summary>
internal static class NativeBuild
{
    private static readonly string[] HomeVariables = ["HOME", "USERPROFILE"];

    private sealed record Verified(LibchdrAuthority Authority, long Epoch, ImmutableArray<string> Exports,
        IReadOnlyDictionary<string, string> Tools, IReadOnlyDictionary<string, string> Build);

    private sealed record Configured(JsonObject Tools, CMakeReply Reply, JsonObject Recipe);

    internal static Result<NativeBuildResult> Run(NativeBuildRequest request, TextWriter? log)
    {
        Result<string> output = ArtifactsPath.ValidateDirectory(request.Output, request.Root);
        if (!output.Succeeded)
            return output.Failure;
        var layout = new OutputLayout(output.Value, request.Rid);
        // Invalidate the old receipts even when source/tool validation fails early.
        if (NativeOutput.Invalidate(layout) is { } stale)
            return stale;
        NativeBuildRequest resolved = request with { Root = ArtifactsPath.Resolve(request.Root), Source = ArtifactsPath.Resolve(request.Source) };
        Result<Verified> verified = VerifyInputs(resolved, log);
        if (!verified.Succeeded)
            return verified.Failure;
        if (NativeOutput.Prepare(layout.Output) is { } prepare)
            return prepare;
        Result<Configured> configured = Configure(resolved, layout, verified.Value, log);
        if (!configured.Succeeded)
            return configured.Failure;
        string program = Path.Combine([resolved.Root, .. LayoutProbe.ProgramPath.Split('/')]);
        string programSha256 = Digest.Sha256File(program);
        if (Compile(layout, verified.Value, configured.Value, log) is { } compile)
            return compile;
        return Finish(resolved, layout, verified.Value, configured.Value, programSha256, log);
    }

    private static Result<Verified> VerifyInputs(NativeBuildRequest request, TextWriter? log)
    {
        Result<IReadOnlyDictionary<string, string>> tools = BuildEnvironment.Create(request.Environment);
        if (!tools.Succeeded)
            return tools.Failure;
        Result<string> host = NativeRids.Host();
        if (!host.Succeeded)
            return host.Failure;
        if (Check.That(request.Rid == host.Value, "Cross-compilation is not qualified; select this native host's RID") is { } cross)
            return cross;
        Result<LibchdrAuthority> authority = Authorities.ReadLibchdr(request.Root);
        if (!authority.Succeeded)
            return authority.Failure;
        Result<long> epoch = SourceIdentity.Verify(request.Source, authority.Value.Pin, request.Environment, log);
        if (!epoch.Succeeded)
            return epoch.Failure;
        Result<ImmutableArray<string>> exports = NativeAllowlists.ExpectedExports(request.Root, request.Source);
        if (!exports.Succeeded)
            return exports.Failure;
        Result<IReadOnlyDictionary<string, string>> build = BuildEnvironment.Create(request.Environment, epoch.Value);
        if (!build.Succeeded)
            return build.Failure;
        return new Verified(authority.Value, epoch.Value, exports.Value, tools.Value, build.Value);
    }

    private static Result<Configured> Configure(NativeBuildRequest request, OutputLayout layout, Verified verified, TextWriter? log)
    {
        Result<JsonObject> tools = NativeToolchain.CollectTools(request.Rid, verified.Tools, log);
        if (!tools.Succeeded)
            return tools.Failure;
        if (NativeBuildInfo.WritePlaceholder(layout.BuildInfoHeader) is { } placeholder)
            return placeholder;
        CMakeFileApi.WriteQueries(layout.Build);
        // Static per-RID settings come from the preset; only verified paths and the compiler are passed here.
        Result<string> configure = ProcessRunner.Run(ConfigureArguments(request, layout), verified.Build, log);
        if (!configure.Succeeded)
            return configure.Failure;
        Result<CMakeReply> reply = CMakeFileApi.Read(layout.Build);
        if (!reply.Succeeded)
            return reply.Failure;
        Result<JsonObject> recipe = Recipe(request, layout, verified.Authority, verified.Epoch, tools.Value, reply.Value);
        if (!recipe.Succeeded)
            return recipe.Failure;
        return new Configured(tools.Value, reply.Value, recipe.Value);
    }

    /// <summary>Constructs CMake's arguments after the request's host, paths and compiler have been verified.</summary>
    internal static string[] ConfigureArguments(NativeBuildRequest request, OutputLayout layout) =>
    [
        "cmake", "-S", Path.Combine(request.Root, "native", "libchdr"), "-B", layout.Build, "--preset", request.Rid,
        "-DCMAKE_C_COMPILER=" + request.Compiler, "-DMOONLARK_UPSTREAM=" + request.Source,
        "-DMOONLARK_SOURCE_ROOT=" + request.Root, "-DMOONLARK_NATIVE_OUTPUT=" + layout.Native,
        "-DMOONLARK_PROBE_OUTPUT=" + layout.Output, "-DMOONLARK_BUILD_INFO_HEADER=" + layout.BuildInfoHeader,
    ];

    private static Result<JsonObject> Recipe(NativeBuildRequest request, OutputLayout layout, LibchdrAuthority authority, long epoch,
        JsonObject tools, CMakeReply reply)
    {
        Result<JsonObject> configuration = BuildRecipe.SelectConfiguration(reply.Cache);
        if (!configuration.Succeeded)
            return configuration.Failure;
        if (BuildRecipe.ValidateConfiguration(configuration.Value, request.Rid, authority.Pin.Features) is { } flags)
            return flags;
        Result<JsonObject> toolchain = NativeToolchain.WithCompiler(tools, reply.Compiler, request.Rid);
        if (!toolchain.Succeeded)
            return toolchain.Failure;
        Result<JsonObject> inputs = BuildRecipe.InputDigests(request.Root);
        if (!inputs.Succeeded)
            return inputs.Failure;
        JsonObject recipe = BuildRecipe.Create(authority, request.Rid, configuration.Value, toolchain.Value, epoch, inputs.Value);
        string[] locations = [request.Root, request.Source, layout.Output, layout.Build, reply.Compiler.Path,
            .. HomeVariables.Select(name => request.Environment.GetValueOrDefault(name) ?? "")];
        return BuildRecipe.RejectLocations(recipe, locations) is { } leak ? leak : recipe;
    }

    private static Failure? Compile(OutputLayout layout, Verified verified, Configured configured, TextWriter? log)
    {
        JsonObject info = BuildRecipe.BuildInfo(verified.Authority.Pin, verified.Authority.ManagedVersion, configured.Recipe);
        if (NativeBuildInfo.Write(layout.BuildInfoHeader, info) is { } header)
            return header;
        if (!LayoutProbe.IsBuiltBy(configured.Reply.Compiler))
            return new Failure("Compiler cannot build the required layout probe");
        Result<string> build = ProcessRunner.Run(["cmake", "--build", layout.Build, "--target", "moonlark_chdr", LayoutProbe.Target], verified.Build, log);
        return build.Succeeded ? null : build.Failure;
    }

    private static Result<NativeBuildResult> Finish(NativeBuildRequest request, OutputLayout layout, Verified verified, Configured configured,
        string programSha256, TextWriter? log)
    {
        LibchdrAuthority authority = verified.Authority;
        JsonObject info = BuildRecipe.BuildInfo(authority.Pin, authority.ManagedVersion, configured.Recipe);
        Result<Inspection> inspection = BinaryInspection.Inspect(layout.Binary, request.Rid, verified.Exports, info, verified.Tools, log);
        if (!inspection.Succeeded)
            return inspection.Failure;
        Result<JsonObject> measured = LayoutProbe.Measure(new ProbeRun(request.Root, request.Rid, layout.Output, layout.Build,
            authority.Pin, configured.Reply.Compiler, programSha256, verified.Build), log);
        if (!measured.Succeeded)
            return measured.Failure;
        if (Unchanged(request, layout, verified, configured, log) is { } changed)
            return changed;
        File.WriteAllBytes(layout.ProbeReceipt, JsonFields.Serialize(measured.Value, sortKeys: false));
        var locations = new JsonObject
        {
            ["buildDirectory"] = layout.Build,
            ["buildInfoHeader"] = layout.BuildInfoHeader,
            ["compiler"] = configured.Reply.Compiler.Path,
            ["nativeOutput"] = layout.Native,
            ["sourceRoot"] = request.Root,
            ["upstream"] = request.Source,
        };
        byte[] manifest = JsonFields.Serialize(NativeManifest.Create(authority, request.Rid, layout.Binary, configured.Recipe, inspection.Value, locations), sortKeys: true);
        // Verify exactly the bytes that will be written.
        Result<JsonObject> written = JsonFields.ParseObject(manifest, "Manifest");
        if (!written.Succeeded)
            return written.Failure;
        if (NativeVerify.VerifyManifest(written.Value, layout.Binary, request.Root) is { } invalid)
            return invalid;
        File.WriteAllBytes(layout.Manifest, manifest);
        return new NativeBuildResult(layout.Manifest, layout.ProbeReceipt);
    }

    /// <summary>Re-verifies the source and recomputes the recipe from current authorities, inputs and the CMake reply.</summary>
    private static Failure? Unchanged(NativeBuildRequest request, OutputLayout layout, Verified verified, Configured configured, TextWriter? log)
    {
        Result<LibchdrAuthority> authority = Authorities.ReadLibchdr(request.Root);
        if (!authority.Succeeded)
            return authority.Failure;
        Result<long> epoch = SourceIdentity.Verify(request.Source, authority.Value.Pin, request.Environment, log);
        if (!epoch.Succeeded)
            return epoch.Failure;
        Result<CMakeReply> reply = CMakeFileApi.Read(layout.Build);
        if (!reply.Succeeded)
            return reply.Failure;
        Result<JsonObject> recipe = Recipe(request, layout, authority.Value, epoch.Value, configured.Tools, reply.Value);
        if (!recipe.Succeeded)
            return recipe.Failure;
        return Check.That(JsonFields.SameCanonical(recipe.Value, configured.Recipe) && epoch.Value == verified.Epoch,
            "Build recipe changed during compilation");
    }
}
