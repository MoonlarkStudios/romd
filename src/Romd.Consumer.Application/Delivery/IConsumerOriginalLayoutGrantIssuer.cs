using ErrorOr;
using Romd.Domain.Hashing;

namespace Romd.Consumer.Application.Delivery;

/// <summary>Separate retained descriptor capability; never a synthetic catalog ROM grant.</summary>
public sealed record ConsumerOriginalLayoutGrant(Guid UserId, int LibraryId, int TitleId, int ReleaseId,
    int LayoutId, Guid LayoutRevision, int FileId, Sha256 Sha256, long SizeBytes, string Filename);
public sealed record ConsumerValidatedOriginalLayoutGrant(ConsumerOriginalLayoutGrant Grant, DateTimeOffset ExpiresAt);
public interface IConsumerOriginalLayoutGrantIssuer
{
    ConsumerIssuedContentGrant Issue(ConsumerOriginalLayoutGrant grant);
    ErrorOr<ConsumerValidatedOriginalLayoutGrant> Validate(string token);
}
