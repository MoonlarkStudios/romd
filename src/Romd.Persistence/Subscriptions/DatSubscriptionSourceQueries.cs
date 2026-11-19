using Romd.Persistence.Entities;

namespace Romd.Persistence.Subscriptions;

internal static class DatSubscriptionSourceQueries
{
    internal static readonly string[] TerminalJobPhases = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred"];

    internal static IQueryable<int> SubscribedSourceIds(RomdDbContext db) =>
        db.DatSubscriptions.Where(row => row.DatSourceId.HasValue).Select(row => row.DatSourceId!.Value)
            .Concat(CompletedFirstImportDocuments(db, db.DatSubscriptions).Select(dat => dat.DatSourceId));

    // Before the next check binds the source, identify a first import by its exact
    // document and system. A name match or an ambiguous document is insufficient.
    internal static IQueryable<DatFileEntity> CompletedFirstImportDocuments(
        RomdDbContext db, IQueryable<DatSubscriptionEntity> subscriptions) =>
        from row in subscriptions
        where row.DatSourceId == null && row.JobId != null && row.CandidateFileId != null
            && !db.Jobs.Any(job => job.Id == row.JobId && !TerminalJobPhases.Contains(job.Phase))
        from dat in db.DatFiles
        where dat.FileId == row.CandidateFileId && dat.PlatformId == row.PlatformId && dat.Lifecycle == "Active"
            && !db.DatFiles.Any(other => other.Id != dat.Id && other.FileId == dat.FileId
                && other.PlatformId == dat.PlatformId && other.Lifecycle == "Active")
        select dat;
}
