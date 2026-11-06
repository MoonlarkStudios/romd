using System.Collections;
using System.Collections.Frozen;
using System.Globalization;

namespace Moonlark.Native.Engineering.Core;

/// <summary>Rejects implicit compiler, linker and Git inputs instead of recording unreviewed flags.</summary>
internal static class BuildEnvironment
{
    private static readonly FrozenSet<string> ImplicitToolInputs = new[]
    {
        "CC", "CXX", "AS", "AR", "LD", "NM", "RANLIB", "RC",
        "CFLAGS", "CPPFLAGS", "CXXFLAGS", "LDFLAGS", "CL", "_CL_", "LINK", "_LINK_",
        "CPATH", "C_INCLUDE_PATH", "CPLUS_INCLUDE_PATH", "OBJC_INCLUDE_PATH",
        "LIBRARY_PATH", "COMPILER_PATH", "GCC_EXEC_PREFIX", "SDKROOT",
        "MACOSX_DEPLOYMENT_TARGET", "LD_PRELOAD", "LD_LIBRARY_PATH",
        "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH", "DYLD_FRAMEWORK_PATH",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static IReadOnlyDictionary<string, string> Current() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? "", StringComparer.Ordinal);

    internal static Result<IReadOnlyDictionary<string, string>> Create(IReadOnlyDictionary<string, string> environment, long? sourceDateEpoch = null)
    {
        string[] overrides = environment.Keys.Where(IsImplicitInput).Order(StringComparer.Ordinal).ToArray();
        if (overrides.Length > 0)
            return new Failure("Unrecorded build environment overrides: " + string.Join(", ", overrides));
        Result<IReadOnlyDictionary<string, string>> names = OperatingSystem.IsWindows() ? WindowsNames(environment) : new Dictionary<string, string>(environment, StringComparer.Ordinal);
        if (!names.Succeeded)
            return names.Failure;
        // Noninteractive commands never need the agent/terminal's display pager.
        Dictionary<string, string> result = names.Value.Where(pair => !pair.Key.Equals("GIT_PAGER", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(pair => pair.Key, pair => pair.Value, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (sourceDateEpoch is { } epoch)
            result["SOURCE_DATE_EPOCH"] = epoch.ToString(CultureInfo.InvariantCulture);
        return result;
    }

    /// <summary>Windows names are case-insensitive; conflicting aliases must not choose a value by enumeration order.</summary>
    internal static Result<IReadOnlyDictionary<string, string>> WindowsNames(IReadOnlyDictionary<string, string> environment)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in environment)
        {
            if (result.TryGetValue(name, out string? previous) && previous != value)
                return new Failure("Conflicting Windows environment aliases: " + name);
            result[name] = value;
        }
        return result;
    }

    private static bool IsImplicitInput(string name)
    {
        string upper = name.ToUpperInvariant();
        return ImplicitToolInputs.Contains(upper)
            || upper.StartsWith("CCC_", StringComparison.Ordinal)
            || upper.StartsWith("CMAKE_", StringComparison.Ordinal)
            || (upper.StartsWith("GIT_", StringComparison.Ordinal) && upper != "GIT_PAGER");
    }
}
