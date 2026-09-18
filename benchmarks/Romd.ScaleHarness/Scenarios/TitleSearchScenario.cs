using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Search;

namespace Romd.ScaleHarness.Scenarios;

/// <summary>
///     Scenario 2: admin title search. Compares keyset (Name sort) pages against FTS
///     relevance offset pages at increasing depth. Every page carries the production
///     correlated subqueries (totalVersions / localPayloadVersions / CoverMediaId).
/// </summary>
public sealed class TitleSearchScenario : IScenario
{
    private const int PageSize = 50;
    private static readonly int[] Depths = [0, 2000, 10000];

    public string Name => "title-search";

    public async Task<ScenarioReport> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        var runner = new ScenarioRunner(context);
        var cases = new List<CaseResult>();
        string token = context.Manifest.SearchToken;

        // Keyset browse (no query) and keyset + FTS query, both Name-sorted.
        foreach ((string label, string? query) in new[] { ("keyset-browse", (string?)null), ("keyset-fts", token) })
        {
            var cursors = await WalkKeysetCursorsAsync(
                context,
                query,
                TitleSortField.Name,
                cancellationToken);
            foreach (int depth in Depths)
            {
                if (!cursors.TryGetValue(depth, out string? cursor) && depth != 0)
                {
                    continue;
                }

                cases.Add(await runner.RunCaseAsync(Name, $"{label}-offset-{depth}", 5,
                    (services, ct) => FetchPageAsync(services, query, TitleSortField.Name, depth == 0 ? null : cursor, ct),
                    cancellationToken));
            }
        }

        // Relevance sort is a keyset over (rank, id) since #128; walk its cursors like the Name sort.
        var relevanceCursors = await WalkKeysetCursorsAsync(
            context,
            token,
            TitleSortField.Relevance,
            cancellationToken);
        foreach (int depth in Depths)
        {
            if (!relevanceCursors.TryGetValue(depth, out string? cursor) && depth != 0)
            {
                continue;
            }

            cases.Add(await runner.RunCaseAsync(Name, $"relevance-fts-offset-{depth}", 5,
                (services, ct) => FetchPageAsync(
                    services,
                    token,
                    TitleSortField.Relevance,
                    depth == 0 ? null : cursor,
                    ct),
                cancellationToken));
        }

        return new ScenarioReport(Name, cases);
    }

    private static async Task<long> FetchPageAsync(
        IServiceProvider services,
        string? query,
        TitleSortField sortField,
        string? cursor,
        CancellationToken ct)
    {
        var repository = services.GetRequiredService<ISearchRepository>();
        var page = await repository.SearchTitlesAsync(
            query, new TitleSearchFilters(), sortField, cursor, PageSize, libraryId: null, ct);
        return page.Items.Count;
    }

    /// <summary>Walks keyset pages once (untimed) to obtain real cursors for the deep-page cases.</summary>
    private static async Task<Dictionary<int, string>> WalkKeysetCursorsAsync(
        ScenarioContext context,
        string? query,
        TitleSortField sortField,
        CancellationToken cancellationToken)
    {
        var cursors = new Dictionary<int, string>();
        int maxDepth = Depths.Max();

        await using var scope = context.Provider.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISearchRepository>();

        string? cursor = null;
        for (int offset = PageSize; offset <= maxDepth; offset += PageSize)
        {
            var page = await repository.SearchTitlesAsync(
                query, new TitleSearchFilters(), sortField, cursor, PageSize, libraryId: null,
                cancellationToken);
            cursor = page.NextCursor;
            if (cursor is null)
            {
                break;
            }

            if (Depths.Contains(offset))
            {
                cursors[offset] = cursor;
            }
        }

        return cursors;
    }
}
