using Microsoft.EntityFrameworkCore;
using Romd.Consumer.Application.Account;

namespace Romd.Persistence.Repositories;

public sealed class ConsumerTaxonomyKeys(RomdDbContext context) : IConsumerTaxonomyKeys
{
    public async Task<IReadOnlyDictionary<string, int>> RegionIdsAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
        keys.Count == 0 ? new Dictionary<string, int>() : await context.Regions.AsNoTracking()
            .Where(row => row.Ownership != null && keys.Contains(row.CanonicalKey))
            .ToDictionaryAsync(row => row.CanonicalKey, row => row.Id, StringComparer.Ordinal, ct);

    public async Task<IReadOnlyDictionary<string, int>> LanguageIdsAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
        keys.Count == 0 ? new Dictionary<string, int>() : await context.GameLanguages.AsNoTracking()
            .Where(row => row.Ownership != null && keys.Contains(row.CanonicalKey))
            .ToDictionaryAsync(row => row.CanonicalKey, row => row.Id, StringComparer.Ordinal, ct);

    public async Task<IReadOnlyDictionary<int, string>> RegionKeysAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default) =>
        ids.Count == 0 ? new Dictionary<int, string>() : await context.Regions.AsNoTracking()
            .Where(row => row.Ownership != null && ids.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, row => row.CanonicalKey, ct);

    public async Task<IReadOnlyDictionary<int, string>> LanguageKeysAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default) =>
        ids.Count == 0 ? new Dictionary<int, string>() : await context.GameLanguages.AsNoTracking()
            .Where(row => row.Ownership != null && ids.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id, row => row.CanonicalKey, ct);
}
