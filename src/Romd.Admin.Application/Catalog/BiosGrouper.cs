using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Catalog;

public sealed class BiosGrouper : IBiosGrouper
{
    private readonly IBiosRepository _biosRepository;

    public BiosGrouper(IBiosRepository biosRepository)
    {
        _biosRepository = biosRepository;
    }

    public async Task<int> GroupAsync(
        int platformId,
        IReadOnlyList<(int GameId, string Name)> biosGames,
        CancellationToken cancellationToken = default)
    {
        var normalizedByGame = biosGames
            .Select(game => (game.GameId, game.Name, Normalized: BiosNormalizer.Normalize(game.Name)))
            .Where(x => x.Normalized.Length > 0)
            .ToList();

        if (normalizedByGame.Count == 0)
        {
            return 0;
        }

        var distinctNames = normalizedByGame
            .Select(x => x.Normalized)
            .Distinct()
            .ToList();

        var existing = await _biosRepository.GetByNormalizedNamesAsync(platformId, distinctNames, cancellationToken);

        var toCreate = distinctNames
            .Where(name => !existing.ContainsKey(name))
            .Select(name =>
            {
                var displayName = normalizedByGame.First(x => x.Normalized == name).Name;
                return Bios.CreateNew(platformId, displayName, name);
            })
            .ToList();

        var created = await _biosRepository.AddRangeAsync(toCreate, cancellationToken);

        var biosIdByName = existing.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
        foreach (var bios in created)
        {
            biosIdByName[bios.NormalizedName] = bios.Id;
        }

        var mappings = normalizedByGame
            .Select(x => (x.GameId, biosIdByName[x.Normalized]))
            .ToList();

        await _biosRepository.AddGameMappingsAsync(mappings, cancellationToken);

        return created.Count;
    }
}
