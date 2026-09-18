using ErrorOr;
using Romd.Admin.Application.Common.Persistence;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy;

/// <summary>
///     Shared business logic for taxonomy entities (merge, alias management).
///     Eliminates duplication across Region/Language command handlers.
/// </summary>
public sealed class TaxonomyService<TEntity> where TEntity : class, ITaxonomyEntity
{
    private readonly ITaxonomyRepository<TEntity> _repository;
    private readonly ITaxonomyResolver<TEntity> _resolver;
    private readonly IUnitOfWork _unitOfWork;
    private readonly string _entityTypeName = typeof(TEntity).Name;

    public TaxonomyService(
        ITaxonomyRepository<TEntity> repository,
        ITaxonomyResolver<TEntity> resolver,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _resolver = resolver;
        _unitOfWork = unitOfWork;
    }

    public async Task<ErrorOr<Deleted>> MergeAsync(int sourceId, int targetId, CancellationToken ct)
    {
        if (sourceId == targetId)
            return TaxonomyErrors.MergeIntoSelf;

        var source = await _repository.GetByIdAsync(sourceId, ct);
        if (source is null)
            return TaxonomyErrors.EntityNotFound(_entityTypeName, sourceId);

        var target = await _repository.GetByIdAsync(targetId, ct);
        if (target is null)
            return TaxonomyErrors.EntityNotFound(_entityTypeName, targetId);

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        if (await _repository.HasReferenceIdentityAsync(sourceId, ct))
            return Error.Conflict("Taxonomy.ReferenceIdentity", "A registered reference identity cannot be merged into another identity.");
        await _repository.ReassignAliasesAsync(sourceId, targetId, ct);
        await _repository.ReassignJunctionsAsync(sourceId, targetId, ct);
        await _repository.DeleteAsync(sourceId, ct);
        await transaction.CommitAsync(ct);

        _resolver.InvalidateCache();

        return Result.Deleted;
    }

    public async Task<ErrorOr<Created>> AddAliasAsync(int entityId, string alias, CancellationToken ct)
    {
        var entity = await _repository.GetByIdAsync(entityId, ct);
        if (entity is null)
            return TaxonomyErrors.EntityNotFound(_entityTypeName, entityId);

        var normalized = alias.Trim().ToLowerInvariant();

        if (await _repository.AliasExistsAsync(normalized, ct))
            return TaxonomyErrors.AliasAlreadyExists(alias);

        await _repository.AddAliasAsync(entityId, normalized, ct);
        _resolver.InvalidateCache();

        return Result.Created;
    }

    public async Task<ErrorOr<Deleted>> RemoveAliasAsync(int entityId, int aliasId, CancellationToken ct)
    {
        var entity = await _repository.GetByIdAsync(entityId, ct);
        if (entity is null)
            return TaxonomyErrors.EntityNotFound(_entityTypeName, entityId);

        var aliases = await _repository.GetAliasesAsync(entityId, ct);
        if (aliases.All(a => a.Id != aliasId))
            return TaxonomyErrors.AliasNotFound(aliasId);

        await _repository.RemoveAliasAsync(aliasId, ct);
        await _unitOfWork.FlushAsync(ct);
        _resolver.InvalidateCache();

        return Result.Deleted;
    }
}
