using System.Globalization;
using System.Text;
using ErrorOr;
using Romd.Application.Common.Validation;
using Romd.Application.Common.Pagination;

namespace Romd.Application.Common.ReferenceCatalog;

/// <summary>A bounded natural-key traversal, scoped to one host, collection, sort and normalized query.</summary>
public sealed class ReferencePageRequest
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 100;
    public const string KeySort = "key:asc";
    private ReferencePageRequest(string scope, string query, int limit, string? afterKey)
    { Scope = scope; Query = query; Limit = limit; AfterKey = afterKey; }
    public string Scope { get; }
    public string Query { get; }
    public int Limit { get; }
    public string? AfterKey { get; }
    public static ErrorOr<ReferencePageRequest> Create(string scope, string? query = null, string? cursor = null,
        int limit = DefaultLimit, string? sortBy = null)
    {
        List<Error> errors = [];
        if (limit is < 1 or > MaxLimit)
            errors.Add(Invalid("limit", "Limit must be between 1 and 100."));
        if (sortBy is not null && sortBy != KeySort)
            errors.Add(Invalid("sortBy", "The supported sort is key:asc."));
        var normalized = NormalizeQuery(query);
        if (normalized.IsError)
            errors.AddRange(normalized.Errors);
        if (errors.Count > 0)
            return errors;
        Position? after = null;
        if (cursor is not null && (!CursorUtils.TryReadCanonical(cursor, 4096, out after) || after.Version != 1 ||
            after.Scope != scope || after.Query != normalized.Value || after.Sort != KeySort || !ValidKey(after.Key)))
            return Invalid("cursor", "Use a cursor from the same collection, query and sort.");
        return new ReferencePageRequest(scope, normalized.Value, limit, after?.Key);
    }

    /// <summary>
    ///     The <see cref="Normalize" />d form of a free-text <c>query</c>, empty when none is supplied, or a refusal keyed
    ///     by <c>query</c> when it is too long, malformed or has no letter or number to search for.
    /// </summary>
    public static ErrorOr<string> NormalizeQuery(string? query)
    {
        if (query is { Length: > 200 } || query?.Any(char.IsControl) == true || !ValidUnicode(query ?? ""))
            return Invalid("query", "Use at most 200 characters without control characters.");
        var normalized = Normalize(query ?? "");
        return !string.IsNullOrEmpty(query) && normalized.Length == 0
            ? Invalid("query", "The query must contain a letter or number.")
            : normalized;
    }
    public string CursorFor(string key) => CursorUtils.ToCursor(new Position(1, Scope, Query, KeySort, key));
    public static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKD).EnumerateRunes()
        .Where(x => Rune.GetUnicodeCategory(x) is not UnicodeCategory.NonSpacingMark and not UnicodeCategory.SpacingCombiningMark and not UnicodeCategory.EnclosingMark)
        .Where(x => Rune.IsLetterOrDigit(x) || Rune.GetUnicodeCategory(x) is UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber).Select(x => x.ToString())).ToLowerInvariant();
    private static bool ValidUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (char.IsHighSurrogate(value[i]))
            { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
            else if (char.IsLowSurrogate(value[i]))
                return false;
        return true;
    }
    private static bool ValidKey(string? key) => key is { Length: > 0 and <= 64 } && key == key.Trim()
        && !key.Any(char.IsControl) && ValidUnicode(key);
    private static Error Invalid(string field, string description) => FieldErrors.Validation("Request.InvalidParameter", description, field);
    private sealed record Position(int Version, string Scope, string Query, string Sort, string Key);
}
