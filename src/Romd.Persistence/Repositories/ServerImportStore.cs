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
        var row = await db.Jobs.OfType<UploadJobEntity>().Where(job => job.Id == id).Select(job => job.Phase).SingleOrDefaultAsync(ct);
        return Enum.TryParse<UploadPhase>(row, out var phase)
            ? new ServerImportJob(id, phase,
                phase is UploadPhase.Completed or UploadPhase.CompletedWithErrors or UploadPhase.Failed or UploadPhase.Cancelled)
            : null;
    }

    public async Task<IReadOnlyList<ServerImportFile>> ReadSubmittedFilesAsync(string directory, CancellationToken ct)
    {
        string? json = await db.Set<ServerImportActivityEntity>()
            .Where(row => row.DirectoryKey == Key(directory)).Select(row => row.SubmittedFilesJson).SingleOrDefaultAsync(ct);
        return json is null ? [] : DeserializeFiles(json);
    }

    public async Task<ServerImportSelection?> ReadSelectionAsync(Guid jobId, CancellationToken ct)
    {
        string? json = await db.Set<ServerImportSelectionEntity>()
            .Where(row => row.JobId == jobId).Select(row => row.FilesJson).SingleOrDefaultAsync(ct);
        return json is null ? null : new ServerImportSelection(DeserializeFiles(json));
    }

    public async Task StageActivityAsync(string directory, ServerImportActivity activity,
        IReadOnlyList<ServerImportFile>? submittedFiles, CancellationToken ct)
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
        if (submittedFiles is not null)
            row.SubmittedFilesJson = JsonSerializer.Serialize(submittedFiles);
    }

    public void StageSelection(Guid jobId, ServerImportSelection selection) =>
        db.Set<ServerImportSelectionEntity>().Add(new ServerImportSelectionEntity
        {
            JobId = jobId,
            FilesJson = JsonSerializer.Serialize(selection.Files)
        });

    private static ServerImportFile[] DeserializeFiles(string json) =>
        JsonSerializer.Deserialize<ServerImportFile[]>(json) ?? throw new JsonException("Server import file selection is missing.");

    private static string Key(string directory) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)));
}
