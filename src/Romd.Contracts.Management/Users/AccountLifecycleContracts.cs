using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Users;

public sealed record AccountLink(Guid Id, string Purpose, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, string Status);
public sealed record IssuedAccountLink(Guid UserId, string Token, DateTimeOffset ExpiresAt)
{
    public Guid Id { get; init; }
    public override string ToString() => nameof(IssuedAccountLink);
}
public sealed record IssueAccountLinkRequest(string Purpose);
public sealed record RedeemAccountLinkRequest(string Token, string Password)
{
    public override string ToString() => nameof(RedeemAccountLinkRequest);
}
public sealed record SetAccountSuspensionRequest(bool Suspended);
public sealed record AccountSession(string Id, string Client, DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt)
{
    public string Device { get; init; } = "";
    public DateTimeOffset LastUsedAt { get; init; }
    public string Status { get; init; } = "Current";
    public bool IsCurrent { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public Guid? RevokedBy { get; init; }
    public string? RevocationReason { get; init; }
}
public sealed record AccountSecurity(Page<AccountLink> Links, Page<AccountSession> Sessions);
public sealed record AdminAuditEvent(string Id, Guid ActorId, string? ActorEmail, DateTimeOffset OccurredAt,
    string Action, string TargetType, string TargetId, string Changes);
