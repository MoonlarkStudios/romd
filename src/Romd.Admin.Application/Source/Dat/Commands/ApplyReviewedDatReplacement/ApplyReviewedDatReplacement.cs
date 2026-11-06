using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Source.Dat.Commands.ApplyReviewedDatReplacement;

/// <summary>The caller retains the candidate stream; review captures the exact private bytes to accept.</summary>
public sealed record ApplyReviewedDatReplacementCommand(int DatId, Stream Candidate, string ActiveSha256, string CandidateSha256)
    : ICommand<Guid>;

public sealed class ApplyReviewedDatReplacementCommandHandler(
    IReviewedDatReplacementPreparation review,
    IJobWorkspaceExclusion exclusion,
    IReplaceDatWorkspacePreparation workspace,
    IReplaceDatJobAcceptance acceptance,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ApplyReviewedDatReplacementCommandHandler> logger) : ICommandHandler<ApplyReviewedDatReplacementCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(ApplyReviewedDatReplacementCommand command, CancellationToken ct = default)
    {
        var prepared = await review.PrepareAsync(command.DatId, command.Candidate, command.ActiveSha256, command.CandidateSha256, ct);
        if (prepared.IsError)
            return prepared.Errors;

        var document = prepared.Value;
        IJobWorkspaceLease? lease = null;
        Stream? content = null;
        ITransaction? transaction = null;
        ReplaceDatJob? job = null;
        bool committed = false;
        try
        {
            // Preserve the direct review path's filename, null platform override and null actor.
            job = ReplaceDatJob.Create(document.Facts.ExistingDatId, "reviewed-catalog.dat", clock.GetUtcNow());
            lease = await exclusion.AcquireAsync(job.Id, ct);
            content = document.OpenRead();
            await workspace.PrepareAsync(new(job.Id, job.SourceFilename), content, lease, ct);

            transaction = await unitOfWork.BeginTransactionAsync(ct);
            await acceptance.AddStagedAsync(job, ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            committed = true;
            return job.Id;
        }
        finally
        {
            // Keep the worker/orphan exclusion until the database boundary has settled. Complete or
            // partial inputs remain on every failure: a failed acknowledgement can still be durable.
            await ReleaseAsync(transaction, "transaction", job?.Id, committed);
            await ReleaseAsync(content, "document stream", job?.Id, committed);
            try
            {
                document.Dispose();
            }
            catch (Exception error)
            {
                LogCleanupFailure(error, "review receipt", job?.Id, committed);
            }
            await ReleaseAsync(lease, "workspace lease", job?.Id, committed);
        }
    }

    private async ValueTask ReleaseAsync(IAsyncDisposable? resource, string name, Guid? jobId, bool committed)
    {
        if (resource is null)
            return;
        try
        {
            await resource.DisposeAsync();
        }
        catch (Exception error)
        {
            LogCleanupFailure(error, name, jobId, committed);
        }
    }

    private void LogCleanupFailure(Exception error, string resource, Guid? jobId, bool committed) =>
        logger.LogWarning(error, "Failed to release {Resource} for reviewed DAT replacement job {JobId}; commit acknowledged: {Committed}",
            resource, jobId, committed);
}
