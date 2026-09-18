using ErrorOr;
using Romd.Admin.Application.Common.Persistence;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Titles.Enrichment;

namespace Romd.Admin.Application.Titles.Commands.ConfirmExternalId;

/// <summary>
///     Command to confirm an existing external ID for a title,
///     making it immutable to auto-enrichment.
/// </summary>
public sealed record ConfirmExternalIdCommand(
    int TitleId,
    string Provider
) : ICommand<Deleted>;

public sealed class ConfirmExternalIdCommandHandler(
    ITitleRepository titleRepository,
    IUnitOfWork unitOfWork,
    IRematerializationScheduler rematerializationScheduler,
    ILogger<ConfirmExternalIdCommandHandler> logger) : ICommandHandler<ConfirmExternalIdCommand, Deleted>
{
    public async Task<ErrorOr<Deleted>> HandleAsync(ConfirmExternalIdCommand command, CancellationToken ct = default)
    {
        var title = await titleRepository.GetWithCollectionsAsync(command.TitleId, ct);
        if (title is null)
        {
            return CatalogErrors.TitleNotFound(command.TitleId);
        }

        var confirmed = title.ConfirmExternalId(command.Provider);
        if (!confirmed)
        {
            return Error.NotFound(
                "Catalog.ExternalIdNotFound",
                $"No external ID found for provider '{command.Provider}'");
        }

        return await unitOfWork.ExecuteInTransactionAsync<Deleted>(async token =>
        {
            await rematerializationScheduler.EnqueueTitleAsync(title.Id, token);
            await titleRepository.UpdateAsync(title, token);
            return Result.Deleted;
        }, logger, ct);
    }
}
