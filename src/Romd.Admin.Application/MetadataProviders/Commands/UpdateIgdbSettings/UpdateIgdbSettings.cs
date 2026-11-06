using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.MetadataProviders;

namespace Romd.Admin.Application.MetadataProviders.Commands.UpdateIgdbSettings;

public sealed record UpdateIgdbSettingsCommand(UpdateIgdbProviderSettingsRequest Request, RevisionPrecondition Precondition) : ICommand<IgdbProviderSettingsDto>;

public sealed class UpdateIgdbSettingsCommandHandler(IIgdbProviderSettingsService service, IUnitOfWork unitOfWork,
    ILogger<UpdateIgdbSettingsCommandHandler> logger)
    : ICommandHandler<UpdateIgdbSettingsCommand, IgdbProviderSettingsDto>
{
    public async Task<ErrorOr<IgdbProviderSettingsDto>> HandleAsync(UpdateIgdbSettingsCommand command, CancellationToken ct = default)
    {
        return await unitOfWork.ExecuteInTransactionAsync<IgdbProviderSettingsDto>(
            token => service.UpdateStagedAsync(command.Request, command.Precondition, token), logger, ct);
    }
}
