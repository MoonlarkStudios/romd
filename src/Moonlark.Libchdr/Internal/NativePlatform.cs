using System.Runtime.InteropServices;

namespace Moonlark.Libchdr.Internal;

internal static class NativePlatform
{
    internal static (string Rid, string Filename) Current() => Resolve(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unsupported",
        RuntimeInformation.ProcessArchitecture, RuntimeInformation.RuntimeIdentifier);

    internal static (string Rid, string Filename) Resolve(string system, Architecture architecture, string runtimeIdentifier) =>
        (system, architecture) switch
        {
            ("windows", Architecture.X64) => ("win-x64", "moonlark_chdr.dll"),
            ("macos", Architecture.Arm64) => ("osx-arm64", "libmoonlark_chdr.dylib"),
            ("linux", Architecture.X64) when !runtimeIdentifier.Contains("musl", StringComparison.Ordinal) => ("linux-x64", "libmoonlark_chdr.so"),
            ("linux", Architecture.Arm64) when !runtimeIdentifier.Contains("musl", StringComparison.Ordinal) => ("linux-arm64", "libmoonlark_chdr.so"),
            _ => throw new PlatformNotSupportedException($"libchdr does not support {runtimeIdentifier}/{architecture}. Supported platforms: linux-x64, linux-arm64, osx-arm64, win-x64; Linux requires glibc."),
        };

    internal static IEnumerable<string> AssetPaths(string rid, string filename)
    {
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string directories)
        {
            foreach (string directory in directories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                if (Path.IsPathFullyQualified(directory))
                    yield return Path.Combine(directory, filename);
        }
        yield return Path.Combine(AppContext.BaseDirectory, filename);
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", filename);
    }
}
