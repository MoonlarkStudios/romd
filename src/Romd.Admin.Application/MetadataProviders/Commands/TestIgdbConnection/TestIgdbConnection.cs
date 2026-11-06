using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.MetadataProviders;

namespace Romd.Admin.Application.MetadataProviders.Commands.TestIgdbConnection;

public sealed record TestIgdbConnectionCommand(RevisionPrecondition Precondition) : ICommand<IgdbProviderSettingsDto>;

public sealed class TestIgdbConnectionCommandHandler(IIgdbProviderSettingsService service, IUnitOfWork unitOfWork,
    ILogger<TestIgdbConnectionCommandHandler> logger)
    : ICommandHandler<TestIgdbConnectionCommand, IgdbProviderSettingsDto>
{
    public async Task<ErrorOr<IgdbProviderSettingsDto>> HandleAsync(TestIgdbConnectionCommand command, CancellationToken ct = default)
    {
        // The provider call never holds a database transaction or a settings row lock.
        var observation = await service.ProbeAsync(command.Precondition, ct);
        if (observation.IsError)
            return observation.Errors;
        return await unitOfWork.ExecuteInTransactionAsync<IgdbProviderSettingsDto>(
            token => service.RecordTestStagedAsync(observation.Value, token), logger, ct);
    }
}
