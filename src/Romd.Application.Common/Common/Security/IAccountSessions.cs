namespace Romd.Application.Common.Security;

public static class SessionClaims
{
    public const string Id = "romd_session_id";
    public const string AccountStamp = "romd_account_stamp";
}

public sealed record AccountSession(Guid Id, Guid UserId, string ClientId, string Device,
    DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt, Guid? RevokedBy, string? RevocationReason, string Status);

/// <summary>Shared authentication session authority; token rotation never creates a session.</summary>
public interface IAccountSessions
{
    Task<AccountSession> StartAsync(Guid userId, string clientId, string device, string accountStamp, CancellationToken ct);
    Task<AccountSession?> ValidateAsync(Guid userId, Guid sessionId, string accountStamp, CancellationToken ct);
    Task<IReadOnlyList<AccountSession>> ListAsync(Guid userId, CancellationToken ct);
    Task<bool> RevokeAsync(Guid userId, Guid? sessionId, Guid? exceptSessionId, Guid actorId, string reason, CancellationToken ct);
    Task SetDeviceAsync(Guid userId, Guid sessionId, string device, CancellationToken ct);
}
