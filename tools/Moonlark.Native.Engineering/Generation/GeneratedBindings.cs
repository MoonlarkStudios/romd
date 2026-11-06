using System.Text.RegularExpressions;
using Moonlark.Native.Engineering.Core;

namespace Moonlark.Native.Engineering.Generation;

/// <summary>
/// Preflight for raw ClangSharp output, which is committed verbatim. Unknown imports, missing or extra functions and
/// public containers fail before any tracked file is written; reflection tests recheck the compiled assembly.
/// </summary>
internal static partial class GeneratedBindings
{
    internal const string GeneratorIdentity = "[assembly: GeneratedCode(\"ClangSharp\", \"" + BindingGenerator.ToolVersion + "\")]";
    private const string MethodContainer = "internal static unsafe partial class NativeMethods";
    private const string RawNamespace = "namespace Moonlark.Libchdr.Interop;";

    /// <summary>Returns the output with LF line endings, or the first reason it is not the reviewed raw form.</summary>
    internal static Result<string> Validate(string generated, IReadOnlyCollection<string> expected)
    {
        string text = generated.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] names = Method().Matches(text).Select(match => match.Groups[1].Value).ToArray();
        HashSet<string> declared = names.ToHashSet(StringComparer.Ordinal);
        if (names.Length != declared.Count || !declared.SetEquals(expected))
        {
            IEnumerable<string> difference = declared.Except(expected, StringComparer.Ordinal).Union(expected.Except(declared, StringComparer.Ordinal), StringComparer.Ordinal);
            return new Failure("Generated functions disagree with pinned exports: [" + string.Join(", ", difference.Order(StringComparer.Ordinal)) + "]");
        }
        if (Check.That(Import().Count(text) == names.Length && Occurrences(text, "[DllImport(") == names.Length,
            "Unreviewed generated import attributes") is { } import)
            return import;
        if (Check.That(text.Contains(MethodContainer, StringComparison.Ordinal),
            "Expected the internal unsafe partial method container") is { } container)
            return container;
        if (ValidateTypeVisibility(text) is { } visibility)
            return visibility;
        if (Check.That(text.Contains(GeneratorIdentity, StringComparison.Ordinal),
            "Generated output does not identify the pinned generator " + BindingGenerator.ToolVersion) is { } identity)
            return identity;
        return text;
    }

    /// <summary>Nested helpers inherit their internal container's effective visibility; only top-level types are checked.</summary>
    private static Failure? ValidateTypeVisibility(string text)
    {
        if (Check.That(Occurrences(text, RawNamespace) == 1, "Expected exactly one file-scoped raw namespace") is { } single)
            return single;
        int depth = 0;
        foreach (string line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                continue;
            if (depth == 0 && PublicType().IsMatch(line))
                return new Failure("Raw generated types must remain internal");
            // Generated NativeTypeName strings can contain C syntax; braces inside
            // normal escaped strings do not participate in C# container nesting.
            string syntax = StringLiteral().Replace(line, "\"\"");
            depth += syntax.Count(character => character == '{') - syntax.Count(character => character == '}');
            if (depth < 0)
                return new Failure("Unbalanced generated containers");
        }
        return Check.That(depth == 0, "Unbalanced generated containers");
    }

    private static int Occurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [GeneratedRegex(@"\b(?:internal|public) static extern\s+[\w* ]+\s+(\w+)\([^;]*\);")]
    private static partial Regex Method();

    [GeneratedRegex(@"^\s*\[DllImport\(""moonlark_chdr"", CallingConvention = CallingConvention\.Cdecl, ExactSpelling = true\)\]$", RegexOptions.Multiline)]
    private static partial Regex Import();

    [GeneratedRegex(@"\A\s*public\s+(?:(?:static|unsafe|partial|sealed|readonly|ref|abstract)\s+)*(?:class|struct|enum|interface|record|delegate)\b")]
    private static partial Regex PublicType();

    [GeneratedRegex(@"""(?:\\.|[^""\\])*""")]
    private static partial Regex StringLiteral();
}
