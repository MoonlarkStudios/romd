using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Moonlark.Native.Engineering.Core;

/// <summary>The native export allowlist is derived from the pinned public header, never maintained by count.</summary>
internal static partial class ExportInventory
{
    internal const string AllowlistPath = "native/libchdr/exports.txt";
    internal const string BuildInfoExport = "moonlark_chdr_build_info";

    /// <summary>Public functions in header declaration order, followed by the build-info shim.</summary>
    internal static Result<ImmutableArray<string>> FromHeader(string headerText)
    {
        ImmutableArray<string> exports = [.. Declaration().Matches(headerText).Select(match => match.Groups[1].Value), BuildInfoExport];
        if (Check.That(exports.Length > 1, "Pinned header declares no public CHD_EXPORT functions") is { } empty)
            return empty;
        return Validate(exports);
    }

    internal static Result<ImmutableArray<string>> ReadAllowlist(string root) =>
        Validate([.. File.ReadAllText(Path.Combine(root, AllowlistPath)).Split('\n', StringSplitOptions.RemoveEmptyEntries)]);

    internal static Failure? MatchesHeader(IReadOnlyList<string> allowlist, string headerText)
    {
        Result<ImmutableArray<string>> expected = FromHeader(headerText);
        if (!expected.Succeeded)
            return expected.Failure;
        return Check.That(allowlist.SequenceEqual(expected.Value, StringComparer.Ordinal),
            "Export allowlist disagrees with pinned public header; regenerate it from the header");
    }

    private static Result<ImmutableArray<string>> Validate(ImmutableArray<string> exports)
    {
        if (Check.That(exports.Distinct(StringComparer.Ordinal).Count() == exports.Length, "Duplicate native export") is { } duplicate)
            return duplicate;
        string? invalid = exports.FirstOrDefault(name => !Spelling().IsMatch(name));
        return invalid is null ? exports : new Failure("Unexpected export spelling: " + invalid);
    }

    [GeneratedRegex(@"^CHD_EXPORT\s+[^;\n]*?\b(chd_\w+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\A(?:chd_[a-z0-9_]+|moonlark_chdr_build_info)\z")]
    private static partial Regex Spelling();
}
