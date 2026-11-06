using ErrorOr;
using Romd.Application.Common.Content;

namespace Romd.Consumer.Application.Delivery;

/// <summary>An authorized captured derived file, with its own identity and complete retained scope.</summary>
public sealed record ConsumerDerivedDescriptorGrant(Guid UserId, int LibraryId, int TitleId, DerivedDescriptorBinding Source);
public sealed record ConsumerValidatedDerivedDescriptorGrant(ConsumerDerivedDescriptorGrant Grant, string KeyId, Guid GrantId,
    DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt);
public interface IConsumerDerivedDescriptorGrantIssuer
{
    ConsumerIssuedContentGrant Issue(ConsumerDerivedDescriptorGrant grant);
    ErrorOr<ConsumerValidatedDerivedDescriptorGrant> Validate(string token);
}
