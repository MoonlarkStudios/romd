namespace Romd.Contracts.Consumer.Account;

public sealed record ConsumerSignInSession(
    Guid Id,
    string Device,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    string Status,
    bool IsCurrent);
