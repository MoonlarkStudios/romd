using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Application.Common.Validation;
using Romd.Contracts.Management.ConsumerApps;

namespace Romd.Admin.Application.ConsumerApps.Commands.RegisterConsumerWebApp;

public sealed record RegisterConsumerWebAppCommand(string DisplayName, string CallbackUri, string BrowserOrigin)
    : ICommand<ConsumerWebApp>;

public sealed class RegisterConsumerWebAppCommandHandler(
    IConsumerWebAppAdministration apps,
    IUnitOfWork unitOfWork,
    ILogger<RegisterConsumerWebAppCommandHandler> logger)
    : ICommandHandler<RegisterConsumerWebAppCommand, ConsumerWebApp>
{
    public Task<ErrorOr<ConsumerWebApp>> HandleAsync(RegisterConsumerWebAppCommand command, CancellationToken ct = default)
    {
        string? name = command.DisplayName?.Trim();
        if (name is null || !ConsumerWebAppRegistration.IsValidDisplayName(name))
            return Invalid("displayName", "Enter a display name of up to 120 characters.");

        if (!ConsumerWebAppRegistration.TryReadCallback(command.CallbackUri, out _))
            return Invalid("callbackUri", "Enter the exact canonical HTTPS callback URL, without credentials or a fragment.");

        if (!ConsumerWebAppRegistration.IsValidBrowserOrigin(command.BrowserOrigin))
            return Invalid("browserOrigin", "Enter the HTTPS browser origin without a path, query, or trailing slash.");

        var registration = new ConsumerWebAppRegistration(
            $"{ConsumerWebAppRegistration.ClientIdPrefix}{Guid.NewGuid():N}", name, command.CallbackUri, command.BrowserOrigin, null, null, null);
        return unitOfWork.ExecuteInTransactionAsync<ConsumerWebApp>(
            _ =>
            {
                apps.StageRegistration(registration);
                return Task.FromResult<ErrorOr<ConsumerWebApp>>(ConsumerWebAppMapping.ToContract(registration));
            }, logger, ct);
    }

    private static Task<ErrorOr<ConsumerWebApp>> Invalid(string field, string message) =>
        Task.FromResult<ErrorOr<ConsumerWebApp>>(FieldErrors.Validation("ConsumerWebApp.InvalidRegistration", message, field));
}
