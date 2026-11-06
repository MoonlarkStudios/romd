using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Libraries;

namespace Romd.ScaleHarness.Scenarios;

/// <summary>
///     Scenario 6 (write side): <c>LibraryRepository.StageMaterializedProjectionsAndActivateAsync</c>
///     — the load-all <c>AsTracking</c> materialization swap. The projection is regenerated from
///     the same deterministic functions the generator used, so this measures the steady-state
///     rematerialization (load-all + diff, near-zero row churn), which is the recurring shape.
/// </summary>
public sealed class MaterializationScenario : IScenario
{
    private const int LibraryId = 1;

    public string Name => "materialization";

    public async Task<ScenarioReport> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        var runner = new ScenarioRunner(context);
        var projection = context.Data.BuildLibraryProjection(LibraryId);

        var replace = await runner.RunCaseAsync(Name, "try-replace-materialized-projections", 3,
            async (services, ct) =>
            {
                var repository = services.GetRequiredService<ILibraryRepository>();
                var library = await repository.GetByIdAsync(LibraryId, ct)
                              ?? throw new InvalidOperationException($"Library {LibraryId} missing from dataset.");

                await using var transaction = await services.GetRequiredService<IUnitOfWork>().BeginTransactionAsync(ct);
                using var publication = await repository.StageMaterializedProjectionsAndActivateAsync(
                    LibraryId, projection, projection.Titles.Count, library.MaterializationRevision, ct);
                if (!publication.Applied)
                {
                    throw new InvalidOperationException("Materialization token was stale; activation was skipped.");
                }
                await transaction.CommitAsync(ct);

                return projection.Titles.Count + projection.Releases.Count;
            },
            cancellationToken);

        return new ScenarioReport(Name, [replace]);
    }
}
