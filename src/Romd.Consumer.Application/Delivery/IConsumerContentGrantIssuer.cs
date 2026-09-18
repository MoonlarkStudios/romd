using ErrorOr;
using Romd.Domain.Hashing;

namespace Romd.Consumer.Application.Delivery;

public sealed record ConsumerContentGrant(
    Guid UserId,
    int LibraryId,
    int TitleId,
    int ReleaseId,
    int RomId,
    int FileId,
    Sha256 Sha256,
    long SizeBytes);

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
