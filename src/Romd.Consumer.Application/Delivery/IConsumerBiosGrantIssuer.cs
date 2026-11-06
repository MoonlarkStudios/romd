using ErrorOr;
using Romd.Domain.Hashing;

namespace Romd.Consumer.Application.Delivery;

public sealed record ConsumerBiosGrant(
    Guid UserId,
    int LibraryId,
    int BiosId,
    int FileId,
    Sha256 Sha256,
    long SizeBytes);

public sealed record ConsumerValidatedBiosGrant(
    ConsumerBiosGrant Grant,
    string KeyId,
    Guid GrantId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public interface IConsumerBiosGrantIssuer
{
    ConsumerIssuedContentGrant IssueBiosDownloadGrant(ConsumerBiosGrant grant);

    ErrorOr<ConsumerValidatedBiosGrant> ValidateBiosDownloadGrant(string token);
}
