using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.MetadataProviders;

namespace Romd.Admin.Application.MetadataProviders.Commands.TestIgdbConnection;

public sealed record TestIgdbConnectionCommand(RevisionPrecondition Precondition) : ICommand<IgdbProviderSettings>;

public sealed class TestIgdbConnectionCommandHandler(IIgdbProviderSettingsService service, IUnitOfWork unitOfWork,
    ILogger<TestIgdbConnectionCommandHandler> logger)
    : ICommandHandler<TestIgdbConnectionCommand, IgdbProviderSettings>
{
    public async Task<ErrorOr<IgdbProviderSettings>> HandleAsync(TestIgdbConnectionCommand command, CancellationToken ct = default)
    {
        // The provider call never holds a database transaction or a settings row lock.
        var observation = await service.ProbeAsync(command.Precondition, ct);
        if (observation.IsError)
            return observation.Errors;
        return await unitOfWork.ExecuteInTransactionAsync<IgdbProviderSettings>(
            token => service.RecordTestStagedAsync(observation.Value, token), logger, ct);
    }
}
