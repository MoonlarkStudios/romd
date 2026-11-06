using System.Security.Cryptography;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;

namespace Romd.Infrastructure.Source;

public sealed class OriginalDiscDescriptorIntake(IOriginalDiscDescriptorRepository descriptors, IFileStorageService storage,
    IJobItemRepository outcomes, IUnitOfWork unitOfWork, IMediaReconciliationRequests requests, TimeProvider clock) : IOriginalDiscDescriptorIntake
{
    public async Task<JobItem> IntakeAsync(Stream stream, Guid jobId, string relativePath, CancellationToken ct)
    {
        // Bounded read before staging or taking any transaction/file lock.
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + read > OriginalCueDescriptor.MaximumBytes)
                return JobItem.ForRom(jobId, relativePath, bytes.Length + read, RomIngestOutcome.Failed, null, [], null,
                    "Original CUE descriptor exceeds the 1 MiB intake limit.", createdAt: clock.GetUtcNow()).WithUploadIdentity();
            bytes.Write(buffer, 0, read);
        }
        string entryPoint = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        var parsed = OriginalCueDescriptor.Parse(bytes.ToArray(), entryPoint);
        bytes.Position = 0;
        await using var staged = await storage.StageAsync(bytes, ct);
        await using var tx = await unitOfWork.BeginTransactionAsync(ct);
        var prior = await outcomes.FindOutcomeAsync(jobId, JobItemKind.Rom, relativePath, ct);
        if (prior is not null)
            return prior;
        var publication = await storage.PublishStagedAsync(staged, ct);
        if (publication.RequiresFlush)
            await unitOfWork.FlushAsync(ct);
        var file = publication.ReadAfterFlush();
        await descriptors.RecordStagedAsync(jobId, relativePath, entryPoint, file.File.Id, parsed, ct);
        var outcome = JobItem.ForRom(jobId, relativePath, bytes.Length, RomIngestOutcome.Ingested, null, [], null, null, createdAt: clock.GetUtcNow(), true).WithUploadIdentity();
        await outcomes.AddStagedAsync(outcome, ct);
        await tx.CommitAsync(ct);
        return outcome;
    }

    public async Task ReconcileAsync(Guid jobId, CancellationToken ct)
    {
        // DAT-matched CUEs retain normal ROM ownership as well as a descriptor attachment.
        // Each bounded batch is derived from successful journal bindings, never loose filenames.
        while (await descriptors.ReadUnattachedMatchedCuesAsync(jobId, ct) is { Count: > 0 } sources)
            foreach (var source in sources)
            {
                // One unreadable matched CUE becomes a visible layout issue; it must not fail the committed upload or its siblings.
                var parsed = await ReadExactAsync(source, ct) is { } bytes ? OriginalCueDescriptor.Parse(bytes, source.RelativePath)
                    : new OriginalCueDescriptor([], "DescriptorUnavailable");
                await using var attachment = await unitOfWork.BeginTransactionAsync(ct);
                await descriptors.RecordMatchedStagedAsync(jobId, source, parsed, ct);
                await attachment.CommitAsync(ct);
            }
        await using var tx = await unitOfWork.BeginTransactionAsync(ct);
        var changed = await descriptors.ReconcileStagedAsync(jobId, ct);
        await unitOfWork.FlushAsync(ct);
        foreach (int layoutId in changed.Order())
        {
            var demand = await requests.StageSubjectAsync(new(Guid.NewGuid(), new MediaReconciliationSubject.OriginalLayout(layoutId),
                false, MediaReconciliationCause.Import, null, null), ct);
            if (demand.IsError)
                throw new InvalidOperationException($"CD layout demand could not be staged ({demand.FirstError.Code}).");
        }
        await tx.CommitAsync(ct);
    }

    private async Task<byte[]?> ReadExactAsync(OriginalDiscDescriptorSource source, CancellationToken ct)
    {
        await using var stream = await storage.RetrieveByIdAsync(source.FileId, ct);
        if (stream is null)
            return null;
        byte[] bytes = new byte[checked((int)source.Size)];
        try
        { await stream.ReadExactlyAsync(bytes, ct); }
        catch (Exception error) when (error is EndOfStreamException or InvalidDataException) { return null; }
        return await stream.ReadAsync(new byte[1], ct) == 0 && Sha256.FromSpan(SHA256.HashData(bytes)) == source.Sha256 ? bytes : null;
    }
}
