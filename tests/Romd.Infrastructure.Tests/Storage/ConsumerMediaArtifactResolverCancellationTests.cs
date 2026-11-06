using NSubstitute;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ConsumerMediaArtifactResolverCancellationTests
{
    [Fact]
    public async Task ResolveMediaAsync_MetadataReadCancelled_PropagatesCancellationWithoutCasRead()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var metadata = Substitute.For<IConsumerMediaMetadataReader>();
        metadata.FindAsync(5, cancellation.Token)
            .Returns(Task.FromCanceled<ConsumerMediaMetadata?>(cancellation.Token));
        var cas = Substitute.For<IContentAddressableStore>();

        var exception = await Should.ThrowAsync<OperationCanceledException>(() =>
            new ConsumerMediaArtifactResolver(metadata, cas)
                .ResolveMediaAsync(new ConsumerMediaArtifactRequest(5), cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
        await metadata.Received(1).FindAsync(5, cancellation.Token);
        cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveMediaAsync_ContentReadCancelled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var hash = Sha256.Parse(new string('a', 64));
        var metadata = Substitute.For<IConsumerMediaMetadataReader>();
        metadata.FindAsync(5, cancellation.Token).Returns(new ConsumerMediaMetadata(hash, "image/png"));
        var cas = Substitute.For<IContentAddressableStore>();
        cas.RetrieveAsync(StorageKey.FromHash(hash), cancellation.Token)
            .Returns(Task.FromCanceled<Stream?>(cancellation.Token));

        var exception = await Should.ThrowAsync<OperationCanceledException>(() =>
            new ConsumerMediaArtifactResolver(metadata, cas)
                .ResolveMediaAsync(new ConsumerMediaArtifactRequest(5), cancellation.Token));

        exception.CancellationToken.ShouldBe(cancellation.Token);
        await cas.Received(1).RetrieveAsync(StorageKey.FromHash(hash), cancellation.Token);
    }
}
