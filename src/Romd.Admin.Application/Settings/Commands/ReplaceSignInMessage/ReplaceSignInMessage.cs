using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Settings.Commands.ReplaceSignInMessage;

public sealed record ReplaceSignInMessageCommand(string Message, RevisionPrecondition Precondition)
    : ICommand<SignInMessageSettings>;

public sealed class ReplaceSignInMessageCommandHandler(
    ISignInMessageStore store,
    IUnitOfWork unitOfWork,
    ILogger<ReplaceSignInMessageCommandHandler> logger)
    : ICommandHandler<ReplaceSignInMessageCommand, SignInMessageSettings>
{
    public Task<ErrorOr<SignInMessageSettings>> HandleAsync(ReplaceSignInMessageCommand command, CancellationToken ct = default)
    {
        if (command.Message is null || command.Message.Length > ReplaceSignInMessageRequest.MaximumLength || command.Message.Contains('\0'))
        {
            return Task.FromResult<ErrorOr<SignInMessageSettings>>(Error.Validation(
                "SignInMessage.InvalidMessage",
                $"Use at most {ReplaceSignInMessageRequest.MaximumLength} characters of text and omit null characters."));
        }

        string message = command.Message.Trim();
        return unitOfWork.ExecuteInTransactionAsync(
            token => store.StageReplacementAsync(message, command.Precondition, token), logger, ct);
    }
}
