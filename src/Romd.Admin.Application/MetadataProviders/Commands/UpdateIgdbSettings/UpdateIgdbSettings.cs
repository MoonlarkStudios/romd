using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.MetadataProviders;

namespace Romd.Admin.Application.MetadataProviders.Commands.UpdateIgdbSettings;

public sealed record UpdateIgdbSettingsCommand(UpdateIgdbProviderSettingsRequest Request, RevisionPrecondition Precondition) : ICommand<IgdbProviderSettings>;

public sealed class UpdateIgdbSettingsCommandHandler(IIgdbProviderSettingsService service, IUnitOfWork unitOfWork,
    ILogger<UpdateIgdbSettingsCommandHandler> logger)
    : ICommandHandler<UpdateIgdbSettingsCommand, IgdbProviderSettings>
{
    public async Task<ErrorOr<IgdbProviderSettings>> HandleAsync(UpdateIgdbSettingsCommand command, CancellationToken ct = default)
    {
        return await unitOfWork.ExecuteInTransactionAsync<IgdbProviderSettings>(
            token => service.UpdateStagedAsync(command.Request, command.Precondition, token), logger, ct);
    }
}
