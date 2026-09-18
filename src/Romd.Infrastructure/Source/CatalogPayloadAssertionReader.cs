using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;

namespace Romd.Infrastructure.Source;

/// <summary>
///     Composes provider assertions by source-entry identity. Multiple providers may observe an
///     entry during a migration; local payload is intentionally the logical OR of their facts.
/// </summary>
public sealed class CatalogPayloadAssertionReader(
    RomdDbContext context,
    IEnumerable<ICatalogPayloadAssertionProvider> providers) : ICatalogPayloadAssertionReader
{
    public async Task<ImmutableArray<CatalogPayloadAssertion>> ReadAsync(
        IReadOnlyCollection<int> sourceEntryIds,
        CancellationToken cancellationToken = default)
    {
        if (sourceEntryIds.Count == 0)
        {
            return ImmutableArray<CatalogPayloadAssertion>.Empty;
        }

        var requested = sourceEntryIds.Distinct().ToList();
        var providerByKind = new Dictionary<CatalogSourceKind, ICatalogPayloadAssertionProvider>();
        foreach (var provider in providers)
        {
            if (!providerByKind.TryAdd(provider.SourceKind, provider))
            {
                throw new InvalidOperationException(
                    $"Multiple payload assertion providers own catalog source kind {provider.SourceKind}.");
            }
        }

        var entries = await (
                from entry in context.SourceEntries.AsNoTracking()
                    .Where(IntegerKeyPredicate.Create<SourceEntryEntity>(requested, row => row.Id))
                join source in context.CatalogSources.AsNoTracking()
                    on entry.CatalogSourceId equals source.Id
                where requested.Contains(entry.Id)
                select new { entry.Id, source.Kind })
            .ToListAsync(cancellationToken);

        var groups = entries
            .GroupBy(entry => Enum.Parse<CatalogSourceKind>(entry.Kind))
            .ToList();
        foreach (var kindGroup in groups)
        {
            if (!providerByKind.ContainsKey(kindGroup.Key))
            {
                throw new InvalidOperationException(
                    $"No catalog payload assertion provider owns source kind '{kindGroup.Key}'.");
            }
        }

        var assertions = new Dictionary<int, CatalogPayloadAssertion>();
        foreach (var kindGroup in groups)
        {
            var provider = providerByKind[kindGroup.Key];
            var ownedIds = kindGroup.Select(entry => entry.Id).ToHashSet();
            foreach (var assertion in await provider.ReadAsync(ownedIds, cancellationToken))
            {
                if (!ownedIds.Contains(assertion.SourceEntryId))
                {
                    throw new InvalidOperationException(
                        $"{kindGroup.Key} payload assertion provider returned source entry " +
                        $"{assertion.SourceEntryId}, which it does not own.");
                }

                if (!assertions.TryAdd(assertion.SourceEntryId, assertion))
                {
                    throw new InvalidOperationException(
                        $"{kindGroup.Key} payload assertion provider returned duplicate source entry " +
                        $"{assertion.SourceEntryId}.");
                }
            }
        }

        return assertions
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value)
            .ToImmutableArray();
    }
}
