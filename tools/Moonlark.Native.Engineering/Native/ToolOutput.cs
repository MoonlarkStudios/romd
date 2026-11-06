using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

/// <summary>Closed parsers for platform inspection tools: unrecognized output fails instead of being skipped.</summary>
internal static partial class ToolOutput
{
    /// <summary><c>nm -gU</c> (Mach-O) or <c>nm -D --defined-only</c> (ELF) defined exports.</summary>
    internal static Result<ImmutableArray<string>> ParseNm(string text, bool mac)
    {
        ImmutableArray<string>.Builder symbols = ImmutableArray.CreateBuilder<string>();
        foreach (string line in Lines(text))
        {
            Match match = NmLine().Match(line.Trim());
            if (!match.Success)
                return new Failure($"Unrecognized nm output: '{line}'");
            (string kind, string symbol) = (match.Groups[1].Value, match.Groups[2].Value);
            if (kind.ToUpperInvariant() is "U" or "W" or "V")
                return new Failure("Unexpected unresolved/weak export: " + symbol);
            if (mac && !symbol.StartsWith('_'))
                return new Failure("Unexpected Mach-O symbol spelling");
            symbols.Add(mac ? symbol[1..] : symbol);
        }
        return symbols.ToImmutable();
    }

    /// <summary><c>otool -L</c>: the loader-relative install name must appear exactly once; the rest are dependencies.</summary>
    internal static Result<ImmutableArray<string>> ParseMachODependencies(string text, string fileName)
    {
        string[] names = [.. Lines(text).Skip(1).Select(line => line.Trim().Split(" (", 2)[0])];
        string identity = "@loader_path/" + fileName;
        if (Check.That(names.Count(name => name == identity) == 1, "Unexpected Mach-O install name") is { } install)
            return install;
        return names.Where(name => name != identity).ToImmutableArray();
    }

    /// <summary><c>otool -l</c>: no RPATH, and exactly one minos equal to the policy floor.</summary>
    internal static Result<string> MacOsMinimum(string commands)
    {
        if (Check.That(!commands.Contains("LC_RPATH", StringComparison.Ordinal), "Native binary contains an RPATH") is { } rpath)
            return rpath;
        string[] minimum = [.. MinOs().Matches(commands).Select(match => match.Groups[1].Value)];
        return minimum is [NativePolicy.MacOsMinimum] ? minimum[0] : new Failure($"Unexpected macOS floor: [{string.Join(", ", minimum)}]");
    }

    /// <summary><c>readelf --version-info</c>: only numeric GLIBC versions, none above the policy ceiling.</summary>
    internal static Result<string> LinuxGlibcMaximum(string versions)
    {
        string[] identities = [.. GlibcVersion().Matches(versions).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal)];
        if (Check.That(identities.Length > 0 && identities.All(NumericVersion().IsMatch),
            "Missing, private or unsupported GLIBC version requirements") is { } invalid)
            return invalid;
        string maximum = identities.MaxBy(Version, VersionComparer.Instance)!;
        return VersionComparer.Instance.Compare(Version(maximum), Version(NativePolicy.LinuxMaximumGlibc)) <= 0
            ? maximum
            : new Failure($"GLIBC floor exceeds {NativePolicy.LinuxMaximumGlibc}: {maximum}");
    }

    internal static bool IsElfMachine(string header, string architecture) =>
        ElfMachine().Matches(header).Any(match => match.Groups[1].Value == architecture);

    internal static bool IsElfSharedLibrary(string header) => ElfDynamicType().IsMatch(header);

    internal static bool HasElfRunPath(string dynamic) => ElfRunPath().IsMatch(dynamic);

    internal static ImmutableArray<string> ElfNeeded(string dynamic) => [.. ElfNeededEntry().Matches(dynamic).Select(match => match.Groups[1].Value)];

    /// <summary>Line splitting with a final newline ending the last line rather than starting an empty one.</summary>
    private static string[] Lines(string text) => text.Length == 0 ? [] : (text.EndsWith('\n') ? text[..^1] : text).Split('\n');

    private static int[] Version(string value) => [.. value.Split('.').Select(part => int.Parse(part, CultureInfo.InvariantCulture))];

    private sealed class VersionComparer : IComparer<int[]>
    {
        internal static readonly VersionComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            int[] left = x ?? [], right = y ?? [];
            for (int index = 0; index < Math.Max(left.Length, right.Length); index++)
            {
                int order = (index < left.Length ? left[index] : -1).CompareTo(index < right.Length ? right[index] : -1);
                if (order != 0)
                    return order;
            }
            return 0;
        }
    }

    [GeneratedRegex(@"\A[0-9a-fA-F]+\s+([A-Za-z])\s+(\S+)\z")]
    private static partial Regex NmLine();

    [GeneratedRegex(@"^\s+minos ([0-9]+\.[0-9]+(?:\.[0-9]+)?)$", RegexOptions.Multiline)]
    private static partial Regex MinOs();

    [GeneratedRegex(@"\bGLIBC_([A-Za-z0-9_.]+)\b")]
    private static partial Regex GlibcVersion();

    [GeneratedRegex(@"\A[0-9]+(?:\.[0-9]+)+\z")]
    private static partial Regex NumericVersion();

    [GeneratedRegex(@"Machine:\s+(.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex ElfMachine();

    [GeneratedRegex(@"Type:\s+DYN\b")]
    private static partial Regex ElfDynamicType();

    [GeneratedRegex(@"\((?:RPATH|RUNPATH)\)")]
    private static partial Regex ElfRunPath();

    [GeneratedRegex(@"\(NEEDED\).*\[([^\]]+)\]")]
    private static partial Regex ElfNeededEntry();
}
