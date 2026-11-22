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
        if (string.IsNullOrEmpty(name) || name.Length > 120 || name.Any(character => char.IsControl(character) ||
            char.GetUnicodeCategory(character) is System.Globalization.UnicodeCategory.Format))
            return Invalid("displayName", "Enter a display name of up to 120 characters.");

        if (!TryReadHttpsUri(command.CallbackUri, 2048, out var callback) || callback.AbsoluteUri != command.CallbackUri)
            return Invalid("callbackUri", "Enter the exact canonical HTTPS callback URL, without credentials or a fragment.");

        if (!TryReadHttpsUri(command.BrowserOrigin, 255, out var origin)
            || origin.GetLeftPart(UriPartial.Authority) != command.BrowserOrigin)
            return Invalid("browserOrigin", "Enter the HTTPS browser origin without a path, query, or trailing slash.");

        var registration = new ConsumerWebAppRegistration(
            $"{ConsumerWebAppRegistration.ClientIdPrefix}{Guid.NewGuid():N}", name, command.CallbackUri, command.BrowserOrigin, null, null);
        return unitOfWork.ExecuteInTransactionAsync<ConsumerWebApp>(
            _ =>
            {
                apps.StageRegistration(registration);
                return Task.FromResult<ErrorOr<ConsumerWebApp>>(ConsumerWebAppMapping.ToContract(registration));
            }, logger, ct);
    }

    private static bool TryReadHttpsUri(string? value, int maximumLength, out Uri uri)
    {
        if (value is { Length: > 0 } && value.Length <= maximumLength
            && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            && value.IndexOfAny(['\\', '*', '#']) < 0
            && Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.IsWellFormedOriginalString()
            && parsed.Scheme == Uri.UriSchemeHttps && parsed.HostNameType is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
            && parsed.UserInfo.Length == 0 && parsed.Host.All(char.IsAscii))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static Task<ErrorOr<ConsumerWebApp>> Invalid(string field, string message) =>
        Task.FromResult<ErrorOr<ConsumerWebApp>>(FieldErrors.Validation("ConsumerWebApp.InvalidRegistration", message, field));
}
