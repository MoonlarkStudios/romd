using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Moonlark.Native.Engineering.Native;

/// <summary>Acceptance policy for local native artifacts. Build settings live only in CMakeLists.txt and CMakePresets.json.</summary>
internal static class NativePolicy
{
    internal const string Product = "moonlark_chdr";
    internal const int ManifestSchemaVersion = 3;
    internal const int RecipeSchemaVersion = 3;

    /// <summary>
    /// The explicit build-recipe identity recorded beside every binary. Bump it by hand whenever the recipe changes
    /// meaning: CMake files, presets, flags, toolchain policy or inspection policy.
    /// </summary>
    internal const int BuildRecipeVersion = 2;
    internal const string Qualification = "local-unqualified";
    internal const string MacOsMinimum = "14.0";
    internal const string LinuxMaximumGlibc = "2.31";
    internal const string RidSetting = "MOONLARK_RID";
    internal const string MacOsDeploymentSetting = "CMAKE_OSX_DEPLOYMENT_TARGET";

    internal static FrozenDictionary<string, FrozenSet<string>> Dependencies { get; } = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
    {
        ["osx-arm64"] = new[] { "/usr/lib/libSystem.B.dylib" }.ToFrozenSet(StringComparer.Ordinal),
        ["linux-x64"] = new[] { "libc.so.6", "libm.so.6", "libpthread.so.0" }.ToFrozenSet(StringComparer.Ordinal),
        ["linux-arm64"] = new[] { "libc.so.6", "libm.so.6", "libpthread.so.0" }.ToFrozenSet(StringComparer.Ordinal),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Each pinned feature requires its upstream option to be effectively ON.</summary>
    internal static FrozenDictionary<string, string> FeatureSettings { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["raw-sectors"] = "CHDR_WANT_RAW_DATA_SECTOR",
        ["subcode"] = "CHDR_WANT_SUBCODE",
        ["block-crc"] = "CHDR_VERIFY_BLOCK_CRC",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Effective settings that must be recorded for a RID; their absence means the preset was not applied.</summary>
    internal static ImmutableArray<string> RequiredSettings(string rid) => rid switch
    {
        "osx-arm64" => [.. CommonSettings, "CMAKE_OSX_ARCHITECTURES", MacOsDeploymentSetting],
        _ => CommonSettings,
    };

    internal static ImmutableArray<string> CompilerIds(string rid) => rid switch
    {
        "osx-arm64" => ["AppleClang", "Clang"],
        _ => ["GNU", "Clang"],
    };

    private static ImmutableArray<string> CommonSettings { get; } =
        ["CMAKE_BUILD_TYPE", "CMAKE_GENERATOR", RidSetting, "CHDR_WANT_RAW_DATA_SECTOR", "CHDR_WANT_SUBCODE", "CHDR_VERIFY_BLOCK_CRC"];
}
