using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles;

/// <summary>
///     Port for curated title media rows. Every write requires the caller's transaction, rotates the
///     title revision, and leaves flush and commit to the caller. Links, layers, ratings, and metadata
///     fields are never written here.
/// </summary>
public interface ITitleMediaStore
{
    /// <summary>
    ///     Stages a provider gallery original as a non-primary media row and returns the title's revision and whether
    ///     it added a row. A new row rotates the revision. A repeat of the same content, type and source under the
    ///     current ETag is the 200 no-op: it writes nothing and returns the current revision. A retry with a stale
    ///     ETag is a 412 before this is called.
    /// </summary>
    Task<StagedMediaAppend> AppendGalleryMediaStagedAsync(TitleMedia media, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages a user upload and returns the title's revision and whether it added a media row. A new row
    ///     rotates the revision and becomes primary only when its type has no primary yet. A repeat of the same
    ///     content and type under the current ETag is the 200 no-op: it writes nothing and returns the current
    ///     revision. A retry with a stale ETag is a 412 before this is called.
    /// </summary>
    Task<StagedMediaAppend> AddUserMediaStagedAsync(TitleMedia media, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages the snapshot's primary selection for one media type: only the IsPrimary flags of
    ///     that type and the title revision change. The caller owns the flush and commit.
    /// </summary>
    Task UpdatePrimaryMediaStagedAsync(Title title, MediaType type, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Verifies and locks the snapshot revision, then stages deletion of one media row and the
    ///     snapshot's IsPrimary flags for its remaining media rows (after
    ///     <see cref="Title.RemoveMedia" /> and any primary recalculation). Other children and title
    ///     fields are untouched. The snapshot must no longer contain <paramref name="mediaId" />.
    /// </summary>
    Task RemoveMediaStagedAsync(Title title, int mediaId, CancellationToken cancellationToken = default);
}

/// <summary>The title's revision after a media append, and whether it added a row rather than finding its repeat.</summary>
public sealed record StagedMediaAppend(Guid TitleRevision, bool Added);
