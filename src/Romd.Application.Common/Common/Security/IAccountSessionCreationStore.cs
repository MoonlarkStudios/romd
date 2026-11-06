namespace Romd.Application.Common.Security;

/// <summary>Captured authentication facts; account-stamp eligibility remains the later validation authority.</summary>
public sealed record AccountSessionCreationFacts(Guid Id, Guid UserId, string ClientId, string Device,
    string AccountStamp, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>Stages one session inside the caller's transaction; never flushes or commits.</summary>
public interface IAccountSessionCreationStore
{
    Task<AccountSession> StageStartAsync(AccountSessionCreationFacts facts, CancellationToken ct = default);
}
