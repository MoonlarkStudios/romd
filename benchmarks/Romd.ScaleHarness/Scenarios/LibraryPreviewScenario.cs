using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Libraries;
using Romd.Application.Common.Ids;
using LibraryEvaluationDto = Romd.Contracts.Management.Libraries.LibraryEvaluationDto;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Libraries;
using Romd.Persistence.Libraries;
using Romd.Persistence;
using Romd.Persistence.Repositories;

namespace Romd.ScaleHarness.Scenarios;

/// <summary>Real evaluator/provider queries over exact global counts; no preview result cache.</summary>
public sealed class LibraryPreviewScenario(int iterations = 5) : IScenario
{
    public string Name => "library-preview";
    public static IReadOnlyList<int> BatchSizes { get; } = Array.AsReadOnly(new[] { 256, 1024, 2048 });

    public async Task<ScenarioReport> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        var runner = new ScenarioRunner(context);
        var reports = new List<CaseResult>();
        foreach (int size in BatchSizes)
        {
            // Populate EF/query/JIT and PostgreSQL buffers outside measured iterations. Every
            // operation still creates a scope/transaction and computes the entire catalog.
            await using (var warm = context.Provider.CreateAsyncScope())
                await EvaluateAsync(warm.ServiceProvider, context.Data, size, "matching", null, 0, null, cancellationToken);
            await VerifyViewsAndCursorAsync(context, size, cancellationToken);
            int maximumTitles = 0, maximumCandidates = 0, batches = 0;
            var report = await runner.RunCaseAsync(Name, $"matching-first-page-batch-{size}", iterations,
                async (services, ct) =>
                {
                    var (result, observed) = await EvaluateAsync(services, context.Data, size, "matching", null, 0, null, ct, verify: false);
                    maximumTitles = Math.Max(maximumTitles, observed.MaximumTitles);
                    maximumCandidates = Math.Max(maximumCandidates, observed.MaximumCandidates);
                    batches = observed.BatchCount;
                    return result.Page.Items.Count;
                }, cancellationToken);
            reports.Add(report with
            {
                Notes = report.Notes.Concat(new[] {
                $"Actual Titles={context.Manifest.RowCounts["Titles"]}; batches/request={batches}; " +
                $"maximum loaded candidate titles={maximumTitles}; maximum release-source candidates={maximumCandidates}; " +
                "retained page rows<=49 (48 returned + next-page sentinel).",
                "All views, case-insensitive search, late afterId, cursor continuation and exact counts verified against " +
                "independent deterministic expectations; duplicate source ownership survives excluded first DAT.",
                "Allocations are whole-operation totals; sampled working set includes JIT/EF/SQL capture and prior cases, " +
                "not an isolated live-candidate heap measurement. Exact counts remain catalog-sized CPU/I/O."
            }).ToArray()
            });
        }
        return new(Name, reports);
    }

    private static async Task VerifyViewsAndCursorAsync(ScenarioContext context, int size, CancellationToken ct)
    {
        foreach (string view in new[] { "all", "matching", "added", "removed", "excluded" })
        {
            await using var scope = context.Provider.CreateAsyncScope();
            await EvaluateAsync(scope.ServiceProvider, context.Data, size, view, "sAgA", context.Data.Scale.Titles - 150, null, ct);
        }
        await using var pages = context.Provider.CreateAsyncScope();
        var first = await EvaluateAsync(pages.ServiceProvider, context.Data, size, "all", null, 0, null, ct);
        await EvaluateAsync(pages.ServiceProvider, context.Data, size, "all", null, 48, first.Result.Page.NextCursor, ct);
    }

    private static async Task<(LibraryEvaluationDto Result, ObservedCandidates Observed)> EvaluateAsync(
        IServiceProvider services, DeterministicDataset data, int size, string view, string? search,
        int afterId, string? cursor, CancellationToken ct, bool verify = true)
    {
        var db = services.GetRequiredService<RomdDbContext>();
        var observed = new ObservedCandidates(new MaterializationDataProvider(db), size, verify);
        var evaluator = new LibraryDraftEvaluator(db, services.GetRequiredService<ILibraryRepository>(),
            observed, services.GetRequiredService<ICatalogProjectionService>(), size);
        var result = await evaluator.EvaluateAsync(1, LibraryPreviewDataset.DraftConfiguration,
            view, search, afterId, ct, cursor, limit: 48);
        if (result.IsError)
            throw new InvalidOperationException($"Preview failed: {result.FirstError.Code}: {result.FirstError.Description}");
        if (verify)
            VerifyResult(data, result.Value, view, search, afterId);
        if (observed.RequestedTitles != data.Scale.Titles || observed.MaximumTitles > size)
            throw new InvalidOperationException("Preview did not load exactly one bounded scope per title.");
        if (db.ChangeTracker.Entries().Any() || db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Preview leaked tracked state or its snapshot.");
        return (result.Value, observed);
    }

    public static void VerifyResult(DeterministicDataset data, LibraryEvaluationDto result, string view, string? search, int afterId)
    {
        var titles = Enumerable.Range(0, data.Scale.Titles).ToArray();
        int saved = titles.Count(t => LibraryPreviewDataset.WasEligible(data, t));
        int matching = titles.Count(t => LibraryPreviewDataset.IsEligible(data, t));
        int added = titles.Count(t => LibraryPreviewDataset.IsEligible(data, t) && !LibraryPreviewDataset.WasEligible(data, t));
        int removed = titles.Count(t => LibraryPreviewDataset.WasEligible(data, t) && !LibraryPreviewDataset.IsEligible(data, t));
        if (result.SavedCount != saved || result.MatchingCount != matching || result.AddedCount != added || result.RemovedCount != removed ||
            result.RemovalReasons.Count != 1 || result.RemovalReasons[0].Reason != "Untracked" || result.RemovalReasons[0].Count != removed)
            throw new InvalidOperationException($"Preview counts actual={result.SavedCount}/{result.MatchingCount}/{result.AddedCount}/{result.RemovedCount}, " +
                $"expected={saved}/{matching}/{added}/{removed}; reasons={string.Join(",", result.RemovalReasons.Select(r => $"{r.Reason}:{r.Count}"))}.");
        var expected = titles.Where(t => t + 1 > afterId && LibraryPreviewDataset.MatchesView(data, t, view))
            .Where(t => search is null || data.TitleName(t).Contains(search, StringComparison.OrdinalIgnoreCase)).Take(49).ToArray();
        if (result.Page.Items.Count != Math.Min(48, expected.Length) || result.Page.HasNextPage != (expected.Length > 48) ||
            (result.Page.NextCursor is not null) != result.Page.HasNextPage)
            throw new InvalidOperationException("Preview page boundaries differ from deterministic expectations.");
        for (int i = 0; i < result.Page.Items.Count; i++)
        {
            var item = result.Page.Items[i];
            int t = expected[i];
            bool eligible = LibraryPreviewDataset.IsEligible(data, t);
            string? reason = !data.TitleTracked(t) ? "Untracked" : !LibraryPreviewDataset.HasCandidates(t) ? "NoLinkedRelease" : null;
            if (!IdCoder.TryDecode(item.Id, out int id) || id != t + 1 || item.Name != data.TitleName(t) ||
                item.PlatformName != $"Platform {data.PlatformOfTitle(t):D2}" || item.WasEligible != LibraryPreviewDataset.WasEligible(data, t) ||
                item.IsEligible != eligible || item.IsOwned != (eligible && LibraryPreviewDataset.IsOwned(t)) ||
                item.IsPlayable != (eligible && LibraryPreviewDataset.IsOwned(t)) || item.Reason != reason)
                throw new InvalidOperationException($"Preview title {t + 1} differs from deterministic eligibility/collapse/order expectations.");
        }
    }

    private sealed class ObservedCandidates(ILibraryCandidateReader inner, int size, bool verifyShape) : ILibraryCandidateReader
    {
        public int MaximumTitles { get; private set; }
        public int MaximumCandidates { get; private set; }
        public int RequestedTitles { get; private set; }
        public int BatchCount { get; private set; }
        private int lastId;

        public async Task<IReadOnlyList<TitleCandidates>> GetCandidatesAsync(LibraryConfiguration config, CancellationToken ct = default)
        {
            if (!config.IncludeUntrackedTitles || config.TitleSelectionMode != LibraryTitleSelectionMode.IncludeOnly ||
                config.IncludeTitleIds.Count is < 1 || config.IncludeTitleIds.Count > size || config.IncludeTitleIds[0] <= lastId)
                throw new InvalidOperationException("Candidate request is not a bounded title keyset scope.");
            lastId = config.IncludeTitleIds[^1];
            RequestedTitles += config.IncludeTitleIds.Count;
            BatchCount++;
            var candidates = await inner.GetCandidatesAsync(config, ct);
            MaximumTitles = Math.Max(MaximumTitles, candidates.Count);
            MaximumCandidates = Math.Max(MaximumCandidates, candidates.Sum(title => title.Candidates.Count));
            if (verifyShape)
                foreach (var title in candidates)
                    if (title.Candidates.Count != 2 || title.Candidates.Select(c => c.CatalogReleaseId).Distinct().Count() != 1 ||
                        title.Candidates[0].CatalogReleaseId != title.TitleId)
                        throw new InvalidOperationException("Preview fixture lost duplicate-source canonical release assertions.");
            return candidates;
        }
    }
}
