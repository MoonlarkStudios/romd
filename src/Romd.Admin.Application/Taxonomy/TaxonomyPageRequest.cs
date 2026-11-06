using System.Text;
using ErrorOr;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.ReferenceCatalog;

namespace Romd.Admin.Application.Taxonomy;

/// <summary>
///     A validated, bounded read of one taxonomy collection in <c>(sortOrder, name, key)</c> order, optionally narrowed
///     by a free-text query. The cursor is bound to its collection, sort and normalized query.
/// </summary>
public sealed class TaxonomyPageRequest
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;
    private const int MaxCursorLength = 8192;
    private const int MaxNameCharacters = 100;
    private const int MaxKeyLength = 64;
    private const string Sort = "sortOrder,name,key:asc";
    private static readonly UTF8Encoding CursorNameEncoding = new(false, true);
    private readonly Position? _after;

    private TaxonomyPageRequest(string collection, string query, int limit, Position? after)
    {
        Collection = collection;
        Query = query;
        Limit = limit;
        _after = after;
    }

    public string Collection { get; }

    /// <summary>The normalized search text (<see cref="ReferencePageRequest.Normalize" />); empty lists every row.</summary>
    public string Query { get; }
    public int Limit { get; }
    public int? AfterSortOrder => _after?.SortOrder;
    public string? AfterName => _after is null ? null : CursorNameEncoding.GetString(_after.NameUtf8);
    public string? AfterKey => _after?.Key;

    /// <param name="collection">The collection the cursor belongs to, such as <c>regions</c>.</param>
    /// <param name="query">Free text matched against each row's name, key (a language's code too) and every alias.</param>
    public static ErrorOr<TaxonomyPageRequest> Create(string collection, string? cursor = null, int limit = DefaultLimit,
        string? query = null)
    {
        List<Error> errors = [];
        if (limit is < 1 or > MaxLimit)
            errors.Add(TaxonomyErrors.InvalidLimit(MaxLimit));
        var normalized = ReferencePageRequest.NormalizeQuery(query);
        if (normalized.IsError)
            errors.AddRange(normalized.Errors);

        Position? after = null;
        if (cursor is not null && !normalized.IsError
            && (!CursorUtils.TryReadCanonical(cursor, MaxCursorLength, out after) || !IsValid(after, collection, normalized.Value)))
            errors.Add(TaxonomyErrors.InvalidCursor());

        return errors.Count > 0 ? errors : new TaxonomyPageRequest(collection, normalized.Value, limit, after);
    }

    // UTF-8 bytes serialize as compact base64, avoiding escaped surrogate pairs in Unicode names.
    public string CursorFor(int sortOrder, string name, string key) =>
        CursorUtils.ToCursor(new Position(2, Collection, Sort, Query, sortOrder, CursorNameEncoding.GetBytes(name), key));

    private static bool IsValid(Position after, string collection, string query)
    {
        if (after.Version != 2 || after.Collection != collection || after.Sort != Sort || after.Query != query ||
            after.NameUtf8 is not { Length: > 0 and <= MaxNameCharacters * 4 } ||
            after.Key is not { Length: > 0 and <= MaxKeyLength })
            return false;
        try
        {
            string name = CursorNameEncoding.GetString(after.NameUtf8);
            return !name.Contains('\0') && name.EnumerateRunes().Count() <= MaxNameCharacters;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private sealed record Position(int Version, string Collection, string Sort, string Query, int SortOrder, byte[] NameUtf8, string Key);
}
