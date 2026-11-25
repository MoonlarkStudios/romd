using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Dat.Subscriptions;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Source.Dat.Commands.CheckDatSubscription;

public enum DatSubscriptionCheckMode { Manual, Scheduled }
/// <summary>Checks an existing enrollment now; scheduled checks also require current eligibility and due time.</summary>
public sealed record CheckDatSubscriptionCommand(int SubscriptionId, DatSubscriptionCheckMode Mode = DatSubscriptionCheckMode.Manual,
    ErrorOr<IReadOnlyList<PublishedDatCatalog>>? SweepDirectory = null) : ICommand<DatCatalogSubscription>;

public sealed class CheckDatSubscriptionCommandHandler(IDatSubscriptionCheckReader reader, IDatSubscriptionCheckStore store,
    IDatRepository dats, ISignedDatCatalogClient client, IDatReplacementReview review, IFileStorageService storage,
    IUnitOfWork unitOfWork, TimeProvider time, ILogger<CheckDatSubscriptionCommandHandler> logger)
    : ICommandHandler<CheckDatSubscriptionCommand, DatCatalogSubscription>
{
    public async Task<ErrorOr<DatCatalogSubscription>> HandleAsync(CheckDatSubscriptionCommand command, CancellationToken ct = default)
    {
        var workflow = new DatSubscriptionCheckWorkflow(reader, store, dats, client, review, storage, time);
        var original = await reader.ReadAsync(command.SubscriptionId, ct);
        if (original is null)
            return DatEnrollmentErrors.NotFound();
        string catalogId = original.CatalogId;
        if (!client.Enabled)
            return DatEnrollmentErrors.Unavailable();
        DatCatalogSubscription? response = null;
        bool committed = false;
        bool failed = false;
        // Existing long-I/O exclusion is retained; its protocol migration is separate from ownership.
        var transaction = await unitOfWork.BeginTransactionAsync(ct);
        DatSubscriptionCheckOutcome? outcome = null;
        try
        {
            bool scheduled = command.Mode == DatSubscriptionCheckMode.Scheduled;
            outcome = await workflow.PrepareAsync(catalogId, command.SubscriptionId, scheduled, null, ct,
                scheduled ? command.SweepDirectory : null);
            if (outcome is DatSubscriptionCheckOutcome.Rejected rejected)
                return rejected.Errors.ToList();
            DatSubscriptionCheckSnapshot? row;
            DatSubscriptionCheckValues values;
            if (outcome is DatSubscriptionCheckOutcome.Checked check)
            {
                row = check.Row;
                values = check.Values;
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

            if (row is null)
                throw new InvalidOperationException("An existing subscription check cannot create an enrollment.");
            var write = await store.StageExistingAsync(row.Id, values, ct);
            await unitOfWork.FlushAsync(ct);
            _ = write.ReadIdAfterFlush();
            var written = await reader.ReadWrittenStatusAsync(write, ct);
            response = written;
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
