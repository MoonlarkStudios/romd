using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Dat.Subscriptions;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Source.Dat.Commands.CreateDatSubscription;

/// <summary>Enrolls and checks once, or replays without checking; DatId selects an explicit active source.</summary>
public sealed record CreateDatSubscriptionCommand(string CatalogId, int? DatId = null) : ICommand<DatSubscriptionEnrollment>;

public sealed class CreateDatSubscriptionCommandHandler(IDatSubscriptionCheckReader reader, IDatSubscriptionCheckStore store,
    IDatRepository dats, ISignedDatCatalogClient client, IDatReplacementReview review, IFileStorageService storage,
    IUnitOfWork unitOfWork, TimeProvider time, ILogger<CreateDatSubscriptionCommandHandler> logger)
    : ICommandHandler<CreateDatSubscriptionCommand, DatSubscriptionEnrollment>
{
    public async Task<ErrorOr<DatSubscriptionEnrollment>> HandleAsync(CreateDatSubscriptionCommand command, CancellationToken ct = default)
    {
        var workflow = new DatSubscriptionCheckWorkflow(reader, store, dats, client, review, storage, time);
        var replay = await workflow.FindReplayAsync(command.CatalogId, command.DatId, ct);
        if (replay.IsError)
            return replay.Errors;
        if (replay.Value is { } replayedRow)
        {
            var existing = await reader.ReadReplayStatusAsync(replayedRow, ct);
            return new DatSubscriptionEnrollment(existing, false);
        }
        string catalogId = command.CatalogId;
        if (!client.Enabled)
            return DatEnrollmentErrors.Unavailable();
        DatSubscriptionEnrollment? response = null;
        bool committed = false;
        bool failed = false;
        // Existing long-I/O exclusion is retained; its protocol migration is separate from ownership.
        var transaction = await unitOfWork.BeginTransactionAsync(ct);
        DatSubscriptionCheckOutcome? outcome = null;
        try
        {
            outcome = await workflow.PrepareAsync(catalogId, null, false, command.DatId, ct);
            if (outcome is DatSubscriptionCheckOutcome.Rejected rejected)
                return rejected.Errors.ToList();
            if (outcome is DatSubscriptionCheckOutcome.ExistingEnrollment replayed)
            {
                var existing = await reader.ReadReplayStatusAsync(replayed.Row, ct);
                return new DatSubscriptionEnrollment(existing, false);
            }
            DatSubscriptionCheckSnapshot? row;
            DatSubscriptionCheckValues values;
            if (outcome is DatSubscriptionCheckOutcome.CheckedFailure checkedFailure)
            {
                row = checkedFailure.Row;
                values = checkedFailure.Values;
            }
            else if (outcome is DatSubscriptionCheckOutcome.VerifiedCandidate verified)
            {
                var publication = await storage.PublishStagedAsync(verified.File, ct);
                if (publication.RequiresFlush)
                    await unitOfWork.FlushAsync(ct);
                int fileId = publication.ReadAfterFlush().File.Id;
                row = verified.Row;
                values = verified.Values(fileId, time.GetUtcNow());
            }
            else
                throw new InvalidOperationException("Unknown subscription check outcome.");

            var write = row is null
                ? await store.StageNewAsync(catalogId, values, ct)
                : await store.StageExistingAsync(row.Id, values, ct);
            await unitOfWork.FlushAsync(ct);
            _ = write.ReadIdAfterFlush();
            var written = await reader.ReadWrittenStatusAsync(write, ct);
            response = new DatSubscriptionEnrollment(written, row is null);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            committed = true;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            try
            {
                if (outcome is not null)
                    await outcome.DisposePreservingOutcomeAsync(committed, failed, logger, catalogId);
            }
            finally
            {
                await transaction.DisposePreservingOutcomeAsync(committed, failed, logger);
            }
        }
        return response!;
    }
}
