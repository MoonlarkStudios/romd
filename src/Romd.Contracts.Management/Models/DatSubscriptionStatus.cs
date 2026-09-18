namespace Romd.Contracts.Management.Models;

public sealed record DatSubscriptionStatus(bool Available, bool Subscribed, string State,
    DateTimeOffset? LastCheckedAt, string? Message, string? CandidateSha256, Guid? JobId);

public sealed record ApplyDatSubscription(string ActiveSha256, string CandidateSha256);
