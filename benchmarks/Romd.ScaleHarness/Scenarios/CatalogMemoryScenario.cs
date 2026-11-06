using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Export;
using Romd.Application.Common.Content;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Libraries;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Libraries;

namespace Romd.ScaleHarness.Scenarios;

public sealed record MaterializationGraph(IReadOnlyList<TitleCandidates> Candidates, MaterializedLibraryProjection Projection);
public sealed record ExportPlanningGraph(IReadOnlyList<ExportFileData> Files, object Groups, int GroupCount,
    IReadOnlyList<ExportArchiveEntry> Members, ExportArchivePlan Plan);

/// <summary>Complete production candidate/projection reads and export read/binding over the current media fixture.</summary>
public sealed class CatalogMemoryScenario(int iterations = 5) : IScenario
{
    public string Name => "catalog-memory";

    public async Task<ScenarioReport> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        var runner = new ScenarioRunner(context);
        await WarmAsync(context, ReadMaterializationAsync,
            graph => VerifyMaterialization(context.Data, (MaterializationGraph)graph.Roots), cancellationToken);
        var materialization = await runner.RunRetainedCaseAsync(Name, "complete-candidates-and-projection", iterations,
            ReadMaterializationAsync, graph => VerifyMaterialization(context.Data, (MaterializationGraph)graph.Roots), cancellationToken);

        await WarmAsync(context, ReadExportAsync,
            graph => VerifyExport(context.Data, (ExportPlanningGraph)graph.Roots), cancellationToken);
        var export = await runner.RunRetainedCaseAsync(Name, "all-catalog-export-read-and-bind", iterations,
            ReadExportAsync, graph => VerifyExport(context.Data, (ExportPlanningGraph)graph.Roots), cancellationToken);

        int candidates = Enumerable.Range(0, context.Data.Scale.Titles).Count(LibraryPreviewDataset.HasCandidates);
        int eligible = Enumerable.Range(0, context.Data.Scale.Titles).Count(t => LibraryPreviewDataset.WasEligible(context.Data, t));
        int owned = Enumerable.Range(0, context.Data.Scale.Titles).Count(LibraryPreviewDataset.IsOwned);
        const string limits = "Latency/allocation exclude independent verification and forced collections. Retained heap is the " +
            "absolute post-collection process managed heap with all result roots kept alive, including surviving EF/SQL-capture/runtime state. " +
            "Baseline and net deltas are diagnostic only: replacing earlier graphs/caches can hide allocation in a delta. " +
            "Working-set peak is sampled every 10ms and includes warm caches/prior cases; neither metric isolates object size.";
        materialization = materialization with
        {
            Notes = materialization.Notes.Concat(new[]
        {
            $"Actual titles={context.Data.Scale.Titles}; returned candidate titles={candidates}; release-source rows={candidates * 2}; " +
            $"projected titles={eligible}; projected releases={eligible}. Rows is the two output projection populations combined.",
            "Retains the complete MaterializationDataProvider result and MaterializedLibraryProjectionBuilder output together; " +
            "does not time activation writes, invalidation, codecs, or concurrent readers.", limits
        }).ToArray()
        };
        export = export with
        {
            Notes = export.Notes.Concat(new[]
        {
            $"Returned owned title groups={owned}; repository files={owned * 2}; bound original members={owned * 2}; " +
            "32 shared original identities in the standard fixture; Rows is the bound member count.",
            "Retains real all-catalog ExportRepository files, title grouping, member entries and ExportArchivePlanner.BindAsync result. " +
            "Includes real RomContentCatalog reads and production delivery policy; excludes job checkpoints/progress dictionaries, " +
            "ZIP writing/content I/O and library-scoped authorization. This fixture has low source/media fanout.", limits
        }).ToArray()
        };
        return new(Name, [materialization, export]);
    }

    private static async Task WarmAsync(ScenarioContext context,
        Func<IServiceProvider, CancellationToken, Task<RetainedGraph>> operation, Action<RetainedGraph> verify, CancellationToken ct)
    {
        await using var scope = context.Provider.CreateAsyncScope();
        var graph = await operation(scope.ServiceProvider, ct);
        verify(graph);
    }

    private static async Task<RetainedGraph> ReadMaterializationAsync(IServiceProvider services, CancellationToken ct)
    {
        var candidates = await new MaterializationDataProvider(services.GetRequiredService<RomdDbContext>())
            .GetCandidatesAsync(LibraryPreviewDataset.SavedConfiguration, ct);
        var projection = MaterializedLibraryProjectionBuilder.Build(1, candidates, LibraryPreviewDataset.SavedConfiguration);
        return new(projection.Titles.Count + projection.Releases.Count, new MaterializationGraph(candidates, projection));
    }

    private static async Task<RetainedGraph> ReadExportAsync(IServiceProvider services, CancellationToken ct)
    {
        var files = await services.GetRequiredService<IExportRepository>().GetExportFilesAsync(new ExportScope.AllCatalog(), ct);
        var groups = files.GroupBy(file => (file.PlatformName, file.TitleId, file.TitleName))
            .OrderBy(group => group.Key.PlatformName).ThenBy(group => group.Key.TitleName).ToArray();
        var members = groups.SelectMany(group => group.DistinctBy(file => file.RomFileId).Select(file =>
            new ExportArchiveEntry(file.FileId,
                $"{ExportArchivePaths.SafeFilename(group.Key.PlatformName)}/{ExportArchivePaths.SafeFilename(group.Key.TitleName)}/" +
                ExportArchivePaths.SafeFilename(file.OriginalFilename), file.RomFileId))).ToArray();
        var plan = await ExportArchivePlanner.BindAsync(members, RomDeliveryFormat.Original,
            services.GetRequiredService<IRomContentCatalog>(), RomMediaPolicyComposition.CreateDeliveryPolicy(), ct: ct);
        if (plan.IsError)
            throw new InvalidOperationException($"Export binding failed: {plan.FirstError.Code}");
        return new(plan.Value.Entries.Count, new ExportPlanningGraph(files, groups, groups.Length, members, plan.Value));
    }

    public static void VerifyMaterialization(DeterministicDataset data, MaterializationGraph graph)
    {
        var candidates = graph.Candidates.ToDictionary(title => title.TitleId);
        var titles = graph.Projection.Titles.ToDictionary(title => title.TitleId);
        var releases = graph.Projection.Releases.ToDictionary(release => release.TitleId);
        int expectedCandidates = 0, expectedProjected = 0;
        for (int t = 0; t < data.Scale.Titles; t++)
        {
            if (LibraryPreviewDataset.HasCandidates(t))
            {
                expectedCandidates++;
                if (!candidates.TryGetValue(t + 1, out var candidate) || candidate.PlatformId != data.PlatformOfTitle(t) ||
                    candidate.IsTracked != data.TitleTracked(t) || candidate.Candidates.Count != 2 ||
                    candidate.Candidates.Select(source => source.DatGameId).Distinct().Count() != 2 ||
                    candidate.Candidates.Any(source => source.CatalogReleaseId != t + 1) ||
                    candidate.Candidates.Count(source => source.HasOwnedRoms && source.IsComplete) != (LibraryPreviewDataset.IsOwned(t) ? 1 : 0))
                    throw new InvalidOperationException($"Candidate population differs at title {t + 1}.");
            }
            if (!LibraryPreviewDataset.WasEligible(data, t))
                continue;
            expectedProjected++;
            bool owned = LibraryPreviewDataset.IsOwned(t);
            if (!titles.TryGetValue(t + 1, out var title) || !releases.TryGetValue(t + 1, out var release) ||
                title.LibraryId != 1 || title.PlatformId != data.PlatformOfTitle(t) || title.IsOwned != owned ||
                title.IsPlayable != owned || title.EligibleReleaseCount != 1 || title.ExposedReleaseCount != 1 ||
                release.CatalogReleaseId != t + 1 || release.DatGameId != t * 2 + 1 ||
                release.IsOwned != owned || release.IsComplete != owned || release.IsPlayable != owned ||
                !release.IsEligible || !release.IsExposed || release.IsBlocked)
                throw new InvalidOperationException($"Projection population differs at title {t + 1}.");
        }
        if (candidates.Count != expectedCandidates || titles.Count != expectedProjected || releases.Count != expectedProjected)
            throw new InvalidOperationException("Candidate/projection populations have missing, duplicate or unexpected titles.");
    }

    public static void VerifyExport(DeterministicDataset data, ExportPlanningGraph graph)
    {
        var expectedFiles = new HashSet<(int TitleId, int RomId)>();
        var expectedEntries = new HashSet<(int RomId, string Path)>();
        int expectedTitles = 0;
        for (int t = 0; t < data.Scale.Titles; t++)
        {
            if (!LibraryPreviewDataset.IsOwned(t))
                continue;
            expectedTitles++;
            foreach (int slot in new[] { 0, 1 })
            {
                int romId = t % 16 + 1 + slot * 16;
                expectedFiles.Add((t + 1, romId));
                expectedEntries.Add((romId, $"Platform {data.PlatformOfTitle(t):D2}/{data.TitleName(t)}/part-{romId}.bin"));
            }
        }
        if (graph.GroupCount != expectedTitles || graph.Files.Count != expectedFiles.Count ||
            !expectedFiles.SetEquals(graph.Files.Select(file => (file.TitleId, file.RomFileId))) ||
            graph.Files.Any(file => file.FileId != file.RomFileId || file.PlatformId != data.PlatformOfTitle(file.TitleId - 1) ||
                file.TitleName != data.TitleName(file.TitleId - 1)) || graph.Members.Count != expectedEntries.Count ||
            graph.Plan.MissingEntries.Count != 0 || graph.Plan.Entries.Count != expectedEntries.Count ||
            !expectedEntries.SetEquals(graph.Plan.Entries.Select(entry => (entry.RomId ?? 0, entry.Path))) ||
            graph.Plan.Entries.Any(entry => entry.FileId != entry.RomId || entry.Target is not { Format: RomDeliveryFormat.Original } target ||
                target.RomId != entry.RomId))
            throw new InvalidOperationException("Export population has missing, duplicate, incorrectly named or unbound members.");
    }
}
