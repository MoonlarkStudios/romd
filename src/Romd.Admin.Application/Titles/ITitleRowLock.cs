namespace Romd.Admin.Application.Titles;

/// <summary>Port for the title-before-files lock protocol.</summary>
public interface ITitleRowLock
{
    /// <summary>
    ///     Locks the title row until the caller's transaction ends, without changing it. False when the
    ///     title is missing. Writers of title media and artwork take it before publishing files, so they
    ///     cannot deadlock on shared image bytes. ROM and DAT ingest update titles after publishing, but
    ///     their bytes never coincide with title media; PostgreSQL would abort such a deadlock anyway.
    /// </summary>
    Task<bool> LockAsync(int titleId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Locks the title row like <see cref="LockAsync" /> and returns its current revision, or null when the
    ///     title is missing. While the lock is held no other writer can rotate the revision, so a precondition
    ///     compared with it holds until the caller's transaction ends.
    /// </summary>
    Task<Guid?> LockRevisionAsync(int titleId, CancellationToken cancellationToken = default);
}
