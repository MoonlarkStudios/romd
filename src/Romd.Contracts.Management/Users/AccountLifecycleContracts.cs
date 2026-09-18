namespace Romd.Contracts.Management.Users;

public sealed record AccountLinkDto(Guid Id, string Purpose, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, string Status);
public sealed record IssuedAccountLinkDto(Guid UserId, string Token, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(IssuedAccountLinkDto);
}
public sealed record IssueAccountLinkRequest(string Purpose);
public sealed record RedeemAccountLinkRequest(string Token, string Password)
{
    public override string ToString() => nameof(RedeemAccountLinkRequest);
}
public sealed record SetAccountSuspensionRequest(bool Suspended);
public sealed record AccountSessionDto(string Id, string Client, DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt)
{
    public string Device { get; init; } = "";
    public DateTimeOffset LastUsedAt { get; init; }
    public string Status { get; init; } = "Current";
    public bool IsCurrent { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public Guid? RevokedBy { get; init; }
    public string? RevocationReason { get; init; }
}
public sealed record AccountSecurityDto(IReadOnlyList<AccountLinkDto> Links, IReadOnlyList<AccountSessionDto> Sessions);
public sealed record AdminAuditEventDto(string Id, Guid ActorId, string? ActorEmail, DateTimeOffset OccurredAt,
    string Action, string TargetType, string TargetId, string Changes);
public sealed record AdminAuditPageDto(IReadOnlyList<AdminAuditEventDto> Items, string? NextCursor);

public sealed record UserDirectoryPageDto(IReadOnlyList<UserDto> Items, string? NextCursor);
