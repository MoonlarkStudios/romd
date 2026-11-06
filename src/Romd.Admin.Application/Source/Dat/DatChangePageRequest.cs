using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErrorOr;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;

namespace Romd.Admin.Application.Source.Dat;

/// <summary>
///     A validated keyset page of a DAT review's changes. Entries page by ordinal entry name and an
///     entry's files by ordinal <c>kind:name</c> key; both orders are total because names are unique
///     within a document. The opaque cursor binds the sort and a fingerprint of the reviewed document
///     pair and its filters, so a cursor presented with any other pair or filter set is invalid.
///     Validation errors name their input through <see cref="FieldErrors" />.
/// </summary>
public sealed class DatChangePageRequest
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 50;
    public const int MaxQueryLength = 200;

    public const string ChangePattern = "^(?:Added|Removed|Changed)$";
    private const int MaxCursorLength = 8192;
    private const string EntrySort = "name:asc";
    private const string FileSort = "kind-name:asc";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly string[] ChangeKinds = ["Added", "Removed", "Changed"];

    private readonly string _sort;
    private readonly byte[] _scope;

    private DatChangePageRequest(string activeSha256, string candidateSha256, string? change, string? query,
        int limit, string sort, byte[] scope, string? after)
    {
        ActiveSha256 = activeSha256;
        CandidateSha256 = candidateSha256;
        Change = change;
        Query = query;
        Limit = limit;
        _sort = sort;
        _scope = scope;
        After = after;
    }

    /// <summary>The reviewed active document hash; empty when no document is installed yet.</summary>
    public string ActiveSha256 { get; }
    public string CandidateSha256 { get; }
    public string? Change { get; }
    public string? Query { get; }
    public int Limit { get; }

    /// <summary>The ordinal key the page starts after, or <c>null</c> for the first page.</summary>
    public string? After { get; }

    /// <summary>A page of changed entries, filtered by change kind and a case-insensitive name query.</summary>
    public static ErrorOr<DatChangePageRequest> ForEntries(string? activeSha256, string? candidateSha256,
        string? change, string? query, string? cursor, int? limit)
    {
        List<Error> errors = [];
        if (change is not null && !ChangeKinds.Contains(change, StringComparer.Ordinal))
            errors.Add(Invalid("DatReview.InvalidChange", "change", "Use Added, Removed or Changed, or omit the change filter."));
        if (query?.Length > MaxQueryLength)
            errors.Add(Invalid("DatReview.InvalidQuery", "query", $"The query must be at most {MaxQueryLength} characters."));
        string? trimmed = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        return Create(activeSha256, candidateSha256, change, trimmed, entryName: "", EntrySort, cursor, limit, errors);
    }

    /// <summary>A page of one entry's changed files; file pages take no filters.</summary>
    public static ErrorOr<DatChangePageRequest> ForFiles(string? activeSha256, string? candidateSha256,
        string entryName, string? cursor, int? limit) =>
        Create(activeSha256, candidateSha256, change: null, query: null, entryName, FileSort, cursor, limit, []);

    public bool Matches(string name, string change) =>
        (Change is null || Change == change) && (Query is null || name.Contains(Query, StringComparison.OrdinalIgnoreCase));

    /// <summary>Takes the page from an ordered sequence, reading one extra item to decide whether another page follows.</summary>
    public PagedList<T> Page<T>(IEnumerable<T> ordered, Func<T, string> key)
    {
        var items = ordered
            .SkipWhile(item => After is not null && string.CompareOrdinal(key(item), After) <= 0)
            .Take(Limit + 1)
            .ToList();
        bool hasNextPage = items.Count > Limit;
        if (hasNextPage)
            items.RemoveAt(Limit);
        return new PagedList<T>(items, hasNextPage ? CursorFor(key(items[^1])) : null, hasNextPage);
    }

    private string CursorFor(string key) => CursorUtils.ToCursor(new Position(1, _sort, StrictUtf8.GetBytes(key), _scope));

    private static ErrorOr<DatChangePageRequest> Create(string? activeSha256, string? candidateSha256, string? change,
        string? query, string entryName, string sort, string? cursor, int? limit, List<Error> errors)
    {
        errors.AddRange(DatReviewPins.Failures(activeSha256, candidateSha256)
            .Select(failure => Invalid("DatReview.InvalidFingerprint", failure.Field, failure.Message)));
        if (limit is < 1 or > MaxLimit)
            errors.Add(Invalid("DatReview.InvalidLimit", "limit", $"Limit must be between 1 and {MaxLimit}."));
        if (errors.Count > 0)
            return errors;

        byte[] scope = Scope(activeSha256!, candidateSha256!, change, query, entryName);
        string? after = null;
        if (cursor is not null && !TryReadCursor(cursor, sort, scope, out after))
            return Invalid("DatReview.InvalidCursor", "cursor", "Use a cursor from the same review, filters and page.");
        return new DatChangePageRequest(activeSha256!, candidateSha256!, change, query, limit ?? DefaultLimit, sort, scope, after);
    }

    private static bool TryReadCursor(string cursor, string sort, byte[] scope, out string? after)
    {
        after = null;
        if (!CursorUtils.TryReadCanonical<Position>(cursor, MaxCursorLength, out var position)
            || position is not { Version: 1, AfterUtf8.Length: > 0 } || position.Sort != sort
            || position.Scope is null || !CryptographicOperations.FixedTimeEquals(position.Scope, scope))
            return false;
        try
        {
            after = StrictUtf8.GetString(position.AfterUtf8);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    // A JSON array is an unambiguous canonical encoding of the bound tuple.
    private static byte[] Scope(string activeSha256, string candidateSha256, string? change, string? query, string entryName) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { activeSha256, candidateSha256, change ?? "", query ?? "", entryName }));

    private static Error Invalid(string code, string field, string description) =>
        FieldErrors.Validation(code, description, field);

    private sealed record Position(int Version, string Sort, byte[] AfterUtf8, byte[] Scope);
}
