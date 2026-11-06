using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Matching;
using Romd.Domain.Catalog;

namespace Romd.Infrastructure.Catalog;

/// <summary>
///     Bounded normalized-name matcher. Its only retained state is proportional to the current
///     caller batch; later batches reuse persisted titles through the unique platform/name index.
/// </summary>
public sealed class TitleMatcher(ITitleCreationStore titleCreation, TimeProvider clock) : ITitleMatcher
{
    public async Task<IStagedTitleMatchBatch> MatchOrStageBatchAsync(
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
            return new StagedMatches([], new Dictionary<string, int>(StringComparer.Ordinal), [], null);
        }

        var names = gameNames.ToArray();
        var normalizedNames = names.Select(TitleNormalizer.Normalize).ToArray();
        var distinctNames = normalizedNames.Distinct(StringComparer.Ordinal).ToList();
        var existingIds = await titleCreation.FindIdsByNormalizedNamesAsync(
            platformId,
            distinctNames,
            cancellationToken);
        var idsByName = new Dictionary<string, int>(existingIds, StringComparer.Ordinal);

        var pending = new List<Title>();
        var pendingNames = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            string normalizedName = normalizedNames[i];
            if (idsByName.ContainsKey(normalizedName) || !pendingNames.Add(normalizedName))
            {
                continue;
            }

            pending.Add(Title.CreateNew(
                platformId,
                TitleNormalizer.ToDisplayName(names[i]),
                normalizedName, clock.GetUtcNow()));
        }

        var pendingNamesInOrder = pending.Select(title => title.NormalizedName).ToArray();
        var creation = pending.Count > 0 ? await titleCreation.AddBatchStagedAsync(pending, cancellationToken) : null;
        return new StagedMatches(normalizedNames, idsByName, pendingNamesInOrder, creation);
    }

    private sealed class StagedMatches(string[] normalizedNames, IReadOnlyDictionary<string, int> existingIds,
        string[] pendingNames, IStagedTitleCreationBatch? creation) : IStagedTitleMatchBatch
    {
        public bool RequiresFlush => creation?.RequiresFlush ?? false;
        public IReadOnlyList<TitleMatch> ReadAfterFlush()
        {
            var idsByName = new Dictionary<string, int>(existingIds, StringComparer.Ordinal);
            if (creation is not null)
            {
                var insertedIds = creation.ReadIdsAfterFlush();
                if (insertedIds.Count != pendingNames.Length)
                    throw new InvalidOperationException("Title batch identity count did not match staged names.");
                for (int i = 0; i < pendingNames.Length; i++)
                    idsByName.Add(pendingNames[i], insertedIds[i]);
            }
            var createdNames = pendingNames.ToHashSet(StringComparer.Ordinal);
            var reportedCreatedNames = new HashSet<string>(StringComparer.Ordinal);
            return normalizedNames.Select(name => new TitleMatch(idsByName[name],
                createdNames.Contains(name) && reportedCreatedNames.Add(name))).ToArray();
        }
    }
}
