using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Matching;
using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Catalog;

/// <summary>
///     Bounded normalized-name matcher. Its only retained state is proportional to the current
///     caller batch; later batches reuse persisted titles through the unique platform/name index.
/// </summary>
public sealed class TitleMatcher(ITitleRepository titleRepository) : ITitleMatcher
{
    public async Task<IReadOnlyList<TitleMatch>> MatchOrCreateBatchAsync(
        int platformId,
        IReadOnlyList<string> gameNames,
        CancellationToken cancellationToken = default)
    {
        if (gameNames.Count > TitleMatchingContract.MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gameNames),
                gameNames.Count,
                $"Title matching accepts at most {TitleMatchingContract.MaxBatchSize} names per batch.");
        }

        if (gameNames.Count == 0)
        {
            return [];
        }

        var normalizedNames = gameNames.Select(TitleNormalizer.Normalize).ToList();
        var distinctNames = normalizedNames.Distinct(StringComparer.Ordinal).ToList();
        var titlesByName = await titleRepository.GetByNormalizedNamesAsync(
            platformId,
            distinctNames,
            cancellationToken);

        var pending = new List<Title>();
        for (int i = 0; i < gameNames.Count; i++)
        {
            string normalizedName = normalizedNames[i];
            if (titlesByName.ContainsKey(normalizedName))
            {
                continue;
            }

            var title = Title.CreateNew(
                platformId,
                TitleNormalizer.ToDisplayName(gameNames[i]),
                normalizedName);
            titlesByName[normalizedName] = title;
            pending.Add(title);
        }

        if (pending.Count > 0)
        {
            var inserted = await titleRepository.AddRangeAsync(pending, cancellationToken);
            foreach (var title in inserted)
            {
                titlesByName[title.NormalizedName] = title;
            }
        }

        var createdNames = pending.Select(t => t.NormalizedName).ToHashSet(StringComparer.Ordinal);
        var reportedCreatedNames = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<TitleMatch>(gameNames.Count);
        foreach (string normalizedName in normalizedNames)
        {
            matches.Add(new TitleMatch(
                titlesByName[normalizedName].Id,
                createdNames.Contains(normalizedName) && reportedCreatedNames.Add(normalizedName)));
        }

        return matches;
    }
}
