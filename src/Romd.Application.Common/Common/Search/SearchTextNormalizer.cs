using System.Globalization;
using System.Text;

namespace Romd.Application.Common.Search;

/// <summary>
///     Provider-neutral normalization shared by the persisted search document and user search
///     input (docs/decisions/postgresql-single-provider-cutover.md, #173). Applies Unicode
///     compatibility decomposition, folds Latin diacritics to their base letters, lowercases
///     invariantly, and maps every character that is not a letter or digit to a single separator,
///     so "Pokémon: Ｒed" and "pokemon red" normalize to the same token sequence. Non-Latin
///     combining marks (kana voicing marks, Indic vowel signs) are kept, because dropping them
///     would change words rather than fold accents. Both the write path (document) and the read
///     path (query) must use this so the provider tokenizer sees identical text.
/// </summary>
public static class SearchTextNormalizer
{
    private const int LatinExtendedBEnd = 0x024F;
    private const int LatinExtendedAdditionalStart = 0x1E00;
    private const int LatinExtendedAdditionalEnd = 0x1EFF;

    /// <summary>
    ///     Returns the normalized document: lowercase tokens separated by single spaces, or an
    ///     empty string when the input has no letters or digits.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        Span<char> encoded = stackalloc char[2];
        bool pendingSeparator = false;
        bool previousBaseIsLatin = false;

        foreach (Rune rune in decomposed.EnumerateRunes())
        {
            if (IsCombiningMark(rune))
            {
                if (!previousBaseIsLatin && builder.Length > 0 && !pendingSeparator)
                {
                    Append(builder, rune, encoded);
                }

                continue;
            }

            if (!Rune.IsLetterOrDigit(rune))
            {
                pendingSeparator = true;
                continue;
            }

            if (pendingSeparator && builder.Length > 0)
            {
                builder.Append(' ');
            }

            pendingSeparator = false;
            previousBaseIsLatin = IsLatin(rune);
            Append(builder, Rune.ToLowerInvariant(rune), encoded);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    ///     Returns the normalized document split into tokens; empty when nothing survives.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        string normalized = Normalize(text);
        return normalized.Length == 0 ? [] : normalized.Split(' ');
    }

    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    private static bool IsLatin(Rune rune) =>
        rune.Value <= LatinExtendedBEnd
        || rune.Value is >= LatinExtendedAdditionalStart and <= LatinExtendedAdditionalEnd;

    private static void Append(StringBuilder builder, Rune rune, Span<char> encoded) =>
        builder.Append(encoded[..rune.EncodeToUtf16(encoded)]);
}
