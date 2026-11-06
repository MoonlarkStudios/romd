using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

/// <summary>
/// The location-independent build identity. buildId is embedded in the binary, so the recipe holds only
/// effective settings, tool identities and repository-relative input digests, never a local path.
/// </summary>
internal static partial class BuildRecipe
{
    internal static ImmutableArray<string> NativeInputs { get; } =
    [
        "native/libchdr/CMakeLists.txt", "native/libchdr/CMakePresets.json", "native/libchdr/exports.txt",
        "native/libchdr/moonlark_chdr_build_info.c", "native/libchdr/moonlark_chdr_build_info.h", "native/libchdr/probe/libchdr_layout.c",
        "native/libchdr/HuffmanLookup.cmake", "native/libchdr/probe/huffman_lookup.c",
    ];

    /// <summary>The engineering code that implements the recipe; every C# file below these directories is an input.</summary>
    internal static ImmutableArray<string> CodeDirectories { get; } =
        ["tools/Moonlark.Native.Engineering/Core", "tools/Moonlark.Native.Engineering/Native"];

    private static readonly FrozenSet<string> ExactSettings = new[]
    {
        "CMAKE_BUILD_TYPE", "CMAKE_GENERATOR", "CMAKE_OSX_ARCHITECTURES", "CMAKE_OSX_DEPLOYMENT_TARGET",
        "CMAKE_MSVC_RUNTIME_LIBRARY", "CMAKE_C_STANDARD", "CMAKE_C_STANDARD_REQUIRED", "CMAKE_C_EXTENSIONS",
        "CMAKE_POSITION_INDEPENDENT_CODE", "CMAKE_C_VISIBILITY_PRESET", "CMAKE_VISIBILITY_INLINES_HIDDEN",
        "CMAKE_INTERPROCEDURAL_OPTIMIZATION", "INSTALL_STATIC_LIBS", NativePolicy.RidSetting,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] SettingFamilies =
    [
        "BUILD_", "CHDR_", "MINIZ_", "WITH_", "CMAKE_C_FLAGS", "CMAKE_EXE_LINKER_FLAGS",
        "CMAKE_MODULE_LINKER_FLAGS", "CMAKE_SHARED_LINKER_FLAGS", "CMAKE_STATIC_LINKER_FLAGS",
    ];

    /// <summary>Path-typed and CMake-internal entries never join a setting family.</summary>
    private static readonly FrozenSet<string> ExcludedFamilyTypes =
        new[] { "PATH", "FILEPATH", "INTERNAL", "STATIC" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Selects the explicit allowlist of location-independent effective cache settings, sorted ordinally.</summary>
    internal static Result<JsonObject> SelectConfiguration(IEnumerable<CacheEntry> cache)
    {
        var selected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (CacheEntry entry in cache.Where(IsSelected))
            if (!selected.TryAdd(entry.Name, entry.Value))
                return new Failure("Duplicate CMake cache entry: " + entry.Name);
        return new JsonObject(selected.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));
    }

    /// <summary>Fails when the preset was not applied or a pinned feature or the macOS floor is not effective.</summary>
    internal static Failure? ValidateConfiguration(JsonObject configuration, string rid, IEnumerable<string> features)
    {
        if (Check.That(configuration.Count > 0 && JsonFields.HasOnlyStrings(configuration), "Build flags drift: configuration must map settings to strings") is { } shape)
            return shape;
        string? missing = NativePolicy.RequiredSettings(rid).FirstOrDefault(name => !configuration.ContainsKey(name));
        if (missing is not null)
            return new Failure("Build flags drift: effective configuration lacks " + missing);
        if (Check.That(Setting(configuration, NativePolicy.RidSetting) == rid, "Recipe RID/family version mismatch: configure preset selected another RID") is { } preset)
            return preset;
        foreach (string feature in features)
        {
            if (!NativePolicy.FeatureSettings.TryGetValue(feature, out string? name))
                return new Failure("Build flags drift: unknown feature " + feature);
            if (Setting(configuration, name) != "ON")
                return new Failure($"Build flags drift: {feature} requires {name}=ON");
        }
        return rid != "osx-arm64" || Setting(configuration, NativePolicy.MacOsDeploymentSetting) == NativePolicy.MacOsMinimum
            ? null
            : new Failure($"Build flags drift: {NativePolicy.MacOsDeploymentSetting} must equal the macOS floor {NativePolicy.MacOsMinimum}");
    }

    /// <summary>SHA-256 of every recipe input, keyed by sorted, forward-slash, repository-relative paths.</summary>
    internal static Result<JsonObject> InputDigests(string root)
    {
        IEnumerable<string> code = CodeDirectories.SelectMany(directory => Directory.Exists(Path.Combine(root, directory))
            ? Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            : []);
        var digests = new JsonObject();
        foreach (string relative in NativeInputs.Concat(code).Order(StringComparer.Ordinal))
        {
            string path = Path.Combine([root, .. relative.Split('/')]);
            if (!File.Exists(path) || ArtifactsPath.IsLink(path))
                return new Failure("Recipe input must be a regular file: " + relative);
            digests[relative] = Digest.Sha256File(path);
        }
        return digests;
    }

    internal static JsonObject Create(LibchdrAuthority authority, string rid, JsonObject configuration, JsonObject toolchain, long sourceDateEpoch, JsonObject inputs) => new()
    {
        ["schemaVersion"] = NativePolicy.RecipeSchemaVersion,
        ["recipeVersion"] = NativePolicy.BuildRecipeVersion,
        ["source"] = authority.Pin.Document.DeepClone(),
        ["managedVersion"] = authority.ManagedVersion,
        ["nativeVersion"] = authority.ManagedVersion,
        ["rid"] = rid,
        ["configuration"] = configuration.DeepClone(),
        ["toolchain"] = toolchain.DeepClone(),
        ["sourceDateEpoch"] = sourceDateEpoch,
        ["inputSha256"] = inputs.DeepClone(),
    };

    /// <summary>buildId identifies the recipe; it is deliberately independent of the binary's own SHA-256.</summary>
    internal static string BuildId(JsonObject recipe) => CanonicalJson.Sha256(recipe);

    /// <summary>The build-info contract read by NativeBuildInfoReader: schema and ABI 1, family versions, pin and buildId.</summary>
    internal static JsonObject BuildInfo(LibchdrPin pin, string version, JsonObject recipe) => new()
    {
        ["schemaVersion"] = 1,
        ["abiVersion"] = 1,
        ["managedVersion"] = version,
        ["nativeVersion"] = version,
        ["upstreamVersion"] = pin.UpstreamVersion,
        ["upstreamCommit"] = pin.Commit,
        ["headers"] = pin.Document["headers"]?.DeepClone(),
        ["features"] = pin.Document["features"]?.DeepClone(),
        ["buildId"] = BuildId(recipe),
    };

    /// <summary>Fails closed if any recipe key or value mentions a local path, which would make buildId location-dependent.</summary>
    internal static Failure? RejectLocations(JsonNode recipe, IEnumerable<string> locations)
    {
        string[] paths = [.. locations.Where(path => path.Length > 1 && Path.IsPathRooted(path))
            .SelectMany(path => new[] { Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(ArtifactsPath.Resolve(path)) })
            .Distinct(StringComparer.Ordinal)];
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Unknown host paths (package-manager prefixes, TMPDIR, SDKs) fail too: any rooted path token, including
        // flag-embedded ones such as -I/opt/x. MSVC options start with '/', so Windows recipes check drive and UNC roots.
        Regex rooted = OperatingSystem.IsWindows() ? WindowsRootedPath() : UnixRootedPath();
        string? leak = Strings(recipe).FirstOrDefault(text => paths.Any(path => text.Contains(path, comparison)) || rooted.IsMatch(text));
        return leak is null ? null : new Failure("Build recipe is location-dependent: " + leak);
    }

    [GeneratedRegex(@"(?:^|[\s=;,""'()]|-[A-Za-z])(?:/|~/)")]
    private static partial Regex UnixRootedPath();

    [GeneratedRegex(@"(?:^|[\s=;,:""'()]|[-/][A-Za-z])(?:[A-Za-z]:[\\/]|\\\\)")]
    private static partial Regex WindowsRootedPath();

    private static bool IsSelected(CacheEntry entry) =>
        ExactSettings.Contains(entry.Name) || (!ExcludedFamilyTypes.Contains(entry.Type)
            && SettingFamilies.Any(prefix => entry.Name.StartsWith(prefix, StringComparison.Ordinal)));

    private static string? Setting(JsonObject configuration, string name) =>
        configuration[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonObject value => value.SelectMany(property => Strings(property.Value).Prepend(property.Key)),
        JsonArray value => value.SelectMany(Strings),
        JsonValue value when value.TryGetValue(out string? text) => [text],
        _ => [],
    };
}
