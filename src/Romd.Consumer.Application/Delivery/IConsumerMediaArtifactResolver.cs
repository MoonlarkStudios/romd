namespace Romd.Consumer.Application.Delivery;

public sealed record ConsumerMediaArtifactRequest(int MediaId);

public sealed record ConsumerMediaArtifact(
    Stream Content,
    string ContentType);

public interface IConsumerMediaArtifactResolver
{
    Task<ConsumerMediaArtifact?> ResolveMediaAsync(
        ConsumerMediaArtifactRequest request,
        CancellationToken ct = default);
}
