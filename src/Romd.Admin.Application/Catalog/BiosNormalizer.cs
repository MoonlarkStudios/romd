using System.Text;
using System.Text.RegularExpressions;

namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Normalizes BIOS DatGame names for grouping firmware across DAT revisions.
///     Unlike <see cref="Titles.Matching.TitleNormalizer" />, region and version tokens are
///     preserved — USA vs Japan and v1 vs v2 are distinct firmware — so only the "[BIOS]"
///     and status markers plus punctuation are stripped.
/// </summary>
public static partial class BiosNormalizer
{
    /// <summary>
    ///     Normalizes a BIOS name for grouping.
    /// </summary>
    /// <example>
    ///     "[BIOS] Sony PlayStation (USA) (v2.2)" → "sonyplaystationusav22"
    ///     "[BIOS] Sony PlayStation (Japan)" → "sonyplaystationjapan"
    /// </example>
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        // Strip bracketed markers ([BIOS], [!], [a], ...) but keep parenthetical
        // region/version tokens so distinct firmware stays distinct.
        var stripped = BracketsRegex().Replace(name, string.Empty).ToLowerInvariant();

        var sb = new StringBuilder(stripped.Length);
        foreach (var c in stripped)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\[[^\]]*\]", RegexOptions.Compiled)]
    private static partial Regex BracketsRegex();
}
