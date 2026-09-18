using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Titles.Matching;
using Romd.Persistence;

namespace Romd.ScaleHarness.Scenarios;

/// <summary>
///     Scenario 1: the production match-existing path for one full 500-item derivation batch.
///     The matcher must use the platform/normalized-name index, return persisted ids in order,
///     and allocate independently of the total titles on the platform.
/// </summary>
public sealed class TitleMatcherScenario : IScenario
{
    public string Name => "title-matcher";

    public async Task<ScenarioReport> RunAsync(ScenarioContext context, CancellationToken cancellationToken = default)
    {
        var runner = new ScenarioRunner(context);
        int platformId = context.Manifest.LargestPlatformId;
        var names = Enumerable.Range(0, TitleMatchingContract.MaxBatchSize).Select(context.Data.TitleName).ToList();
        var expectedIds = Enumerable.Range(1, TitleMatchingContract.MaxBatchSize).ToList();

        var matchExisting = await runner.RunCaseAsync(Name, $"match-existing-batch-500 (platform {platformId})", 5,
            async (services, ct) =>
            {
                var matcher = services.GetRequiredService<ITitleMatcher>();
                var matches = await matcher.MatchOrCreateBatchAsync(platformId, names, ct);
                if (matches.Count != names.Count
                    || matches.Any(match => match.TitleWasCreated || match.TitleId <= 0)
                    || !matches.Select(match => match.TitleId).SequenceEqual(expectedIds))
                {
                    throw new InvalidOperationException(
                        "Bounded matcher did not return the expected existing titles in input order.");
                }

                return matches.Count;
            },
            cancellationToken);

        await using var verificationScope = context.Provider.CreateAsyncScope();
        int titleCount = await verificationScope.ServiceProvider.GetRequiredService<RomdDbContext>()
            .Titles.CountAsync(cancellationToken);
        if (titleCount != context.Data.Scale.Titles)
        {
            throw new InvalidOperationException(
                $"Match-existing scenario changed title count: expected {context.Data.Scale.Titles}, found {titleCount}.");
        }

        return new ScenarioReport(Name, [matchExisting]);
    }
}
