using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

/// <summary>Independently re-checks a manifest against current authorities, recipe inputs, source identity and the actual binary.</summary>
internal static class NativeVerify
{
    /// <summary>Returns the verified binary path.</summary>
    internal static Result<string> Run(string root, string manifestPath, string source, IReadOnlyDictionary<string, string> environment, TextWriter? log) =>
        Run(root, manifestPath, source, environment, log, InspectionHost.Native);

    internal static Result<string> Run(string root, string manifestPath, string source, IReadOnlyDictionary<string, string> environment, TextWriter? log,
        InspectionHost inspectionHost)
    {
        Result<JsonObject> manifest = JsonFields.ReadObject(manifestPath, "Manifest");
        if (!manifest.Succeeded)
            return manifest.Failure;
        // Refuse traversal before using manifest content to construct any path.
        Result<string> rid = JsonFields.RequireString(manifest.Value, "rid");
        Result<string> file = JsonFields.RequireString(manifest.Value, "file");
        if (Check.That(rid.Succeeded && file.Succeeded && NativeRids.IsSupported(rid.Value) && file.Value == NativeRids.LibraryFileName(rid.Value),
            "Unsupported manifest RID/filename") is { } unsupported)
            return unsupported;
        string binary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, NativeOutput.NativeDirectory, file.Value);
        if (VerifyManifest(manifest.Value, binary, root) is { } invalid)
            return invalid;
        Result<LibchdrAuthority> authority = Authorities.ReadLibchdr(root);
        if (!authority.Succeeded)
            return authority.Failure;
        Result<long> epoch = SourceIdentity.Verify(source, authority.Value.Pin, environment, log);
        if (!epoch.Succeeded)
            return epoch.Failure;
        JsonObject recipe = (JsonObject)manifest.Value["recipe"]!;
        if (Check.That(JsonFields.RequireInteger(recipe, "sourceDateEpoch").Value == epoch.Value, "Source timestamp mismatch") is { } timestamp)
            return timestamp;
        Result<ImmutableArray<string>> exports = NativeAllowlists.ExpectedExports(root, source);
        if (!exports.Succeeded)
            return exports.Failure;
        Result<IReadOnlyDictionary<string, string>> tools = BuildEnvironment.Create(environment);
        if (!tools.Succeeded)
            return tools.Failure;
        Result<Inspection> inspection = BinaryInspection.Inspect(binary, rid.Value, exports.Value, (JsonObject)manifest.Value["buildInfo"]!, tools.Value, log, inspectionHost);
        if (!inspection.Succeeded)
            return inspection.Failure;
        foreach ((string name, JsonNode actual) in Recorded(inspection.Value))
            if (Check.That(JsonFields.SameCanonical(actual, manifest.Value[name]), $"Actual binary {name} differs from manifest") is { } differs)
                return differs;
        return binary;
    }

    /// <summary>Manifest-only checks; never loads or executes the binary.</summary>
    internal static Failure? VerifyManifest(JsonObject manifest, string binary, string root)
    {
        Result<LibchdrAuthority> authority = Authorities.ReadLibchdr(root);
        if (!authority.Succeeded)
            return authority.Failure;
        Result<string> rid = JsonFields.RequireString(manifest, "rid");
        if (Check.That(rid.Succeeded && NativeRids.IsSupported(rid.Value), "Unsupported manifest RID") is { } unsupported)
            return unsupported;
        return VerifyBinary(manifest, binary, rid.Value)
            ?? VerifyRecipe(manifest, authority.Value, rid.Value, root)
            ?? VerifyClaims(manifest, authority.Value, rid.Value, root);
    }

    private static Failure? VerifyBinary(JsonObject manifest, string binary, string rid)
    {
        Result<long> schema = JsonFields.RequireInteger(manifest, "schemaVersion");
        Result<string> product = JsonFields.RequireString(manifest, "product");
        if (Check.That(schema.Succeeded && schema.Value == NativePolicy.ManifestSchemaVersion && product.Succeeded && product.Value == NativePolicy.Product,
            "Unsupported manifest schema/product") is { } kind)
            return kind;
        Result<string> file = JsonFields.RequireString(manifest, "file");
        if (Check.That(file.Succeeded && file.Value == NativeRids.LibraryFileName(rid) && file.Value == Path.GetFileName(binary),
            "Manifest filename mismatch") is { } name)
            return name;
        if (Check.That(File.Exists(binary) && !ArtifactsPath.IsLink(binary), "Native binary must be a regular file") is { } regular)
            return regular;
        Result<long> size = JsonFields.RequireInteger(manifest, "size");
        if (Check.That(size.Succeeded && size.Value > 0 && new FileInfo(binary).Length == size.Value, "Native binary size mismatch") is { } length)
            return length;
        Result<string> sha256 = JsonFields.RequireString(manifest, "sha256");
        if (!sha256.Succeeded)
            return sha256.Failure;
        return Check.That(Authorities.Sha256().IsMatch(sha256.Value) && Digest.Sha256File(binary) == sha256.Value, "Native binary digest mismatch");
    }

    private static Failure? VerifyRecipe(JsonObject manifest, LibchdrAuthority authority, string rid, string root)
    {
        Result<JsonObject> recipe = JsonFields.RequireObject(manifest, "recipe");
        if (!recipe.Succeeded)
            return recipe.Failure;
        JsonObject value = recipe.Value;
        Result<long> schema = JsonFields.RequireInteger(value, "schemaVersion");
        if (Check.That(schema.Succeeded && schema.Value == NativePolicy.RecipeSchemaVersion && JsonFields.SameCanonical(value["source"], authority.Pin.Document),
            "Recipe source/pin mismatch") is { } source)
            return source;
        string version = authority.ManagedVersion;
        if (Check.That(Text(value, "rid") == rid && Text(value, "managedVersion") == version && Text(value, "nativeVersion") == version,
            "Recipe RID/family version mismatch") is { } identity)
            return identity;
        if (Check.That(Integer(value, "recipeVersion") == NativePolicy.BuildRecipeVersion, "Recipe build-recipe version mismatch") is { } recipeVersion)
            return recipeVersion;
        Result<long> epoch = JsonFields.RequireInteger(value, "sourceDateEpoch");
        if (Check.That(epoch.Succeeded && epoch.Value > 0, "Invalid SOURCE_DATE_EPOCH") is { } invalidEpoch)
            return invalidEpoch;
        Result<JsonObject> configuration = JsonFields.RequireObject(value, "configuration");
        if (!configuration.Succeeded)
            return new Failure("Build flags drift: recipe has no effective configuration");
        if (BuildRecipe.ValidateConfiguration(configuration.Value, rid, authority.Pin.Features) is { } flags)
            return flags;
        Result<JsonObject> toolchain = JsonFields.RequireObject(value, "toolchain");
        if (Check.That(toolchain.Succeeded && toolchain.Value.Count > 0 && JsonFields.HasOnlyStrings(toolchain.Value)
            && Text(toolchain.Value, "compilerVersion") is { Length: > 0 }, "Toolchain mismatch") is { } tools)
            return tools;
        if (NativeToolchain.CheckCompiler(Text(toolchain.Value, "compilerId") ?? "", rid) is { } compiler)
            return compiler;
        Result<JsonObject> inputs = BuildRecipe.InputDigests(root);
        if (!inputs.Succeeded)
            return inputs.Failure;
        if (Check.That(JsonFields.SameCanonical(value["inputSha256"], inputs.Value), "Input recipe digest mismatch") is { } digest)
            return digest;
        return Check.That(JsonFields.SameCanonical(manifest["buildInfo"], BuildRecipe.BuildInfo(authority.Pin, version, value)), "Build-info/recipe mismatch");
    }

    private static Failure? VerifyClaims(JsonObject manifest, LibchdrAuthority authority, string rid, string root)
    {
        string version = authority.ManagedVersion;
        if (Check.That(Text(manifest, "managedVersion") == version && Text(manifest, "nativeVersion") == version
            && Text(manifest, "upstreamCommit") == authority.Pin.Commit && Text(manifest, "upstreamVersion") == authority.Pin.UpstreamVersion
            && Integer(manifest, "recipeVersion") == NativePolicy.BuildRecipeVersion,
            "Manifest identity mismatch") is { } identity)
            return identity;
        Result<ImmutableArray<string>> allowlist = ExportInventory.ReadAllowlist(root);
        if (!allowlist.Succeeded)
            return allowlist.Failure;
        Result<ImmutableArray<string>> symbols = JsonFields.RequireStrings(manifest, "symbols").Then(items => NativeAllowlists.ValidateSymbols(items, allowlist.Value));
        if (!symbols.Succeeded)
            return symbols.Failure;
        Result<ImmutableArray<string>> dependencies = JsonFields.RequireStrings(manifest, "dependencies").Then(items => NativeAllowlists.ValidateDependencies(items, rid));
        if (!dependencies.Succeeded)
            return dependencies.Failure;
        if (Check.That(manifest["platform"] is JsonObject, "Manifest platform must be an object") is { } platform)
            return platform;
        if (Check.That(manifest["locations"] is JsonObject locations && JsonFields.HasOnlyStrings(locations),
            "Manifest locations must map names to path strings") is { } paths)
            return paths;
        return Check.That(Text(manifest, "qualification") == NativePolicy.Qualification
            && manifest.TryGetPropertyValue("attestation", out JsonNode? attestation) && attestation is null,
            "Local manifest cannot claim release qualification or attestation");
    }

    private static IEnumerable<(string Name, JsonNode Value)> Recorded(Inspection inspection) =>
    [
        ("symbols", JsonFields.Array(inspection.Symbols)),
        ("dependencies", JsonFields.Array(inspection.Dependencies)),
        ("platform", inspection.Platform),
    ];

    private static string? Text(JsonObject owner, string name) =>
        JsonFields.RequireString(owner, name) is { Succeeded: true } value ? value.Value : null;

    private static long? Integer(JsonObject owner, string name) =>
        JsonFields.RequireInteger(owner, name) is { Succeeded: true } value ? value.Value : null;
}
