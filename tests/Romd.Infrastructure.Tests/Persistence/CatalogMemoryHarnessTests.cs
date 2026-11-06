using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;
using Romd.PostgreSql.TestSupport;
using Romd.ScaleHarness;
using Romd.ScaleHarness.Scenarios;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class CatalogMemoryHarnessTests
{
    [Fact]
    public void Options_CatalogMemory_UsesCurrentFixtureWithoutEnteringDefaultScenarios()
    {
        var memory = HarnessOptions.Parse(["--scale", "100k", "--scenario", "catalog-memory",
            "--connection-string", "Host=localhost;Username=synthetic", "--results-dir", Path.GetTempPath()]);
        var preview = memory with { Scenario = "library-preview" };
        var all = memory with { Scenario = "all" };

        memory.DatasetKind.ShouldBe(preview.DatasetKind);
        memory.DatabaseName.ShouldBe(preview.DatabaseName);
        memory.ManifestPath.ShouldBe(preview.ManifestPath);
        memory.ResultsDirectory.ShouldBe(Path.GetFullPath(Path.GetTempPath()));
        all.UsesLibraryPreviewDataset.ShouldBeFalse();
        all.DatabaseName.ShouldNotBe(memory.DatabaseName);
    }

    [Fact]
    public void MaterializationOracle_EqualCountsWithWrongIdentityOrFlags_Rejects()
    {
        var data = new DeterministicDataset(new("oracle", 72, 60, 180));
        var candidates = Enumerable.Range(0, data.Scale.Titles).Where(LibraryPreviewDataset.HasCandidates).Select(t =>
        {
            int dat = (data.PlatformOfTitle(t) - 1) * 2 + 1;
            var ratings = data.TitleEnriched(t) ? new[] { new ContentRating
            {
                Board = (RatingBoard)data.RatingBoardOfTitle(t), Code = data.RatingCodeOfTitle(t),
                MinimumAge = data.RatingAgeOfTitle(t), Designation = RatingDesignation.Rated, SourceId = "igdb"
            } } : Array.Empty<ContentRating>();
            return new TitleCandidates(t + 1, data.PlatformOfTitle(t), data.TitleGenre(t), ratings,
            [
                new(t * 2 + 1, t + 1, dat, null, LibraryPreviewDataset.IsOwned(t), LibraryPreviewDataset.IsOwned(t), [dat], [1], [1]),
                new(t * 2 + 2, t + 1, dat + 1, null, false, false, [dat + 1], [1], [1])
            ], data.TitleTracked(t));
        }).ToArray();
        var graph = new MaterializationGraph(candidates,
            MaterializedLibraryProjectionBuilder.Build(1, candidates, LibraryPreviewDataset.SavedConfiguration));
        CatalogMemoryScenario.VerifyMaterialization(data, graph);

        var wrongIdentity = candidates.ToArray();
        wrongIdentity[0] = wrongIdentity[0] with { TitleId = 999 };
        Should.Throw<InvalidOperationException>(() => CatalogMemoryScenario.VerifyMaterialization(data,
            graph with { Candidates = wrongIdentity }));
        var wrongOwnership = graph.Projection.Titles.ToArray();
        wrongOwnership[0] = wrongOwnership[0] with { IsOwned = !wrongOwnership[0].IsOwned };
        Should.Throw<InvalidOperationException>(() => CatalogMemoryScenario.VerifyMaterialization(data,
            graph with { Projection = graph.Projection with { Titles = wrongOwnership } }));
    }

    [Fact]
    public async Task CurrentFixture_CompleteQueriesAndPlanning_RecordMeasuredGraphsAndRecoverFromCancellation()
    {
        await using var server = PostgreSqlTestDatabase.CreateEmpty();
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"romd-memory-harness-{Guid.NewGuid():N}");
        var scale = new ScaleParameters($"memory{Guid.NewGuid():N}", 72, 60, 180);
        var options = new HarnessOptions(scale, "catalog-memory", dataDirectory, server.ConnectionString, true, 1);
        try
        {
            var manifest = await new DatasetGenerator(options).GenerateAsync();
            await using var provider = HarnessServices.Build(options.ScaleConnectionString);
            var context = new ScenarioContext(provider, provider.GetRequiredService<SqlCaptureInterceptor>(),
                new(scale), manifest, options.ScaleConnectionString);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => new CatalogMemoryScenario(1).RunAsync(context, cancelled.Token));

            var result = await new CatalogMemoryScenario(1).RunAsync(context);

            result.Cases.Count.ShouldBe(2);
            result.Cases[0].Rows.ShouldBe(84); // 42 eligible titles and one collapsed release each.
            result.Cases[1].Rows.ShouldBe(36); // 18 owned titles and two distinct members each.
            foreach (var sample in result.Cases)
            {
                sample.SucceededIterations.ShouldBe(1, string.Join("\n", sample.Notes));
                sample.CommandCount.ShouldBeGreaterThan(0);
                sample.AllocatedBytesP50.ShouldBeGreaterThan(0);
                sample.RetainedManagedDeltasBytes.ShouldNotBeNull().Count.ShouldBe(1);
                sample.RetainedManagedHeapBytes.ShouldNotBeNull().Count.ShouldBe(1);
                sample.RetainedManagedBytesP50.ShouldBe(sample.RetainedManagedHeapBytes[0]);
                sample.RetainedManagedDeltasBytes[0].ShouldBe(sample.RetainedManagedHeapBytes[0] - sample.ManagedHeapBaselineBytes![0]);
            }
        }
        finally
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(options.ScaleConnectionString));
            await using var cleanup = new NpgsqlConnection(options.MaintenanceConnectionString);
            await cleanup.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{options.DatabaseName}\" WITH (FORCE)", cleanup);
            await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
