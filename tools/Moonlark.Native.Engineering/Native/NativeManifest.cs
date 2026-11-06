using System.Text.Json.Nodes;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

/// <summary>
/// The local, unqualified build receipt. The location-independent recipe defines buildId; path-bearing
/// evidence is recorded separately under <c>locations</c> and never contributes to any identity.
/// </summary>
internal static class NativeManifest
{
    internal static JsonObject Create(LibchdrAuthority authority, string rid, string binary, JsonObject recipe, Inspection inspection,
        JsonObject locations) => new()
        {
            ["schemaVersion"] = NativePolicy.ManifestSchemaVersion,
            ["product"] = NativePolicy.Product,
            ["rid"] = rid,
            ["file"] = Path.GetFileName(binary),
            ["size"] = new FileInfo(binary).Length,
            ["sha256"] = Digest.Sha256File(binary),
            ["recipeVersion"] = NativePolicy.BuildRecipeVersion,
            ["upstreamVersion"] = authority.Pin.UpstreamVersion,
            ["upstreamCommit"] = authority.Pin.Commit,
            ["managedVersion"] = authority.ManagedVersion,
            ["nativeVersion"] = authority.ManagedVersion,
            ["buildInfo"] = BuildRecipe.BuildInfo(authority.Pin, authority.ManagedVersion, recipe),
            ["recipe"] = recipe.DeepClone(),
            ["symbols"] = JsonFields.Array(inspection.Symbols),
            ["dependencies"] = JsonFields.Array(inspection.Dependencies),
            ["platform"] = inspection.Platform.DeepClone(),
            ["locations"] = locations.DeepClone(),
            ["qualification"] = NativePolicy.Qualification,
            ["attestation"] = null,
        };
}
