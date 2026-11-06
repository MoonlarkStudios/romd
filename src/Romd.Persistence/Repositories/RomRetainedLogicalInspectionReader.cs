using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Source.Rom;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;

namespace Romd.Persistence.Repositories;

/// <summary>Reads immutable logical observations while refusing caller-only staged authority.</summary>
public sealed class RomRetainedLogicalInspectionReader(RomdDbContext context, DbContextOptions<RomdDbContext> readOptions) : IRomRetainedLogicalInspectionReader
{
    public async Task<ErrorOr<RomRetainedLogicalInspection?>> ReadAsync(Guid inspectionId, CancellationToken ct = default)
    {
        if (inspectionId == Guid.Empty)
            return RomRetainedLogicalInspectionErrors.InvalidIdentity;
        var visible = await ReadSnapshotAsync(context, inspectionId, ct);
        if (context.Database.CurrentTransaction is null || visible.IsError)
            return visible;
        await using var committed = new RomdDbContext(readOptions);
        if (context.Database.ProviderName != committed.Database.ProviderName || ReferenceEquals(context.Database.GetDbConnection(), committed.Database.GetDbConnection()))
            return RomRetainedLogicalInspectionErrors.Unavailable;
        var retained = await ReadSnapshotAsync(committed, inspectionId, ct);
        if (retained.IsError)
            return retained.Errors;
        if (!Same(visible.Value, retained.Value))
            return RomRetainedLogicalInspectionErrors.StagedChanges;
        return retained;
    }
    private static bool Same(RomRetainedLogicalInspection? first, RomRetainedLogicalInspection? second) => first is null ? second is null :
        second is not null && first.RomId == second.RomId && first.Observation.Id == second.Observation.Id &&
        first.Observation.ObservedAt == second.Observation.ObservedAt && first.Observation.SameObservation(second.Observation);
    private static async Task<ErrorOr<RomRetainedLogicalInspection?>> ReadSnapshotAsync(RomdDbContext db, Guid inspectionId, CancellationToken ct)
    {
        await using var snapshot = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        if (snapshot is not null)
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        int? romId = (await db.Database.SqlQuery<int>($"""SELECT "RomId" AS "Value" FROM romd."LogicalInspections" WHERE "Id" = {inspectionId}""")
            .ToArrayAsync(ct)).Select(id => (int?)id).SingleOrDefault();
        if (romId is null)
            return (RomRetainedLogicalInspection?)null;
        if (db.ChangeTracker.Entries<RomFileEntity>().Any(entry => entry.Entity.Id == romId && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            return RomRetainedLogicalInspectionErrors.StagedChanges;
        try
        {
            var reader = new LogicalInspectionReader(db);
            var rows = db.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel == IsolationLevel.ReadCommitted
                ? await reader.ReadLockedPublicationAsync([romId.Value], ct) : await reader.ReadAsync([romId.Value], ct);
            var observation = rows.GetValueOrDefault(romId.Value)?.SingleOrDefault(row => row.Id == inspectionId);
            if (snapshot is not null)
                await snapshot.CommitAsync(ct);
            return observation is null ? (RomRetainedLogicalInspection?)null : new RomRetainedLogicalInspection(romId.Value, observation);
        }
        catch (InvalidDataException) { return RomRetainedLogicalInspectionErrors.Conflicting; }
    }
}
