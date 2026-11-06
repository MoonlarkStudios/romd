using System.Collections.Frozen;
using System.Collections.Immutable;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Native;

/// <summary>Exact export and dependency allowlists. Linker export files are generated from exports.txt by CMake.</summary>
internal static class NativeAllowlists
{
    /// <summary>The committed allowlist; with a source checkout it must equal the pinned header's exact declaration sequence.</summary>
    internal static Result<ImmutableArray<string>> ExpectedExports(string root, string? source)
    {
        Result<ImmutableArray<string>> allowlist = ExportInventory.ReadAllowlist(root);
        if (!allowlist.Succeeded || source is null)
            return allowlist;
        string header = File.ReadAllText(Path.Combine(source, Authorities.HeaderPaths[0]));
        return ExportInventory.MatchesHeader(allowlist.Value, header) is { } drift ? drift : allowlist;
    }

    /// <summary>Actual exports must be unique and exactly the expected set; returns them sorted ordinally.</summary>
    internal static Result<ImmutableArray<string>> ValidateSymbols(IReadOnlyList<string> actual, IReadOnlyList<string> expected)
    {
        if (Check.That(actual.Distinct(StringComparer.Ordinal).Count() == actual.Count, "Duplicate native exports") is { } duplicate)
            return duplicate;
        string[] missing = [.. expected.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] extra = [.. actual.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        if (missing.Length > 0 || extra.Length > 0)
            return new Failure($"Export mismatch; missing=[{string.Join(", ", missing)}]; unexpected=[{string.Join(", ", extra)}]");
        return actual.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>Dependencies must be unique, nonempty and allowlisted; returns them sorted ordinally.</summary>
    internal static Result<ImmutableArray<string>> ValidateDependencies(IReadOnlyList<string> actual, string rid)
    {
        if (Check.That(actual.Distinct(StringComparer.Ordinal).Count() == actual.Count, "Duplicate dependencies") is { } duplicate)
            return duplicate;
        FrozenSet<string> allowed = NativePolicy.Dependencies[rid];
        string[] extra = [.. actual.Where(item => !allowed.Contains(item)).Order(StringComparer.Ordinal)];
        if (extra.Length > 0)
            return new Failure($"Unexpected dynamic dependencies: [{string.Join(", ", extra)}]");
        if (Check.That(actual.Count > 0, "Expected a system runtime dependency") is { } none)
            return none;
        return actual.Order(StringComparer.Ordinal).ToImmutableArray();
    }
}
