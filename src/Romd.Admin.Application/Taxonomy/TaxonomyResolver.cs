using Romd.Admin.Application.Common.Persistence;
using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy;

/// <summary>
///     Generic taxonomy resolver that resolves comma-separated tokens into entity IDs for one DAT ingest, inside its
///     transaction. Auto-creates unknown tokens as new entities. Parameterized via entity factory delegate.
/// </summary>
/// <remarks>
///     The database is the only authority on what a token names. The admin and worker hosts are separate processes,
///     so nothing kept between ingests would see the other host's merges and alias edits, or an ingest that rolled
///     back. Each distinct token is read once per ingest, which locks its row as the ingest's links will, so a merge
///     cannot remove the row before the ingest ends.
/// </remarks>
public sealed class TaxonomyResolver<TEntity> : ITaxonomyResolver<TEntity>
    where TEntity : class, ITaxonomyEntity
{
    // A read that waited for a merge of the alias's row misses although the merge moved the alias; each further read
    // waits for at most one more merge of it.
    private const int AliasReads = 3;

    private readonly ITaxonomyRepository<TEntity> _repository;
    private readonly Func<string, TEntity> _entityFactory;
    private readonly IUnitOfWork _unitOfWork;
    private readonly Dictionary<string, int> _resolved = new(StringComparer.Ordinal);

    public TaxonomyResolver(ITaxonomyRepository<TEntity> repository, IUnitOfWork unitOfWork, Func<string, TEntity> entityFactory)
    {
        _repository = repository;
        _entityFactory = entityFactory;
        _unitOfWork = unitOfWork;
    }

    public async Task<IReadOnlyList<int>> ResolveAsync(string? rawInput, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return [];

        var tokens = rawInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new HashSet<int>();

        foreach (var token in tokens)
        {
            var normalized = token.ToLowerInvariant();

            if (!_resolved.TryGetValue(normalized, out var id))
            {
                id = await ReadAliasAsync(normalized, ct) ?? await AutoCreateAsync(token, normalized, ct);
                _resolved[normalized] = id;
            }

            ids.Add(id);
        }

        return ids.ToList();
    }

    private async Task<int> AutoCreateAsync(string originalToken, string normalized, CancellationToken ct)
    {
        // The minting lock waits for its previous holder's transaction to end, so once it is taken, a row another
        // ingest created for this token, in any process, is either committed or gone. Read the token again.
        await _repository.LockMintingAsync(ct);
        if (await ReadAliasAsync(normalized, ct) is { } existingId)
            return existingId;

        if (await _repository.ExecuteMintSavepointAsync(async token =>
            {
                var row = await _repository.AddStagedAsync(_entityFactory(originalToken), normalized, token);
                await _unitOfWork.FlushAsync(token);
                int id = row.ReadIdAfterFlush();
                var alias = await _repository.AddAliasStagedAsync(id, normalized, token);
                try
                { await _unitOfWork.FlushAsync(token); }
                catch (Exception error) when (_repository.IsAliasConflict(error)) { return null; }
                _ = alias.ReadAfterFlush();
                return id;
            }, ct) is { } mintedId)
            return mintedId;

        // A writer that takes no minting lock, such as an alias add, committed the alias since the read above, and the
        // refused mint left nothing behind. Use that writer's row.
        return await ReadAliasAsync(normalized, ct)
            ?? throw new InvalidOperationException("The alias another writer committed for this token was removed again.");
    }

    // The id of the row the alias names, locked, or null when no row has the alias.
    private async Task<int?> ReadAliasAsync(string normalized, CancellationToken ct)
    {
        for (int read = 1; ; read++)
        {
            if (await _repository.LockByAliasAsync(normalized, ct) is { } id)
                return id;
            if (!await _repository.AliasExistsAsync(normalized, ct))
                return null;
            if (read == AliasReads)
                throw new InvalidOperationException("Merges kept moving this token's alias while the ingest read it.");
        }
    }
}
