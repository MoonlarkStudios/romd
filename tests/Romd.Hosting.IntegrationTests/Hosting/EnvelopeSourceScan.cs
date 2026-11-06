using System.Text;
using System.Text.RegularExpressions;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Finds endpoint code that builds an error body outside the shared envelope helpers
///     (docs/decisions/api-contract-policy.md, "One Error Envelope"). Comments and string
///     literals are blanked first, preserving line numbers, so prose and messages never match.
///     Each violation names its form, so an allowlist can pin a file's exact mix of forms.
/// </summary>
internal static class EnvelopeSourceScan
{
    /// <summary>Route prefixes whose surfaces keep their own protocol semantics.</summary>
    public static readonly string[] ProtocolRoutePrefixes =
        ["/media/{mediaId}", "/artwork", "/delivery", "/connect", "/vfs", "/health"];

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    /// <summary>Error result factories, called or passed as a method group.</summary>
    private static readonly Regex ErrorResultFactory = new(
        @"\b(?<receiver>(?:Typed)?Results)\s*\.\s*(?<name>NotFound|BadRequest|Conflict|Unauthorized|Forbid|Challenge|UnprocessableEntity|InternalServerError|Problem|ValidationProblem)\b",
        Options);

    private static readonly Regex StatusCodeResult = new(
        @"\b(?<receiver>(?:Typed)?Results)\s*\.\s*StatusCode\s*\(\s*(?<value>[^()]*?)\s*\)",
        Options);

    /// <summary>Body factories whose fourth positional argument, or a named one, sets the status.</summary>
    private static readonly Regex BodyResultFactory = new(
        @"\b(?<receiver>(?:Typed)?Results)\s*\.\s*(?<name>Json|Text|Content)\b",
        Options);

    private static readonly Regex StatusCodeArgument = new(@"^\s*statusCode\s*:\s*(?<value>.+)$", Options | RegexOptions.Singleline);

    private static readonly Regex StatusCodeAssignment = new(
        @"\.\s*StatusCode\s*=(?!=)\s*(?<value>[^;]+);",
        Options | RegexOptions.IgnoreCase);

    private static readonly Regex ProblemDetailsConstruction = new(
        @"\bnew\s+(?<type>(?:Http)?(?:Validation)?ProblemDetails)\b",
        Options);

    private static readonly Regex BadRequestThrow = new(@"\bthrow\s+new\s+BadHttpRequestException\b", Options);

    private static readonly Regex StaticResultsImport = new(
        @"\busing\s+static\s+[\w.]*\b(?:Typed)?Results\s*;",
        Options);

    private static readonly Regex ResultsAlias = new(
        @"\busing\s+\w+\s*=\s*[\w.]*\b(?:Typed)?Results\s*;",
        Options);

    private static readonly Regex StatusValue = new(
        @"^(?:(?<number>\d{3})|StatusCodes\s*\.\s*Status(?<number>\d{3})\w*)$",
        Options);

    /// <summary>Every <c>Map*("…")</c> route literal, read from comment-free source.</summary>
    private static readonly Regex MapRouteLiteral = new(
        @"\bMap[A-Za-z]*\s*(?:<[^<>()]*>)?\s*\(\s*""(?<route>[^""]*)""",
        Options);

    public sealed record Violation(int Line, string Form, string Text);

    public static IReadOnlyList<Violation> FindViolations(string source)
    {
        string code = Blank(source, keepStrings: false);
        var found = new List<(int Offset, string Form)>();

        found.AddRange(ErrorResultFactory.Matches(code)
            .Select(match => (match.Index, $"{match.Groups["receiver"].Value}.{match.Groups["name"].Value}")));
        found.AddRange(StatusCodeResult.Matches(code)
            .Where(match => !IsNonErrorStatus(match.Groups["value"].Value))
            .Select(match => (match.Index, $"{match.Groups["receiver"].Value}.StatusCode")));
        found.AddRange(BodyResultFactory.Matches(code)
            .Where(match => SetsErrorStatus(code, match.Index + match.Length))
            .Select(match => (match.Index, $"{match.Groups["receiver"].Value}.{match.Groups["name"].Value} statusCode")));
        found.AddRange(StatusCodeAssignment.Matches(code)
            .Where(match => !IsNonErrorStatus(match.Groups["value"].Value))
            .Select(match => (match.Index, ".StatusCode =")));
        found.AddRange(ProblemDetailsConstruction.Matches(code)
            .Select(match => (match.Index, $"new {match.Groups["type"].Value}")));
        found.AddRange(BadRequestThrow.Matches(code).Select(match => (match.Index, "throw BadHttpRequestException")));
        found.AddRange(StaticResultsImport.Matches(code).Select(match => (match.Index, "using static Results")));
        found.AddRange(ResultsAlias.Matches(code).Select(match => (match.Index, "using Results alias")));

        string[] lines = source.Split('\n');
        return found
            .OrderBy(violation => violation.Offset)
            .Select(violation => (Line: LineOf(code, violation.Offset), violation.Form))
            .Select(violation => new Violation(violation.Line, violation.Form, lines[violation.Line - 1].Trim()))
            .ToArray();
    }

    /// <summary>The <c>Map*("…")</c> route literals in <paramref name="source" />, comments excluded.</summary>
    public static IReadOnlyList<string> MapRouteLiterals(string source) =>
        MapRouteLiteral.Matches(Blank(source, keepStrings: true))
            .Select(match => match.Groups["route"].Value)
            .ToArray();

    /// <summary>
    ///     A file is a protocol surface only when it maps routes and every route literal it maps
    ///     starts with a protocol prefix.
    /// </summary>
    public static bool IsProtocolSurface(string source)
    {
        var routes = MapRouteLiterals(source);
        return routes.Count > 0 && routes.All(route => ProtocolRoutePrefixes.Any(prefix =>
            route == prefix || route.StartsWith(prefix + "/", StringComparison.Ordinal)));
    }

    private static bool IsNonErrorStatus(string value)
    {
        var match = StatusValue.Match(value.Trim());
        return match.Success && int.Parse(match.Groups["number"].Value) is >= 100 and < 400;
    }

    /// <summary>
    ///     Whether the call that follows <paramref name="afterName" /> (past any nested type
    ///     arguments) sets a status other than a success, redirect or not-modified one, by name or
    ///     as the fourth positional argument. A method group sets none.
    /// </summary>
    private static bool SetsErrorStatus(string code, int afterName)
    {
        int index = SkipTypeArguments(code, SkipWhitespace(code, afterName));
        if (index >= code.Length || code[index] != '(')
            return false;

        var arguments = TopLevelArguments(code, index);
        string? status = arguments
            .Select(argument => StatusCodeArgument.Match(argument))
            .Where(match => match.Success)
            .Select(match => match.Groups["value"].Value)
            .FirstOrDefault()
            ?? (arguments.Count >= 4 && !arguments[3].Contains(':') ? arguments[3] : null);
        return status is not null && !IsNonErrorStatus(status);
    }

    private static int SkipWhitespace(string code, int index)
    {
        while (index < code.Length && char.IsWhiteSpace(code[index]))
            index++;
        return index;
    }

    private static int SkipTypeArguments(string code, int index)
    {
        if (index >= code.Length || code[index] != '<')
            return index;

        int depth = 0;
        for (; index < code.Length; index++)
        {
            depth += code[index] switch { '<' => 1, '>' => -1, _ => 0 };
            if (depth == 0)
                return SkipWhitespace(code, index + 1);
        }

        return index;
    }

    /// <summary>The call's arguments split on top-level commas; type argument lists stay whole.</summary>
    private static IReadOnlyList<string> TopLevelArguments(string code, int openParen)
    {
        var arguments = new List<string>();
        int depth = 0;
        int angle = 0;
        int start = openParen + 1;
        for (int index = openParen; index < code.Length; index++)
        {
            char current = code[index];
            if (current is '(' or '[' or '{')
                depth++;
            else if (current is ')' or ']' or '}')
                depth--;
            else if (current == '<' && index > 0 && (char.IsLetterOrDigit(code[index - 1]) || code[index - 1] == '_'))
                angle++;
            else if (current == '>' && angle > 0)
                angle--;
            else if (current == ',' && depth == 1 && angle == 0)
            {
                arguments.Add(code[start..index]);
                start = index + 1;
            }

            if (depth == 0)
            {
                string last = code[start..index];
                if (!string.IsNullOrWhiteSpace(last))
                    arguments.Add(last);
                return arguments;
            }
        }

        return arguments;
    }

    private static int LineOf(string code, int offset) =>
        code.AsSpan(0, offset).Count('\n') + 1;

    /// <summary>
    ///     Replaces comments (and, unless <paramref name="keepStrings" />, string and character
    ///     literals) with spaces, keeping newlines so offsets map to the original lines.
    /// </summary>
    public static string Blank(string source, bool keepStrings)
    {
        var output = new StringBuilder(source.Length);
        int index = 0;
        while (index < source.Length)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (current == '/' && next == '/')
            {
                int end = source.IndexOf('\n', index);
                end = end < 0 ? source.Length : end;
                AppendBlank(output, source, index, end);
                index = end;
            }
            else if (current == '/' && next == '*')
            {
                int end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                AppendBlank(output, source, index, end);
                index = end;
            }
            else if (current is '"' or '\'' or '@' or '$' && StringLength(source, index) is > 0 and var length)
            {
                if (keepStrings)
                    output.Append(source, index, length);
                else
                    AppendBlank(output, source, index, index + length);
                index += length;
            }
            else
            {
                output.Append(current);
                index++;
            }
        }

        return output.ToString();
    }

    private static void AppendBlank(StringBuilder output, string source, int start, int end)
    {
        for (int index = start; index < end; index++)
        {
            output.Append(source[index] == '\n' ? '\n' : ' ');
        }
    }

    /// <summary>
    ///     The length of the string or character literal starting at <paramref name="start" />,
    ///     or 0 when none starts there. Handles regular, verbatim, interpolated (including holes
    ///     that contain literals) and raw literals.
    /// </summary>
    private static int StringLength(string source, int start)
    {
        int index = start;
        bool interpolated = false;
        bool verbatim = false;
        while (index < source.Length && source[index] is '$' or '@')
        {
            interpolated |= source[index] == '$';
            verbatim |= source[index] == '@';
            index++;
        }

        if (index >= source.Length)
            return 0;
        if (source[index] == '\'' && index == start)
            return CharLiteralLength(source, start);
        if (source[index] != '"')
            return 0;

        int quotes = 0;
        while (index + quotes < source.Length && source[index + quotes] == '"')
            quotes++;

        if (quotes >= 3)
        {
            int close = source.IndexOf(new string('"', quotes), index + quotes, StringComparison.Ordinal);
            return close < 0 ? source.Length - start : close + quotes - start;
        }

        index++;
        while (index < source.Length)
        {
            char current = source[index];
            if (!verbatim && current == '\\')
            {
                index += 2;
            }
            else if (current == '"')
            {
                if (verbatim && index + 1 < source.Length && source[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                return index + 1 - start;
            }
            else if (interpolated && current == '{')
            {
                if (index + 1 < source.Length && source[index + 1] == '{')
                {
                    index += 2;
                    continue;
                }

                index = InterpolationHoleEnd(source, index + 1);
            }
            else
            {
                index++;
            }
        }

        return source.Length - start;
    }

    private static int InterpolationHoleEnd(string source, int index)
    {
        int depth = 0;
        while (index < source.Length)
        {
            char current = source[index];
            if (current is '"' or '\'' or '@' or '$' && StringLength(source, index) is > 0 and var length)
            {
                index += length;
                continue;
            }

            if (current == '{')
                depth++;
            if (current == '}' && depth-- == 0)
                return index + 1;
            index++;
        }

        return index;
    }

    private static int CharLiteralLength(string source, int start)
    {
        int index = start + 1;
        while (index < source.Length && source[index] != '\'')
        {
            index += source[index] == '\\' ? 2 : 1;
        }

        return Math.Min(index + 1, source.Length) - start;
    }
}
