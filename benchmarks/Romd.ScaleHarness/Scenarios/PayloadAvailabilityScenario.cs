using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Rom;
using Romd.Domain.Catalog;
using Romd.Persistence.Queries;
using Romd.Persistence;

namespace Romd.ScaleHarness.Scenarios;

/// <summary>
///     Canonical title-availability read and repair workload for issue #130. The fixture owns
///     one hundred thousand or one million actual Title rows; no extrapolation is used.
/// </summary>
public sealed class PayloadAvailabilityScenario : IScenario
{
    public string Name => "payload-availability";

    public async Task<ScenarioReport> RunAsync(
        ScenarioContext context,
        CancellationToken cancellationToken = default)
    {
        var runner = new ScenarioRunner(context);
        // A prior cancellation may have committed the measured delete before its cleanup ran.
        // Recover that durable backup before any case observes or replaces the fixture.
        await RecoverLinkBackupIfPresentAsync(context, CancellationToken.None);
        long expectedPayloadEntries = Enumerable.Range(0, context.Data.Scale.DatGames)
            .LongCount(context.Data.GameHasLocalPayload);
        long expectedPayloadTitles = Enumerable.Range(0, context.Data.Scale.Titles)
            .LongCount(context.Data.TitleHasLocalPayload);

        var platformBreakdown = await runner.RunCaseAsync(
            Name,
            "tracked-local-payload-platform-breakdown",
            5,
            async (services, ct) =>
            {
                var breakdown = await services.GetRequiredService<IRomRepository>()
                    .GetPlatformBreakdownAsync(ct);
                return breakdown.Sum(row => row.LocalPayloadCount);
            },
            cancellationToken);

        var sourceLifecycle = await runner.RunCaseAsync(
            Name,
            "source-disable-reactivate",
            5,
            async (services, ct) =>
            {
                var db = services.GetRequiredService<RomdDbContext>();
                const int catalogSourceId = 1;
                int titleCount = await (
                    from entry in db.SourceEntries.AsNoTracking()
                    join link in db.TitleSourceLinks.AsNoTracking()
                        on entry.Id equals link.SourceEntryId
                    where entry.CatalogSourceId == catalogSourceId
                    orderby link.TitleId
                    select link.TitleId)
                    .Distinct()
                    .CountAsync(ct);
                var unitOfWork = services.GetRequiredService<IUnitOfWork>();
                await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
                var availability = services.GetRequiredService<ITitlePayloadAvailabilityProjection>();
                await db.CatalogSources
                    .Where(source => source.Id == catalogSourceId)
                    .ExecuteUpdateAsync(update => update.SetProperty(
                        source => source.Status,
                        nameof(CatalogSourceStatus.Disabled)), ct);
                await availability.RollupCatalogSourceAsync(catalogSourceId, ct);
                await db.CatalogSources
                    .Where(source => source.Id == catalogSourceId)
                    .ExecuteUpdateAsync(update => update.SetProperty(
                        source => source.Status,
                        nameof(CatalogSourceStatus.Active)), ct);
                await availability.RollupCatalogSourceAsync(catalogSourceId, ct);
                await transaction.CommitAsync(ct);
                return titleCount;
            },
            cancellationToken);

        // Fault injection is preparation, not part of recovery latency or WAL evidence. Both
        // normalized layers are corrupted before the timer starts; the measured operation below
        // is exactly the production Dirty-recovery assertion refresh plus title rollup.
        await using (var corruptionScope = context.Provider.CreateAsyncScope())
        {
            var db = corruptionScope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var unitOfWork = corruptionScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await db.SourceEntries.ExecuteUpdateAsync(
                update => update.SetProperty(entry => entry.HasLocalPayload, false), cancellationToken);
            await db.Titles.ExecuteUpdateAsync(
                update => update.SetProperty(title => title.HasLocalPayload, false), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        await AnalyzeTouchedTablesAsync(runner, cancellationToken);

        var fullRepair = await runner.RunCaseAsync(
            Name,
            $"full-catalog-repair-{context.Data.Scale.Titles:N0}-titles",
            1,
            async (services, ct) =>
            {
                var db = services.GetRequiredService<RomdDbContext>();
                var unitOfWork = services.GetRequiredService<IUnitOfWork>();
                var availability = services.GetRequiredService<ITitlePayloadAvailabilityProjection>();
                for (int currentPlatformId = 1;
                     currentPlatformId <= ScaleParameters.PlatformCount;
                     currentPlatformId++)
                {
                    // Production recovery owns one transaction per platform. Keeping that
                    // boundary here makes both rollback and WAL high-water evidence honest:
                    // a failed platform cannot undo an earlier repair, and the journal need
                    // only retain the largest platform's work.
                    await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
                    await availability.RefreshPlatformPayloadAssertionsAsync(currentPlatformId, ct);
                    await transaction.CommitAsync(ct);
                }
                long repairedEntries = await db.SourceEntries
                    .LongCountAsync(entry => entry.HasLocalPayload, ct);
                long repairedTitles = await db.Titles
                    .LongCountAsync(title => title.HasLocalPayload, ct);
                if (repairedEntries != expectedPayloadEntries || repairedTitles != expectedPayloadTitles)
                {
                    throw new InvalidOperationException(
                        $"Repair truth mismatch: entries {repairedEntries:N0}/{expectedPayloadEntries:N0}, " +
                        $"titles {repairedTitles:N0}/{expectedPayloadTitles:N0}.");
                }

                return repairedTitles;
            },
            cancellationToken);
        if (fullRepair.SucceededIterations != 1)
        {
            throw new InvalidOperationException(
                "Payload repair case failed; see the captured case notes for the production-path exception.");
        }
        await AssertRepairTruthAsync(context, cancellationToken);

        int targetedCount = Math.Min(100_000, context.Data.Scale.Titles);
        var (firstTitleId, lastTitleId) = await GetTitleRangeAsync(
            context, targetedCount, cancellationToken);
        await CreateLinkBackupAsync(context, firstTitleId, lastTitleId, cancellationToken);
        CaseResult highFanout;
        try
        {
            highFanout = await runner.RunCaseAsync(
                Name,
                $"link-delete-commit-{targetedCount:N0}-titles",
                1,
                async (services, ct) =>
                {
                    var db = services.GetRequiredService<RomdDbContext>();
                    var unitOfWork = services.GetRequiredService<IUnitOfWork>();
                    await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
                    await db.TitleSourceLinks
                        .Where(link => link.TitleId >= firstTitleId && link.TitleId <= lastTitleId)
                        .ExecuteDeleteAsync(ct);
                    await services.GetRequiredService<ITitlePayloadAvailabilityProjection>()
                        .RollupTitleRangeAsync(firstTitleId - 1, lastTitleId, ct);
                    await transaction.CommitAsync(ct);
                    return targetedCount;
                },
                cancellationToken);
        }
        finally
        {
            // Fixture recovery must outlive caller cancellation. The backup remains durable if
            // cleanup itself is interrupted, and the next invocation repairs it before work.
            await RecoverLinkBackupIfPresentAsync(context, CancellationToken.None);
        }

        return new ScenarioReport(Name, [platformBreakdown, sourceLifecycle, fullRepair, highFanout]);
    }

    private static async Task AssertRepairTruthAsync(
        ScenarioContext context,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        long entryMismatches = await db.SourceEntries.LongCountAsync(entry =>
            entry.HasLocalPayload != db.DatGames.Any(game =>
                game.SourceEntryId == entry.Id && db.DatRoms.Any(rom =>
                    rom.DatGameId == game.Id && rom.RomFileId != null)), cancellationToken);
        var effectivePayloadEntries = db.EffectiveSourceEntries()
            .Where(entry => entry.HasLocalPayload);
        long titleMismatches = await db.Titles.LongCountAsync(title =>
            title.HasLocalPayload != db.TitleSourceLinks.Any(link =>
                link.TitleId == title.Id && effectivePayloadEntries.Any(entry =>
                    entry.Id == link.SourceEntryId)), cancellationToken);
        if (entryMismatches != 0 || titleMismatches != 0)
        {
            throw new InvalidOperationException(
                $"Repair left {entryMismatches:N0} source-entry and {titleMismatches:N0} title mismatch(es).");
        }
    }

    private static async Task<(int First, int Last)> GetTitleRangeAsync(
        ScenarioContext context,
        int count,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Provider.CreateAsyncScope();
        var titles = scope.ServiceProvider.GetRequiredService<RomdDbContext>().Titles.AsNoTracking();
        int first = await titles.OrderBy(title => title.Id)
            .Select(title => title.Id)
            .FirstAsync(cancellationToken);
        int last = await titles.OrderBy(title => title.Id)
            .Select(title => title.Id)
            .Skip(count - 1)
            .FirstAsync(cancellationToken);
        return (first, last);
    }

    private static async Task CreateLinkBackupAsync(
        ScenarioContext context,
        int firstTitleId,
        int lastTitleId,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             CREATE TABLE romd."_PayloadAvailabilityLinkBackup" AS
             SELECT "SourceEntryId", "TitleId", "CreatedAt", "CreatedByUserId", "UpdatedAt", "UpdatedByUserId"
             FROM romd."TitleSourceLinks"
             WHERE "TitleId" >= {firstTitleId} AND "TitleId" <= {lastTitleId};
             """,
            cancellationToken);
    }

    private static async Task RecoverLinkBackupIfPresentAsync(
        ScenarioContext context,
        CancellationToken cancellationToken)
    {
        await using var discoveryScope = context.Provider.CreateAsyncScope();
        var discoveryDb = discoveryScope.ServiceProvider.GetRequiredService<RomdDbContext>();
        int exists = await discoveryDb.Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(*)::int AS "Value"
                FROM information_schema.tables
                WHERE table_schema = 'romd' AND table_name = '_PayloadAvailabilityLinkBackup'
                """)
            .SingleAsync(cancellationToken);
        if (exists == 0)
        {
            return;
        }

        var range = await discoveryDb.Database.SqlQueryRaw<BackupTitleRange>(
                """
                SELECT MIN("TitleId") AS "FirstTitleId", MAX("TitleId") AS "LastTitleId"
                FROM romd."_PayloadAvailabilityLinkBackup"
                """)
            .SingleAsync(cancellationToken);
        if (range.FirstTitleId is null || range.LastTitleId is null)
        {
            await discoveryDb.Database.ExecuteSqlRawAsync(
                "DROP TABLE romd.\"_PayloadAvailabilityLinkBackup\";", cancellationToken);
            return;
        }

        await RestoreLinkBackupAsync(
            context,
            range.FirstTitleId.Value,
            range.LastTitleId.Value,
            cancellationToken);
    }

    private static async Task RestoreLinkBackupAsync(
        ScenarioContext context,
        int firstTitleId,
        int lastTitleId,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<RomdDbContext>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // Idempotent for both measured outcomes: a committed delete has no rows here;
            // a rolled-back/failed operation still has them. Normalize either state before
            // restoring so cleanup cannot collide with or obscure the measured disposition.
            await db.TitleSourceLinks
                .Where(link => link.TitleId >= firstTitleId && link.TitleId <= lastTitleId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO romd."TitleSourceLinks"
                    ("SourceEntryId", "TitleId", "CreatedAt", "CreatedByUserId", "UpdatedAt", "UpdatedByUserId")
                SELECT "SourceEntryId", "TitleId", "CreatedAt", "CreatedByUserId", "UpdatedAt", "UpdatedByUserId"
                FROM romd."_PayloadAvailabilityLinkBackup";
                """,
                cancellationToken);
            await services.GetRequiredService<ITitlePayloadAvailabilityProjection>()
                .RollupTitleRangeAsync(firstTitleId - 1, lastTitleId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync(
            "DROP TABLE romd.\"_PayloadAvailabilityLinkBackup\";", cancellationToken);
    }

    /// <summary>
    ///     The SQLite run truncated the WAL here so file-size deltas stayed comparable; on PostgreSQL
    ///     the equivalent fairness step is fresh planner statistics for the tables the repair rewrote.
    /// </summary>
    private static async Task AnalyzeTouchedTablesAsync(ScenarioRunner runner, CancellationToken cancellationToken)
    {
        await using var connection = runner.OpenRawConnection();
        await connection.OpenAsync(cancellationToken);
        await using var analyze = connection.CreateCommand();
        analyze.CommandText = "ANALYZE romd.\"Titles\", romd.\"SourceEntries\", romd.\"TitleSourceLinks\";";
        await analyze.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record BackupTitleRange(int? FirstTitleId, int? LastTitleId);
}
