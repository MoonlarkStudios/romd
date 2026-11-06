using ErrorOr;
using Romd.Application.Common.Validation;
using Romd.Application.Common.Pagination;
using Romd.Contracts.Common.Models;

namespace Romd.Application.Common.ReferenceCatalog;

/// <summary>A continuation over the exact stored published sequence, anchored by the last unique natural identity.</summary>
public sealed class ReferenceSnapshotPageRequest
{
    private const string Sort = "published-sequence:v1";
    private ReferenceSnapshotPageRequest(string scope, string revision, int limit, string? after)
    { Scope = scope; Revision = revision; Limit = limit; AfterKey = after; }
    public string Scope { get; }
    public string Revision { get; }
    public int Limit { get; }
    public string? AfterKey { get; }
    public static ErrorOr<ReferenceSnapshotPageRequest> Create(string scope, string revision, string? cursor = null,
        int limit = ReferencePageRequest.DefaultLimit)
    {
        if (revision is not { Length: 64 } || !revision.All(x => char.IsAsciiHexDigitLower(x)))
            return Invalid("revision", "Use the lowercase SHA-256 catalogRevision from the current descriptor.");
        if (limit is < 1 or > ReferencePageRequest.MaxLimit)
            return Invalid("limit", "Limit must be between 1 and 100.");
        Position? after = null;
        if (cursor is not null && (!CursorUtils.TryReadCanonical(cursor, 4096, out after) || after.Version != 1 ||
            after.Scope != scope || after.Revision != revision || after.Sort != Sort || after.Key is not { Length: > 0 and <= 200 } || after.Key.Any(char.IsControl)))
            return Invalid("cursor", "Use a cursor from this catalog revision and collection.");
        return new ReferenceSnapshotPageRequest(scope, revision, limit, after?.Key);
    }
    public ErrorOr<Page<T>> Read<T>(IReadOnlyList<T> rows, Func<T, string> key)
    {
        var start = 0;
        if (AfterKey is not null)
        {
            var found = false;
            for (var i = 0; i < rows.Count; i++)
                if (key(rows[i]) == AfterKey)
                { start = i + 1; found = true; break; }
            if (!found)
                return Invalid("cursor", "The continuation identity is not in this published catalog.");
        }
        var items = rows.Skip(start).Take(Limit).ToArray();
        var more = start + items.Length < rows.Count;
        return new Page<T>
        {
            Items = items,
            HasNextPage = more,
            NextCursor = more ? CursorUtils.ToCursor(new Position(1, Scope, Revision, Sort, key(items[^1]))) : null
        };
    }
    private static Error Invalid(string field, string description) => FieldErrors.Validation("Request.InvalidParameter", description, field);
    private sealed record Position(int Version, string Scope, string Revision, string Sort, string Key);
}
