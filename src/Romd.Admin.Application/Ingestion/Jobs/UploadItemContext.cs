using Romd.Admin.Application.Common.Persistence;
using Romd.Domain.Jobs;
using System.Data.Common;

namespace Romd.Admin.Application.Ingestion.Jobs;

/// <summary>Worker-owned file identity within an accepted upload workspace.</summary>
public sealed record UploadItemContext(Guid JobId, string RelativePath);

/// <summary>An outcome write or commit was not acknowledged; consult the ledger before retrying.</summary>
public sealed class UploadOutcomeUncertainException(string message, Exception? inner = null) : Exception(message, inner);

public static class UploadOutcomeCommit
{
    /// <summary>Stages the outcome inside the content transaction before committing both.</summary>
    public static async Task CommitAsync(
        IUnitOfWork unitOfWork, ITransaction transaction, IJobItemRepository? repository, JobItem? outcome, CancellationToken ct)
    {
        if (outcome is null)
        {
            await transaction.CommitAsync(ct);
            return;
        }
        if (repository is null)
            throw new InvalidOperationException("Upload outcomes require a journal.");
        await repository.AddStagedAsync(outcome.WithUploadIdentity(), ct);
        await unitOfWork.FlushAsync(ct);
        try
        {
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or JobExecutionOwnershipLostException)
            && !IsDefinitiveRejection(ex))
        {
            throw new UploadOutcomeUncertainException("The upload outcome transaction was not acknowledged.", ex);
        }
    }

    // An explicit SQL data/constraint rejection or transaction rollback confirms
    // that nothing committed. Only a missing acknowledgement needs ledger recovery.
    private static bool IsDefinitiveRejection(Exception error) =>
        (error is DbException { SqlState: { } state } &&
        (state.StartsWith("22", StringComparison.Ordinal) || state.StartsWith("23", StringComparison.Ordinal)
            || state.StartsWith("40", StringComparison.Ordinal))) ||
        (error.InnerException is { } inner && IsDefinitiveRejection(inner));
}
