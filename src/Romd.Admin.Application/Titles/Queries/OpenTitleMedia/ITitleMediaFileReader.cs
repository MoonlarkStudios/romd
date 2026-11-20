namespace Romd.Admin.Application.Titles.Queries.OpenTitleMedia;

/// <summary>
///     Narrow read port that locates the stored file behind one legacy title media row. It reads
///     metadata only; content bytes stay behind the file storage port.
/// </summary>
public interface ITitleMediaFileReader
{
    /// <summary>Returns the media's file and declared content type, or <c>null</c> when no row exists.</summary>
    Task<TitleMediaFile?> FindAsync(int mediaId, CancellationToken ct);
}

/// <summary>The stored file, declared content type, owning title and type of one title media row.</summary>
public sealed record TitleMediaFile(int FileId, string ContentType, int TitleId = 0, string Type = "Cover");
