using Romd.Contracts.Common.Models;
using ErrorOr;
using Romd.Contracts.Management.Users;

namespace Romd.Admin.Application.Users;

public interface IAccountLifecycle
{
    Task<ErrorOr<Page<AccountSession>>> GetSessionsAsync(Guid userId, string? cursor, int limit, Guid? currentSessionId, string scope, CancellationToken ct);
    Task<ErrorOr<Page<AccountLink>>> GetLinksAsync(Guid userId, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<AccountSecurity>> GetSecurityAsync(Guid userId, CancellationToken ct);
    Task<ErrorOr<IssuedAccountLink>> IssueLinkAsync(Guid userId, string purpose, CancellationToken ct);
    Task<ErrorOr<Success>> RevokeLinkAsync(Guid userId, Guid linkId, CancellationToken ct);
    Task<ErrorOr<Success>> RedeemLinkAsync(string token, string password, CancellationToken ct);
    Task<ErrorOr<Success>> SetSuspensionAsync(Guid userId, bool suspended, CancellationToken ct);
    Task<ErrorOr<Success>> RevokeSessionsAsync(Guid userId, string? sessionId, CancellationToken ct, Guid? exceptSessionId = null);
}
