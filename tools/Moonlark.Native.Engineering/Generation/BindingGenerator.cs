using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Native;

namespace Moonlark.Native.Engineering.Generation;

/// <summary>The single external tool step. Tests substitute it to observe the command or simulate the pinned generator.</summary>
internal delegate Failure? GeneratorTool(IReadOnlyList<string> command, IReadOnlyDictionary<string, string> environment,
    string workingDirectory, TextWriter log);

/// <summary>Host compiler arguments libclang cannot discover itself, such as the macOS SDK root.</summary>
internal delegate Result<ImmutableArray<string>> PlatformArguments(IReadOnlyDictionary<string, string> environment, TextWriter log);

/// <summary>A successful run: how many imports were generated and the exact command the tool received.</summary>
internal sealed record GenerationResult(int ExportCount, ImmutableArray<string> Command);

/// <summary>
/// Generates the internal raw bindings and the build contract from the exact pinned headers with the pinned
/// ClangSharp CLI. It never installs or downloads a tool; check mode compares without writing tracked files.
/// </summary>
internal static partial class BindingGenerator
{
    internal const string ToolManifestPath = ".config/dotnet-tools.json";
    internal const string ToolId = "clangsharppinvokegenerator";
    internal const string ToolCommand = "ClangSharpPInvokeGenerator";
    internal const string ToolVersion = "21.1.8.4";
    internal const string ExpectedInputSha256 = "872a88e9328c9e420fb7c5ced2ba3ef8927f6e5a5f7aea55f2a6fbe1bab82cf6";
    internal const string BindingsPath = "src/Moonlark.Libchdr/Interop/Libchdr.g.cs";
    internal const string ContractPath = "src/Moonlark.Libchdr/Internal/NativeBuildContract.g.cs";
    internal const string CommandEvidenceFile = "generation-command.json";
    internal const string LogFile = "generate.log";

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>Runs the pinned local tool through its manifest; a missing restore is reported as a failed command.</summary>
    internal static GeneratorTool PinnedTool { get; } = (command, environment, workingDirectory, log) =>
    {
        Result<string> run = ProcessRunner.Run(command, environment, log, workingDirectory);
        return run.Succeeded ? null : run.Failure;
    };

    /// <summary>Uses the existing platform SDK; libclang does not discover Apple's SDK.</summary>
    internal static Result<ImmutableArray<string>> HostPlatformArguments(IReadOnlyDictionary<string, string> environment, TextWriter log)
    {
        if (!OperatingSystem.IsMacOS())
            return ImmutableArray<string>.Empty;
        ProcessOutput sdk = ProcessRunner.Execute(["xcrun", "--show-sdk-path"], environment, log);
        string path = sdk.Stdout.Trim();
        if (Check.That(sdk.ExitCode == 0 && Path.IsPathRooted(path) && File.Exists(Path.Combine(path, "usr/include/stdio.h")),
            "Existing macOS SDK must provide an absolute standard-header root") is { } invalid)
            return invalid;
        return ImmutableArray.Create("--additional=-isysroot" + path);
    }

    /// <summary>
    /// Runs the pinned tool once. An optional <paramref name="clangResources"/> directory below artifacts supplies libclang's
    /// builtin headers, for hosts without a version-matched Clang; it is fingerprinted before and after the tool runs.
    /// </summary>
    internal static Result<GenerationResult> Run(string root, string dotnet, bool check, IReadOnlyDictionary<string, string> environment,
        GeneratorTool tool, PlatformArguments platform, string? clangResources = null)
    {
        Result<LibchdrAuthority> authority = Authorities.ReadLibchdr(root);
        if (!authority.Succeeded)
            return authority.Failure;
        if (VerifyToolPin(root) is { } toolPin)
            return toolPin;
        Result<IReadOnlyDictionary<string, string>> env = BuildEnvironment.Create(environment);
        if (!env.Succeeded)
            return env.Failure;
        using var preflight = new StringWriter();
        if (VerifyInputs(root, authority.Value.Pin, env.Value, preflight) is { } before)
            return before;
        Result<JsonObject?> resources = VerifyResources(root, clangResources);
        if (!resources.Succeeded)
            return resources.Failure;
        Result<string> output = PrepareOutput(root);
        if (!output.Succeeded)
            return output.Failure;
        using var log = new StreamWriter(Path.Combine(output.Value, LogFile));
        log.Write(preflight.ToString());
        PlatformArguments selected = platform;
        if (clangResources is not null && resources.Value is { } inventory)
        {
            log.Write("Clang resources: " + JsonFields.Compact(inventory) + "\n");
            selected = (variables, writer) => platform(variables, writer)
                .Then(host => (Result<ImmutableArray<string>>)host.AddRange(ClangResources.Arguments(clangResources)));
        }
        Result<ImmutableArray<string>> command = Invoke(root, dotnet, env.Value, tool, selected, output.Value, log);
        if (!command.Succeeded)
            return command.Failure;
        if (VerifyInputs(root, authority.Value.Pin, env.Value, log) is { } after)
            return after;
        if (clangResources is not null && ResourcesUnchanged(root, clangResources, resources.Value!) is { } changed)
            return changed;
        Result<DerivedSources> derived = Derive(root, authority.Value);
        if (!derived.Succeeded)
            return derived.Failure;
        if (Publish(root, check, derived.Value) is { } publish)
            return publish;
        return new GenerationResult(derived.Value.ExportCount, command.Value);
    }

    private static Failure? VerifyToolPin(string root)
    {
        string? version = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ToolManifestPath))) is JsonObject manifest
            && manifest["tools"] is JsonObject tools && tools[ToolId] is JsonObject entry
            && entry["version"] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        return Check.That(version == ToolVersion, "Generator pin changed without a reviewed regeneration");
    }

    /// <summary>Checked before and after the tool runs, so an input changed during generation cannot be committed.</summary>
    private static Failure? VerifyInputs(string root, LibchdrPin pin, IReadOnlyDictionary<string, string> environment, TextWriter log)
    {
        Result<long> identity = SourceIdentity.Verify(Path.Combine(root, GenerationConfiguration.SourcePath), pin, environment, log);
        if (!identity.Succeeded)
            return identity.Failure;
        string entry = Path.Combine(root, GenerationConfiguration.EntryPath);
        if (Check.That(File.Exists(entry) && !ArtifactsPath.IsLink(entry) && Digest.Sha256File(entry) == ExpectedInputSha256,
            "Generation entry point must include the verified header and explicit shim path") is { } input)
            return input;
        // The shim's const-char-pointer ABI needs no transitive include directory.
        // It is included by explicit path, avoiding an unchecked -I wrapper root.
        string shim = Path.Combine(root, GenerationConfiguration.ShimPath);
        return Check.That(File.Exists(shim) && !ArtifactsPath.IsLink(shim) && !Include().IsMatch(File.ReadAllText(shim)),
            "Build-info ABI header must be a direct, self-contained input");
    }

    /// <summary>The resource tree must be complete and outside the generation output, which every run recreates.</summary>
    private static Result<JsonObject?> VerifyResources(string root, string? clangResources)
    {
        if (clangResources is null)
            return (JsonObject?)null;
        if (ArtifactsPath.IsWithin(ArtifactsPath.Resolve(clangResources), ArtifactsPath.Resolve(Path.Combine(root, GenerationConfiguration.Libchdr.OutputDirectory))))
            return new Failure("Clang resources must not be inside the generation output " + GenerationConfiguration.Libchdr.OutputDirectory);
        Result<JsonObject> inventory = ClangResources.Verify(root, clangResources);
        return inventory.Succeeded ? inventory.Value : inventory.Failure;
    }

    private static Failure? ResourcesUnchanged(string root, string clangResources, JsonObject before)
    {
        Result<JsonObject> after = ClangResources.Verify(root, clangResources);
        if (!after.Succeeded)
            return after.Failure;
        return Check.That(JsonFields.SameCanonical(before, after.Value), "Clang resource headers changed during generation");
    }

    /// <summary>A successful invocation must create fresh output, never reuse a stale file or log.</summary>
    private static Result<string> PrepareOutput(string root)
    {
        Result<string> output = ArtifactsPath.ValidateDirectory(Path.Combine(root, GenerationConfiguration.Libchdr.OutputDirectory), root);
        if (!output.Succeeded)
            return output;
        if (Directory.Exists(output.Value))
            Directory.Delete(output.Value, recursive: true);
        Directory.CreateDirectory(output.Value);
        return output;
    }

    private static Result<ImmutableArray<string>> Invoke(string root, string dotnet, IReadOnlyDictionary<string, string> environment,
        GeneratorTool tool, PlatformArguments platform, string output, TextWriter log)
    {
        Result<ImmutableArray<string>> host = platform(environment, log);
        if (!host.Succeeded)
            return host.Failure;
        ImmutableArray<string> command = [dotnet, "tool", "run", ToolCommand, "--", .. GenerationConfiguration.Libchdr.ToolArguments(host.Value)];
        File.WriteAllText(Path.Combine(output, CommandEvidenceFile), Evidence(root, command));
        if (tool(command, environment, root, log) is { } failed)
            return failed;
        return command;
    }

    private static string Evidence(string root, ImmutableArray<string> command) =>
        new JsonObject
        {
            ["tool"] = ToolCommand,
            ["toolVersion"] = ToolVersion,
            ["workingDirectory"] = root,
            ["command"] = new JsonArray([.. command.Select(argument => (JsonNode?)argument)]),
        }.ToJsonString(IndentedJson) + "\n";

    private sealed record DerivedSources(string Bindings, string Contract, int ExportCount);

    /// <summary>Builds both tracked texts completely before either is compared or written.</summary>
    private static Result<DerivedSources> Derive(string root, LibchdrAuthority authority)
    {
        GenerationConfiguration configuration = GenerationConfiguration.Libchdr;
        Result<string> output = ArtifactsPath.ValidateDirectory(Path.Combine(root, configuration.OutputDirectory), root);
        if (!output.Succeeded)
            return output.Failure;
        string generated = Path.Combine(output.Value, configuration.OutputFileName);
        if (Check.That(File.Exists(generated) && !ArtifactsPath.IsLink(generated),
            "Generator produced no fresh output at " + configuration.Output) is { } missing)
            return missing;
        string header = File.ReadAllText(Path.Combine(root, GenerationConfiguration.SourcePath, Authorities.HeaderPaths[0]));
        Result<ImmutableArray<string>> exports = ExportInventory.FromHeader(header);
        if (!exports.Succeeded)
            return exports.Failure;
        Result<string> bindings = GeneratedBindings.Validate(File.ReadAllText(generated), exports.Value);
        if (!bindings.Succeeded)
            return bindings.Failure;
        Result<string> contract = NativeBuildContractSource.Render(authority, exports.Value);
        if (!contract.Succeeded)
            return contract.Failure;
        return new DerivedSources(bindings.Value, contract.Value, exports.Value.Length);
    }

    private static Failure? Publish(string root, bool check, DerivedSources derived)
    {
        string bindings = Path.Combine(root, BindingsPath);
        string contract = Path.Combine(root, ContractPath);
        if (check)
        {
            if (!Matches(bindings, derived.Bindings))
                return new Failure("Generated binding drift; regenerate with the pinned tool");
            return Check.That(Matches(contract, derived.Contract), "Generated build-contract drift; regenerate with the pinned tool");
        }
        Write(bindings, derived.Bindings);
        Write(contract, derived.Contract);
        return null;
    }

    private static bool Matches(string path, string expected) => File.Exists(path) && File.ReadAllText(path) == expected;

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [GeneratedRegex(@"^\s*#\s*include\b", RegexOptions.Multiline)]
    private static partial Regex Include();
}
