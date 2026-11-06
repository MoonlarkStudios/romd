using Microsoft.Extensions.Logging.Abstractions;
using Romd.Application.Common.Security;
using Romd.Application.Common.Security.Commands.StartAccountSession;
using Romd.Persistence;
using Romd.Persistence.Identity;

namespace Romd.Infrastructure.Tests.Helpers;

internal static class AccountSessionCreationTestExtensions
{
    // Fixture setup uses the same application-owned commit as both authentication hosts.
    internal static async Task<AccountSession> StartCommittedAsync(this AccountSessionStore store, RomdDbContext context,
        TimeProvider clock, Guid userId, string clientId, string device, string stamp, CancellationToken ct = default)
    {
        var handler = new StartAccountSessionCommandHandler(store,
            new AccountSessionTransaction(new EfUnitOfWork(context), NullLogger<AccountSessionTransaction>.Instance), clock);
        return (await handler.HandleAsync(new(userId, clientId, device, stamp), ct)).Value;
    }
}
