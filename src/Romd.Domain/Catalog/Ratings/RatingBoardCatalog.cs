using System.Collections.Frozen;

namespace Romd.Domain.Catalog.Ratings;

/// <summary>
///     A row of canonical board knowledge: what a board's category means.
/// </summary>
public sealed record ContentRatingCategory(
    RatingBoard Board,
    string Code,
    RatingDesignation Designation,
    int? MinimumAge);

/// <summary>
///     The single source of truth for rating board knowledge: maps (board, raw code) to the
///     canonical category, designation, and minimum age. A closed table — unrecognized input
///     resolves to null, never a guess. The board is always explicit, so identical tokens on
///     different boards ("M" under ESRB vs ACB) are different facts.
///     MinimumAge semantics: youngest age the board deems the content suitable for,
///     conservative reading. ACB PG/M are advisory (unrestricted) categories; assigning 8/15
///     is a deliberate conservative interpretation recorded here.
/// </summary>
public static partial class RatingBoardCatalog
{
    private static readonly FrozenDictionary<(RatingBoard Board, string Alias), ContentRatingCategory> Categories =
        BuildCatalog();

    /// <summary>
    ///     Canonical categories grouped by board, ordered youngest-first with pending/refused
    ///     designations last. Drives reference surfaces (e.g. the admin re-rate picker) so the
    ///     valid codes per board come from this single source of truth, never a UI copy.
    /// </summary>
    private static readonly FrozenDictionary<RatingBoard, IReadOnlyList<ContentRatingCategory>> CanonicalByBoard =
        Categories.Values
            .Distinct()
            .GroupBy(category => category.Board)
            .ToFrozenDictionary(
                group => group.Key,
                group => (IReadOnlyList<ContentRatingCategory>)group
                    .OrderBy(category => category.MinimumAge ?? int.MaxValue)
                    .ThenBy(category => category.Code, StringComparer.Ordinal)
                    .ToList());

    /// <summary>
    ///     The canonical categories a board can assign, ordered for display. Empty for an
    ///     unrecognized board.
    /// </summary>
    public static IReadOnlyList<ContentRatingCategory> GetCategories(RatingBoard board) =>
        CanonicalByBoard.GetValueOrDefault(board, []);

    private static readonly FrozenDictionary<string, string> NumberWords =
        new Dictionary<string, string>
        {
            ["ZERO"] = "0",
            ["THREE"] = "3",
            ["SIX"] = "6",
            ["SEVEN"] = "7",
            ["TEN"] = "10",
            ["TWELVE"] = "12",
            ["FOURTEEN"] = "14",
            ["FIFTEEN"] = "15",
            ["SIXTEEN"] = "16",
            ["EIGHTEEN"] = "18"
        }.ToFrozenDictionary();

    /// <summary>
    ///     Resolves a raw code under an explicit board to its canonical category.
    ///     Returns null for unrecognized input — never a guess.
    /// </summary>
    public static ContentRatingCategory? TryResolve(RatingBoard board, string rawCode)
    {
        var normalized = Normalize(board, rawCode);
        if (normalized is null)
            return null;

        return Categories.TryGetValue((board, normalized), out var category) ? category : null;
    }

    /// <summary>
    ///     Resolves a claim into an effective <see cref="ContentRating" /> carrying the
    ///     claim's descriptors, synopsis, and provenance. Returns null when the claim's
    ///     code is not recognized for its board.
    /// </summary>
    public static ContentRating? TryResolve(ContentRatingClaim claim, string sourceId)
    {
        var category = TryResolve(claim.Board, claim.RawCode);
        if (category is null)
            return null;

        return new ContentRating
        {
            Board = category.Board,
            Code = category.Code,
            Designation = category.Designation,
            MinimumAge = category.MinimumAge,
            Descriptors = claim.Descriptors,
            Synopsis = claim.Synopsis,
            SourceId = sourceId,
            ExternalRatingId = claim.ExternalRatingId
        };
    }

    /// <summary>
    ///     Normalizes a raw code for lookup: uppercase, unify separators, "PLUS" → "+",
    ///     strip the board's own name prefix, and spell number words as digits.
    /// </summary>
    private static string? Normalize(RatingBoard board, string rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
            return null;

        var text = rawCode
            .Trim()
            .ToUpperInvariant()
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Replace("PLUS", "+");

        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (BoardPrefixes.TryGetValue(board, out var prefixes))
        {
            foreach (var prefix in prefixes)
            {
                if (text.Equals(prefix, StringComparison.Ordinal))
                    return null;

                if (text.StartsWith(prefix + " ", StringComparison.Ordinal))
                {
                    text = text[(prefix.Length + 1)..];
                    break;
                }
            }
        }

        return NumberWords.TryGetValue(text, out var digits) ? digits : text;
    }

    private static FrozenDictionary<(RatingBoard, string), ContentRatingCategory> BuildCatalog()
    {
        var entries = new Dictionary<(RatingBoard, string), ContentRatingCategory>();

        void Add(RatingBoard board, string code, RatingDesignation designation, int? minimumAge,
            params string[] aliases)
        {
            if (designation == RatingDesignation.Rated != minimumAge.HasValue)
                throw new InvalidOperationException(
                    $"Catalog invariant violated for {board} {code}: MinimumAge must be set iff Rated.");

            var category = new ContentRatingCategory(board, code, designation, minimumAge);
            foreach (var alias in aliases)
            {
                entries.Add((board, alias), category);
            }
        }

        AddDefinitions(Add);

        return entries.ToFrozenDictionary();
    }
}
