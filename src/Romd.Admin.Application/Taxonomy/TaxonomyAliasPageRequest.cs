using ErrorOr;
using Romd.Application.Common.Pagination;

namespace Romd.Admin.Application.Taxonomy;

/// <summary>
///     A validated, bounded read of one region's or language's aliases in ascending id order. The cursor is bound to
///     the collection and the row's key, so it continues only that row's aliases, from the first page the row's
///     representation embeds as well.
/// </summary>
public sealed class TaxonomyAliasPageRequest
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    /// <summary>The aliases a row's representation embeds; the rest continue through its alias collection.</summary>
    public const int EmbeddedLimit = 10;

    private TaxonomyAliasPageRequest(string collection, string key, int limit, int? afterId)
    {
        Collection = collection;
        Key = key;
        Limit = limit;
        AfterId = afterId;
    }

    public string Collection { get; }
    public string Key { get; }
    public int Limit { get; }
    public int? AfterId { get; }

    /// <param name="collection">The collection the row belongs to, such as <c>regions</c>.</param>
    /// <param name="key">The natural key of the row whose aliases are read.</param>
    public static ErrorOr<TaxonomyAliasPageRequest> Create(string collection, string key, string? cursor = null, int limit = DefaultLimit)
    {
        List<Error> errors = [];
        if (limit is < 1 or > MaxLimit)
            errors.Add(TaxonomyErrors.InvalidLimit(MaxLimit));
        var after = KeysetCursor.Decode<IntegerPosition>(cursor, Fingerprint(collection, key), position => position.IsValid);
        if (after.IsError)
            errors.Add(TaxonomyErrors.InvalidCursor());

        return errors.Count > 0 ? errors : new TaxonomyAliasPageRequest(collection, key, limit, after.Value?.Id);
    }

    /// <summary>The cursor that continues the row's aliases after the alias <paramref name="afterId" />.</summary>
    public static string CursorFor(string collection, string key, int afterId) =>
        KeysetCursor.Encode(new IntegerPosition(afterId), Fingerprint(collection, key));

    public string CursorFor(int afterId) => CursorFor(Collection, Key, afterId);

    private static string Fingerprint(string collection, string key) =>
        KeysetCursor.Fingerprint("admin.taxonomy.aliases", collection, key, "id:asc");
}
