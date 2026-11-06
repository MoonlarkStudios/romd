using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Native;
using Moonlark.Native.Engineering.Tests.TestSupport;

namespace Moonlark.Native.Engineering.Tests.Native;

/// <summary>A temporary repository root holding the authorities and every recipe input, copied from the checkout.</summary>
internal sealed class NativeRoot : IDisposable
{
    internal const string Rid = "linux-x64";

    private readonly TemporaryDirectory _directory = new();

    internal NativeRoot()
    {
        TestRepository.CopyTo(Path, [Authorities.PinPath, Authorities.PropsPath, .. BuildRecipe.NativeInputs, .. BuildRecipe.CodeDirectories]);
        Authority = Authorities.ReadLibchdr(Path).Value;
    }

    internal string Path => _directory.Path;

    internal LibchdrAuthority Authority { get; }

    internal ImmutableArray<string> Allowlist => ExportInventory.ReadAllowlist(Path).Value;

    public void Dispose() => _directory.Dispose();

    /// <summary>A valid linux-x64 manifest, round-tripped through its written form, for a synthetic binary that is never loaded.</summary>
    internal (string Binary, JsonObject Manifest) Manifest(byte[]? contents = null)
    {
        string binary = System.IO.Path.Combine(Path, "native", NativeRids.LibraryFileName(Rid));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(binary)!);
        File.WriteAllBytes(binary, contents ?? "synthetic binary; never loaded"u8.ToArray());
        JsonObject recipe = BuildRecipe.Create(Authority, Rid, Configuration(Rid), Toolchain(), 12345, BuildRecipe.InputDigests(Path).Value);
        var inspection = new Inspection([.. Allowlist.Order(StringComparer.Ordinal)], ["libc.so.6"], new JsonObject { ["architecture"] = "synthetic" });
        JsonObject manifest = NativeManifest.Create(Authority, Rid, binary, recipe, inspection, new JsonObject { ["sourceRoot"] = Path });
        return (binary, JsonFields.ParseObject(JsonFields.Serialize(manifest, sortKeys: true), "Manifest").Value);
    }

    /// <summary>The minimal effective configuration a preset and the CMakeLists settings produce for <paramref name="rid"/>.</summary>
    internal static JsonObject Configuration(string rid)
    {
        var configuration = new JsonObject
        {
            ["CMAKE_BUILD_TYPE"] = "Release",
            ["CMAKE_GENERATOR"] = "Ninja",
            ["CMAKE_C_FLAGS_RELEASE"] = "-O3 -DNDEBUG",
            ["MOONLARK_RID"] = rid,
            ["CHDR_WANT_RAW_DATA_SECTOR"] = "ON",
            ["CHDR_WANT_SUBCODE"] = "ON",
            ["CHDR_VERIFY_BLOCK_CRC"] = "ON",
        };
        if (rid == "osx-arm64")
        {
            configuration["CMAKE_OSX_ARCHITECTURES"] = "arm64";
            configuration["CMAKE_OSX_DEPLOYMENT_TARGET"] = "14.0";
        }
        return configuration;
    }

    internal static JsonObject Toolchain() => new()
    {
        ["compilerId"] = "GNU",
        ["compilerVersion"] = "13.2.0",
        ["cmake"] = "synthetic cmake",
        ["ninja"] = "synthetic ninja",
    };

    /// <summary>Writes a pin and props that agree with a synthetic source, so authority checks pass and later checks are exercised.</summary>
    internal void WriteAuthorityFor(LibchdrPin pin)
    {
        string pinPath = System.IO.Path.Combine(Path, Authorities.PinPath);
        JsonObject document = JsonNode.Parse(File.ReadAllText(pinPath))!.AsObject();
        document["commit"] = pin.Commit;
        document["upstreamVersion"] = pin.UpstreamVersion;
        document["headers"] = new JsonObject(pin.Headers.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));
        File.WriteAllText(pinPath, document.ToJsonString());
        string propsPath = System.IO.Path.Combine(Path, Authorities.PropsPath);
        File.WriteAllText(propsPath, File.ReadAllText(propsPath)
            .Replace(Authority.Pin.Commit, pin.Commit, StringComparison.Ordinal)
            .Replace(Authority.Pin.UpstreamVersion, pin.UpstreamVersion, StringComparison.Ordinal));
    }
}
