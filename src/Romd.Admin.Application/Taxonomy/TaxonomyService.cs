using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Taxonomy.ReadModels;
using Romd.Contracts.Management.Taxonomy;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy;

/// <summary>
///     Shared business logic for taxonomy entities (merge, alias management), addressed by natural key.
///     Eliminates duplication across Region/Language command handlers.
/// </summary>
public sealed class TaxonomyService<TEntity> where TEntity : class, ITaxonomyEntity
{
    // One run and one retry: PostgreSQL ends a deadlock by aborting one of its transactions.
    private const int MergeAttempts = 2;

    // Comfortably under the 30-second command timeout, so a merge or alias write queued behind a running ingest,
    // rebuild or merge answers a retryable 409 instead of failing the request.
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private readonly ITaxonomyRepository<TEntity> _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<TaxonomyService<TEntity>> _logger;

    public TaxonomyService(
        ITaxonomyRepository<TEntity> repository,
        IUnitOfWork unitOfWork,
        ILogger<TaxonomyService<TEntity>> logger)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<ErrorOr<Deleted>> MergeAsync(string sourceKey, string targetKey, CancellationToken ct)
    {
        var identities = await ResolveMergeAsync(sourceKey, targetKey, ct);
        if (identities.IsError)
            return identities.Errors;
        var (sourceId, targetId) = identities.Value;

        // A catalog rebuild deletes a platform's release links, then links its releases again, so a merge can wait for
        // a deleted link while the rebuild's new link waits for the merge's source. The merge runs again when it is
        // the deadlock's victim; the retry waits for the rebuild instead. A lock wait that timed out is not retried: the
        // transaction it waited for still runs.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await MergeOnceAsync(sourceId, targetId, ct);
            }
            catch (Exception error) when (_unitOfWork.ConflictOf(error) is TransactionConflict.Deadlock && attempt < MergeAttempts)
            {
                // The victim's transaction rolled back, so the next attempt starts from the committed state.
            }
            catch (Exception error) when (_unitOfWork.ConflictOf(error) is not TransactionConflict.None)
            {
                return TaxonomyErrors.MergeConflict();
            }
        }
    }

    private async Task<ErrorOr<Deleted>> MergeOnceAsync(int sourceId, int targetId, CancellationToken ct)
    {
        return await _unitOfWork.ExecuteInTransactionAsync<Deleted>(async token =>
        {
            await _repository.LimitLockWaitsAsync(LockTimeout, token);
            // Lock both rows, then re-check them: a concurrent merge may have removed either since they were resolved. The
            // source is deleted, so it waits for every transaction referencing it; the target must stay, and merges into
            // it take turns, but running ingests that reference it do not hold it up.
            var locked = await _repository.LockAsync(
                [(sourceId, TaxonomyRowLock.Update), (targetId, TaxonomyRowLock.NoKeyUpdate)], token);
            if (!locked.Contains(sourceId))
                return TaxonomyErrors.NotFound();
            if (!locked.Contains(targetId))
                return TaxonomyErrors.UnknownTarget();
            if (await _repository.HasReferenceIdentityAsync(sourceId, token))
                return TaxonomyErrors.ReferenceIdentity();
            await _repository.ReassignAliasesAsync(sourceId, targetId, token);
            await _repository.ReassignJunctionsAsync(sourceId, targetId, token);
            await _repository.DeleteAsync(sourceId, token);
            return Result.Deleted;
        }, _logger, ct);
    }

    /// <summary>The merge's effect without performing it, refused exactly where <see cref="MergeAsync" /> refuses.</summary>
    public async Task<ErrorOr<TaxonomyMergeImpactDto>> PreviewMergeAsync(string sourceKey, string targetKey, CancellationToken ct)
    {
        var identities = await ResolveMergeAsync(sourceKey, targetKey, ct);
        if (identities.IsError)
            return identities.Errors;
        if (await _repository.HasReferenceIdentityAsync(identities.Value.SourceId, ct))
            return TaxonomyErrors.ReferenceIdentity();
        return await _repository.GetMergeImpactAsync(identities.Value.SourceId, ct);
    }

    public async Task<ErrorOr<TaxonomyAlias>> AddAliasAsync(string key, string alias, CancellationToken ct)
    {
        string trimmed = alias.Trim();
        if (trimmed.Length is 0 or > TaxonomyErrors.MaxAliasLength || trimmed.Any(char.IsControl))
            return TaxonomyErrors.InvalidAlias();

        if (await _repository.GetIdByKeyAsync(key, ct) is not { } entityId)
            return TaxonomyErrors.NotFound();

        string normalized = trimmed.ToLowerInvariant();
        if (await _repository.AliasExistsAsync(normalized, ct))
            return TaxonomyErrors.AliasAlreadyExists();

        // An ingest that has inserted the same alias but not committed holds the insert on the unique index until it
        // ends, so the wait is bounded like the row lock's.
        return await BoundedAliasWriteAsync<TaxonomyAlias>(async () =>
        {
            return await _unitOfWork.ExecuteInTransactionAsync<TaxonomyAlias>(async token =>
            {
                await _repository.LimitLockWaitsAsync(LockTimeout, token);
                // Lock the row, then re-check it: a concurrent merge may have removed it.
                if (!(await _repository.LockAsync([(entityId, TaxonomyRowLock.KeyShare)], token)).Contains(entityId))
                    return TaxonomyErrors.NotFound();
                var staged = await _repository.AddAliasStagedAsync(entityId, normalized, token);
                try
                { await _unitOfWork.FlushAsync(token); }
                catch (Exception error) when (_repository.IsAliasConflict(error)) { return TaxonomyErrors.AliasAlreadyExists(); }
                var created = staged.ReadAfterFlush();
                return created;
            }, _logger, ct);
        });
    }

    public async Task<ErrorOr<Deleted>> RemoveAliasAsync(string key, int aliasId, CancellationToken ct)
    {
        if (await _repository.GetIdByKeyAsync(key, ct) is not { } entityId)
            return TaxonomyErrors.NotFound();

        return await BoundedAliasWriteAsync<Deleted>(async () =>
        {
            return await _unitOfWork.ExecuteInTransactionAsync<Deleted>(async token =>
            {
                await _repository.LimitLockWaitsAsync(LockTimeout, token);
                // Lock the row before loading the alias: a concurrent merge may have removed the row and moved its aliases.
                if (!(await _repository.LockAsync([(entityId, TaxonomyRowLock.NoKeyUpdate)], token)).Contains(entityId))
                    return TaxonomyErrors.NotFound();
                if (!await _repository.RemoveAliasAsync(entityId, aliasId, token))
                    return TaxonomyErrors.AliasNotFound();

                return Result.Deleted;
            }, _logger, ct);
        });
    }

    // A lock wait that outlasted the timeout, or a lost deadlock, rolled the write's transaction back. It is not retried
    // here: the work it waited for may still run, so the client decides when to send it again.
    private async Task<ErrorOr<T>> BoundedAliasWriteAsync<T>(Func<Task<ErrorOr<T>>> write)
    {
        try
        {
            return await write();
        }
        catch (Exception error) when (_unitOfWork.ConflictOf(error) is not TransactionConflict.None)
        {
            return TaxonomyErrors.AliasConflict();
        }
    }

    // The state-independent refusal first, then the path's source (404), then the body's target (400); callers
    // check the source's registration (409).
    private async Task<ErrorOr<(int SourceId, int TargetId)>> ResolveMergeAsync(string sourceKey, string targetKey, CancellationToken ct)
    {
        if (string.Equals(sourceKey, targetKey, StringComparison.Ordinal))
            return TaxonomyErrors.MergeIntoSelf;
        if (await _repository.GetIdByKeyAsync(sourceKey, ct) is not { } sourceId)
            return TaxonomyErrors.NotFound();
        if (string.IsNullOrWhiteSpace(targetKey) || await _repository.GetIdByKeyAsync(targetKey, ct) is not { } targetId)
            return TaxonomyErrors.UnknownTarget();
        return (sourceId, targetId);
    }
}
