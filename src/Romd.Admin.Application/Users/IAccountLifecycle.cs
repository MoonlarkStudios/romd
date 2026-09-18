using ErrorOr;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users;

public interface IAccountLifecycle
{
    Task<ErrorOr<AccountSecurityDto>> GetSecurityAsync(Guid userId, CancellationToken ct);
    Task<ErrorOr<IssuedAccountLinkDto>> IssueLinkAsync(Guid userId, string purpose, CancellationToken ct);
    Task<ErrorOr<Success>> RevokeLinkAsync(Guid userId, Guid linkId, CancellationToken ct);
    Task<ErrorOr<Success>> RedeemLinkAsync(string token, string password, CancellationToken ct);
    Task<ErrorOr<Success>> SetSuspensionAsync(Guid userId, bool suspended, CancellationToken ct);
    Task<ErrorOr<Success>> RevokeSessionsAsync(Guid userId, string? sessionId, CancellationToken ct, Guid? exceptSessionId = null);
}
