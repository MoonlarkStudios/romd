using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Moonlark.Native.Engineering.Core;

internal static class NativeRids
{
    internal static ImmutableArray<string> Supported { get; } = ["linux-x64", "linux-arm64", "osx-arm64"];

    internal static bool IsSupported(string rid) => Supported.Contains(rid, StringComparer.Ordinal);

    internal static string LibraryFileName(string rid) => rid switch
    {
        "linux-x64" or "linux-arm64" => "libmoonlark_chdr.so",
        "osx-arm64" => "libmoonlark_chdr.dylib",
        _ => throw new ArgumentOutOfRangeException(nameof(rid), rid, "Unsupported RID"),
    };

    internal static Result<string> Host() => Resolve(
        OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsLinux() ? "Linux" : RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture);

    internal static Result<string> Resolve(string system, Architecture architecture) => (system, architecture) switch
    {
        ("Darwin", Architecture.Arm64) => "osx-arm64",
        ("Linux", Architecture.X64) => "linux-x64",
        ("Linux", Architecture.Arm64) => "linux-arm64",
        _ => new Failure($"Unsupported native host: {system}/{architecture}; supported: {string.Join(", ", Supported)}"),
    };
}
