using ErrorOr;
using Romd.Application.Common.Cqrs;

namespace Romd.Application.Common.Security.Commands.StartAccountSession;

/// <summary>Persists a sign-in's captured identity before the host binds a cookie or issues protocol credentials.</summary>
public sealed record StartAccountSessionCommand(Guid UserId, string ClientId, string Device, string AccountStamp) : ICommand<AccountSession>;

public sealed class StartAccountSessionCommandHandler(IAccountSessionCreationStore creations,
    IAccountSessionTransaction transaction, TimeProvider clock) : ICommandHandler<StartAccountSessionCommand, AccountSession>
{
    public Task<ErrorOr<AccountSession>> HandleAsync(StartAccountSessionCommand command, CancellationToken ct = default) =>
        transaction.ExecuteAsync(async token =>
        {
            var now = clock.GetUtcNow();
            var facts = new AccountSessionCreationFacts(Guid.NewGuid(), command.UserId, command.ClientId,
                AccountSessionDeviceLabel.Clean(command.Device), command.AccountStamp, now, now.AddDays(30));
            // Preserve the captured stamp: current eligibility is checked by the existing session validation path.
            return await creations.StageStartAsync(facts, token);
        }, ct);
}
