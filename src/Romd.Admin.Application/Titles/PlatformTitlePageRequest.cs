using System.Text;
using ErrorOr;
using Romd.Application.Common.Pagination;

namespace Romd.Admin.Application.Titles;

/// <summary>A validated, bounded platform-title read with a scope-bound name/id continuation.</summary>
public sealed class PlatformTitlePageRequest
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;
    private const int MaxCursorLength = 8192;
    private static readonly UTF8Encoding CursorNameEncoding = new(false, true);
    private const int MaxNameCharacters = 500;
    private const string Sort = "name:asc";
    private readonly Position? _after;

    private PlatformTitlePageRequest(int platformId, int? libraryId, bool localPayloadOnly, int limit, Position? after)
    {
        PlatformId = platformId;
        LibraryId = libraryId;
        LocalPayloadOnly = localPayloadOnly;
        Limit = limit;
        _after = after;
    }

    public int PlatformId { get; }
    public int? LibraryId { get; }
    public bool LocalPayloadOnly { get; }
    public int Limit { get; }
    public string? AfterName => _after is null ? null : CursorNameEncoding.GetString(_after.NameUtf8);
    public int? AfterId => _after?.Id;

    public static ErrorOr<PlatformTitlePageRequest> Create(
        int platformId, int? libraryId, bool localPayloadOnly,
        string? cursor = null, int limit = DefaultLimit)
    {
        List<Error> errors = [];
        if (limit is < 1 or > MaxLimit)
            errors.Add(Error.Validation("PlatformTitles.InvalidLimit", $"Limit must be between 1 and {MaxLimit}."));

        Position? after = null;
        if (cursor is not null)
        {
            string? afterName = null;
            if (CursorUtils.TryReadCanonical(cursor, MaxCursorLength, out after)
                && after.NameUtf8 is { Length: <= MaxNameCharacters * 4 })
            {
                try
                { afterName = CursorNameEncoding.GetString(after.NameUtf8); }
                catch (DecoderFallbackException) { }
            }
            if (after is null || after.Version != 1 || after.PlatformId != platformId ||
                after.LibraryId != libraryId || after.LocalPayloadOnly != localPayloadOnly ||
                after.Sort != Sort || afterName is null || afterName.Contains('\0') ||
                afterName.EnumerateRunes().Count() > MaxNameCharacters || after.Id <= 0)
                errors.Add(Error.Validation("PlatformTitles.InvalidCursor", "Use a valid cursor from the same platform, library, and payload filter."));
        }

        return errors.Count > 0 ? errors : new PlatformTitlePageRequest(platformId, libraryId, localPayloadOnly, limit, after);
    }

    // UTF-8 bytes serialize as compact base64, avoiding escaped surrogate pairs in long Unicode names.
    public string CursorFor(string name, int id) =>
        CursorUtils.ToCursor(new Position(1, PlatformId, LibraryId, LocalPayloadOnly, Sort, CursorNameEncoding.GetBytes(name), id));

    private sealed record Position(int Version, int PlatformId, int? LibraryId, bool LocalPayloadOnly, string Sort, byte[] NameUtf8, int Id);
}
