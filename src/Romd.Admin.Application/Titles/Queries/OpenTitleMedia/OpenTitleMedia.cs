using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.Queries.OpenTitleMedia;

/// <summary>
///     Opens the stored bytes of one title media row by its internal ID. Without <see cref="DocumentOfTitleId" /> it
///     serves the anonymous image lane, <c>/media/{mediaId}</c>, which never serves documents. With it, it serves only
///     a document of that title, for the authenticated document representation. Versioned artwork has its own
///     delivery port.
/// </summary>
public sealed record OpenTitleMediaQuery(int MediaId, int? DocumentOfTitleId = null) : IQuery<TitleMediaContent>;

/// <summary>
///     Handler for <see cref="OpenTitleMediaQuery" />. A missing row and a row whose bytes are
///     absent are both not-found. On success the caller owns the returned stream; nothing is opened
///     on any failure path.
/// </summary>
public sealed class OpenTitleMediaQueryHandler : IQueryHandler<OpenTitleMediaQuery, TitleMediaContent>
{
    private readonly IFileStorageService _files;
    private readonly ITitleMediaFileReader _media;

    public OpenTitleMediaQueryHandler(ITitleMediaFileReader media, IFileStorageService files)
    {
        _media = media;
        _files = files;
    }

    public async Task<ErrorOr<TitleMediaContent>> HandleAsync(
        OpenTitleMediaQuery query,
        CancellationToken ct = default)
    {
        var media = await _media.FindAsync(query.MediaId, ct);
        var isDocument = media is not null && Enum.TryParse<MediaType>(media.Type, out var type) &&
                         MediaTypes.IsDocument(type);
        if (media is null || isDocument != query.DocumentOfTitleId.HasValue ||
            (query.DocumentOfTitleId is { } titleId && media.TitleId != titleId))
        {
            return CatalogErrors.MediaNotFound();
        }

        // Last await: once the stream exists, ownership passes straight to the caller.
        var content = await _files.RetrieveByIdAsync(media.FileId, ct);
        return content is null
            ? CatalogErrors.MediaContentNotFound()
            : new TitleMediaContent(content, media.ContentType);
    }
}
