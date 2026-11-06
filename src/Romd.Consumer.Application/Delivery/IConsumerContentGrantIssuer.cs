using ErrorOr;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Consumer.Application.Delivery;

public sealed record ConsumerContentGrant(Guid UserId, int LibraryId, int TitleId, int ReleaseId, RomDeliveryTarget Target);

public sealed record ConsumerIssuedContentGrant(
    string DownloadUrl,
    DateTimeOffset ExpiresAt);

public sealed record ConsumerValidatedContentGrant(
    ConsumerContentGrant Grant,
    string KeyId,
    Guid GrantId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public interface IConsumerContentGrantIssuer
{
    ConsumerIssuedContentGrant IssueDownloadGrant(ConsumerContentGrant grant);

    ErrorOr<ConsumerValidatedContentGrant> ValidateDownloadGrant(string token);
}
