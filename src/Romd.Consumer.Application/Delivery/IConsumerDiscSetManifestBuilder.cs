using ErrorOr;
using Romd.Consumer.Application.Delivery.Commands.IssueConsumerDiscSetManifest;
using Romd.Contracts.Consumer.Releases;

namespace Romd.Consumer.Application.Delivery;

/// <summary>Builds from an already authorized, consistent all-member plan; does not perform a second policy read.</summary>
public interface IConsumerDiscSetManifestBuilder
{
    Task<ErrorOr<ConsumerDiscSetManifest>> BuildAsync(IssueConsumerDiscSetManifestCommand command,
        ConsumerDiscSetPlan plan, int libraryId, Guid userId, CancellationToken ct = default);
}
