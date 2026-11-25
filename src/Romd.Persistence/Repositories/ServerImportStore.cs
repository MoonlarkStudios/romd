using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class ServerImportStore(RomdDbContext db) : IServerImportStore
{
    private const int _acceptanceFamily = 1397313090;

    public Task AcquireAcceptanceLockAsync(CancellationToken ct) =>
        JobAcceptanceLock.AcquireAsync(db, _acceptanceFamily, 0, ct);

    public Task<ServerImportActivity?> ReadActivityAsync(string directory, CancellationToken ct) =>
        db.Set<ServerImportActivityEntity>().Where(row => row.DirectoryKey == Key(directory))
            .Select(row => new ServerImportActivity(
                row.CheckedAt, row.Outcome, row.WaitingFiles, row.Message, row.JobId, row.LastQueuedFiles))
            .SingleOrDefaultAsync(ct);

    public async Task<ServerImportJob?> ReadJobAsync(Guid? jobId, CancellationToken ct)
    {
        if (jobId is not { } id)
            return null;
        var row = await db.Jobs.OfType<UploadJobEntity>().Where(job => job.Id == id)
            .Select(job => new { job.Phase, job.RomsRejected }).SingleOrDefaultAsync(ct);
        return row is null ? null : ToJob(id, row.Phase, row.RomsRejected);
    }

    public async Task<IReadOnlyList<ServerImportFile>> FindUnsubmittedFilesAsync(
        string directory, IReadOnlyList<ServerImportFile> ready, CancellationToken ct)
    {
        if (ready.Count == 0)
            return [];

        // Compare only files that still exist. The accepted job already owns its selection for
        // worker recovery and retry. Copy-era selections must not suppress a move-mode attempt.
        var submitted = (await db.Database.SqlQuery<int>($"""
            WITH submitted AS (
                SELECT file.stamp
                FROM romd."ServerImportSelections" AS selection
                JOIN romd."Jobs" AS job ON job."Id" = selection."JobId"
                CROSS JOIN LATERAL jsonb_array_elements(selection."FilesJson") AS file(stamp)
                WHERE job."ImportSourcePath" = {directory} AND job."ImportMove"
            )
            SELECT (ready.ordinality - 1)::integer AS "Value"
            FROM jsonb_array_elements(CAST({JsonSerializer.Serialize(ready)} AS jsonb))
                WITH ORDINALITY AS ready(stamp, ordinality)
            WHERE EXISTS (SELECT 1 FROM submitted WHERE submitted.stamp = ready.stamp)
            """).ToListAsync(ct)).ToHashSet();

        return ready.Where((_, index) => !submitted.Contains(index)).ToArray();
    }

    public async Task<ServerImportSelection?> ReadSelectionAsync(Guid jobId, CancellationToken ct)
    {
        string? json = await db.Set<ServerImportSelectionEntity>()
            .Where(row => row.JobId == jobId).Select(row => row.FilesJson).SingleOrDefaultAsync(ct);
        return json is null ? null : new ServerImportSelection(DeserializeFiles(json));
    }

    public async Task<ServerImportRetryBatch?> ReadRetryBatchAsync(string directory, Guid jobId, CancellationToken ct)
    {
        var batch = await (from selection in db.Set<ServerImportSelectionEntity>()
                           join job in db.Jobs.OfType<UploadJobEntity>() on selection.JobId equals job.Id
                           where job.Id == jobId && job.ImportSourcePath == directory && job.ImportMove
                           select new { job.CorrelationId, job.Phase, job.RomsRejected, selection.FilesJson }).SingleOrDefaultAsync(ct);
        return batch is not null && ToJob(jobId, batch.Phase, batch.RomsRejected)?.CanRetry == true
            ? new ServerImportRetryBatch(batch.CorrelationId, new ServerImportSelection(DeserializeFiles(batch.FilesJson)))
            : null;
    }

    public async Task StageActivityAsync(string directory, ServerImportActivity activity, CancellationToken ct)
    {
        string key = Key(directory);
        var row = await db.Set<ServerImportActivityEntity>().AsTracking().SingleOrDefaultAsync(value => value.DirectoryKey == key, ct);
        if (row is null)
        {
            row = new ServerImportActivityEntity { DirectoryKey = key };
            db.Set<ServerImportActivityEntity>().Add(row);
        }
        row.CheckedAt = activity.CheckedAt;
        row.Outcome = activity.Outcome;
        row.WaitingFiles = activity.WaitingFiles;
        row.Message = activity.Message;
        row.JobId = activity.JobId;
        row.LastQueuedFiles = activity.LastQueuedFiles;
    }

    public void StageSelection(Guid jobId, ServerImportSelection selection) =>
        db.Set<ServerImportSelectionEntity>().Add(new ServerImportSelectionEntity
        {
            JobId = jobId,
            FilesJson = JsonSerializer.Serialize(selection.Files)
        });

    private static ServerImportJob? ToJob(Guid id, string phaseName, int rejected) =>
        Enum.TryParse<UploadPhase>(phaseName, out var phase)
            ? new ServerImportJob(id, phase,
                phase is UploadPhase.Completed or UploadPhase.CompletedWithErrors or UploadPhase.Failed or UploadPhase.Cancelled,
                rejected > 0)
            : null;

    private static ServerImportFile[] DeserializeFiles(string json) =>
        JsonSerializer.Deserialize<ServerImportFile[]>(json) ?? throw new JsonException("Server import file selection is missing.");

    private static string Key(string directory) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)));
}
