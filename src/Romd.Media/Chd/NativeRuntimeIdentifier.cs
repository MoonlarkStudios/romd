using System.Runtime.InteropServices;

namespace Romd.Media.Chd;

/// <summary>The portable runtime identifier of this process, compared with the RID a native build recorded.</summary>
/// <remarks>Derived from the operating system and process architecture, never from a distribution-specific runtime RID.
/// musl processes get a distinct name, so a glibc build record never matches them.</remarks>
public static class NativeRuntimeIdentifier
{
    public static string? Current { get; } = Resolve(OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(),
        OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture, RuntimeInformation.RuntimeIdentifier);

    internal static string? Resolve(bool linux, bool macOS, bool windows, Architecture architecture, string runtimeIdentifier)
    {
        string? system = linux ? (runtimeIdentifier.Contains("musl", StringComparison.Ordinal) ? "linux-musl" : "linux")
            : macOS ? "osx" : windows ? "win" : null;
        return system is null ? null : system + "-" + architecture.ToString().ToLowerInvariant();
    }

    /// <summary>A bounded portable RID label: lowercase ASCII letters, digits, '.' and '-'.</summary>
    public static bool IsLabel(string? value) => value is { Length: > 0 and <= 32 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');
}
