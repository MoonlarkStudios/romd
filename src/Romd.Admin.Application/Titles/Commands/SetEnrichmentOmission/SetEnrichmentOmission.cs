using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Titles.Commands.SetEnrichmentOmission;

/// <summary>
///     Accepts a core metadata field as absent, or revisits that decision. <paramref name="IfMatch" /> is the title
///     revision the curator read; the write rotates it like every core title write.
/// </summary>
public sealed record SetEnrichmentOmissionCommand(
    int TitleId,
    string Field,
    bool Accepted,
    RevisionPrecondition IfMatch) : ICommand<TitleDetail>;

public sealed class SetEnrichmentOmissionCommandHandler(
    IReferenceCatalogService referenceCatalog,
    IEnrichmentWorkbench workbench,
    ITitleDetailReader titleDetails,
    IUnitOfWork unitOfWork, ILogger<SetEnrichmentOmissionCommandHandler> logger) : ICommandHandler<SetEnrichmentOmissionCommand, TitleDetail>
{
    public async Task<ErrorOr<TitleDetail>> HandleAsync(SetEnrichmentOmissionCommand command, CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var result = await unitOfWork.ExecuteInTransactionAsync<TitleDetailData>(async token =>
        {
            var written = await workbench.SetOmissionAsync(command.TitleId, command.Field, command.Accepted, command.IfMatch, token);
            if (written.IsError)
            {
                return written.Errors;
            }

            // The transaction holds the revision this write rotated to, so the response read here is this write's own
            // state: a writer that commits after it is in neither the body nor the ETag.
            await unitOfWork.FlushAsync(token);
            var detail = await titleDetails.GetDetailAsync(command.TitleId, token);
            if (detail is null)
            {
                return CatalogErrors.TitleNotFound();
            }

            return detail;
        }, logger, ct);
        return result.IsError ? result.Errors : TitleDetailMapper.ToContract(result.Value, systemKeys);
    }
}
