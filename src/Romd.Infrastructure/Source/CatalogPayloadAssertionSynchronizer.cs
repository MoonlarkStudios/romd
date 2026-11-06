using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Persistence;

namespace Romd.Infrastructure.Source;

/// <summary>
///     Routes large relational assertion refreshes to exactly one provider per source kind.
///     Providers own their payload topology; the title projection owns only the neutral rollup.
/// </summary>
public sealed class CatalogPayloadAssertionSynchronizer : ICatalogPayloadAssertionSynchronizer
{
    private readonly RomdDbContext _context;
    private readonly IReadOnlyDictionary<CatalogSourceKind, ICatalogPayloadAssertionProvider> _providers;

    public CatalogPayloadAssertionSynchronizer(
        RomdDbContext context,
        IEnumerable<ICatalogPayloadAssertionProvider> providers)
    {
        _context = context;
        _providers = providers
            .GroupBy(provider => provider.SourceKind)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1
                    ? group.Single()
                    : throw new InvalidOperationException(
                        $"Catalog payload source kind '{group.Key}' has multiple providers."));
    }

    public async Task SynchronizeCatalogSourceAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        string? sourceKind = await _context.CatalogSources
            .AsNoTracking()
            .Where(source => source.Id == catalogSourceId)
            .Select(source => source.Kind)
            .SingleOrDefaultAsync(cancellationToken);
        if (sourceKind is null)
        {
            return;
        }

        var kind = Enum.Parse<CatalogSourceKind>(sourceKind, ignoreCase: false);
        if (!_providers.TryGetValue(kind, out var provider))
        {
            throw new InvalidOperationException(
                $"No catalog payload assertion provider owns source kind '{kind}'.");
        }

        await provider.SynchronizeCatalogSourceAsync(catalogSourceId, cancellationToken);
    }

    public async Task SynchronizePlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction();
        var kinds = await _context.SourceEntries
            .AsNoTracking()
            .Where(entry => entry.PlatformId == platformId)
            .Join(
                _context.CatalogSources.AsNoTracking(),
                entry => entry.CatalogSourceId,
                source => source.Id,
                (_, source) => source.Kind)
            .Distinct()
            .ToListAsync(cancellationToken);

        var parsedKinds = kinds
            .Select(sourceKind => Enum.Parse<CatalogSourceKind>(sourceKind, ignoreCase: false))
            .ToList();
        foreach (var kind in parsedKinds)
        {
            if (!_providers.ContainsKey(kind))
            {
                throw new InvalidOperationException(
                    $"No catalog payload assertion provider owns source kind '{kind}'.");
            }
        }

        foreach (var kind in parsedKinds)
        {
            await _providers[kind].SynchronizePlatformAsync(platformId, cancellationToken);
        }
    }

    private void RequireTransaction()
    {
        if (_context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Payload assertion synchronization requires a caller-owned transaction.");
        }
    }
}
