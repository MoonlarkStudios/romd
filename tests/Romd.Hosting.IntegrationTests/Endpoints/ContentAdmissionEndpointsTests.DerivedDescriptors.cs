using Microsoft.AspNetCore.Http;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Host.Endpoints;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed partial class ContentAdmissionEndpointsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DerivedDescriptor_ReadFailureOrCancellation_DisposesSourceBeforeHeaders(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        if (cancel)
            cancellation.Cancel();
        var identity = MediaFactFixtures.Identity("generated descriptor"u8);
        var captured = new DerivedDescriptorBinding(7, Guid.NewGuid(), 11, 13, "disc.cue", 17, 1, 19, identity, "fixture-generator/v1");
        var issuer = Substitute.For<IConsumerDerivedDescriptorGrantIssuer>();
        issuer.Validate("token").Returns(new ConsumerValidatedDerivedDescriptorGrant(new(Guid.NewGuid(), 2, 3, captured),
            "fixture", Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)));
        var retained = Substitute.For<IConsumerDerivedDescriptorSource>();
        retained.IsRetainedAsync(captured, cancellation.Token).Returns(true);
        var artifacts = Substitute.For<IConsumerContentArtifactResolver>();
        Exception failure = cancel ? new OperationCanceledException(cancellation.Token) : new IOException("injected descriptor read failure");
        var stream = new DescriptorReadFailureStream(failure);
        artifacts.ResolveAsync(identity.Sha256, cancellation.Token).Returns(Task.FromResult<Stream?>(stream));
        var context = new DefaultHttpContext();
        Task<IResult> Read() => Invoke(typeof(ConsumerStorageEndpoints), "RedeemDerivedDescriptorGrant", "token", context, issuer,
            retained, artifacts, cancellation.Token);
        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(Read);
        else
            await Should.ThrowAsync<IOException>(Read);
        stream.WasDisposed.ShouldBeTrue();
        context.Response.HasStarted.ShouldBeFalse();
        context.Response.ContentLength.ShouldBeNull();
        context.Response.Headers.ContentDisposition.ToString().ShouldBeEmpty();
        await retained.Received(1).IsRetainedAsync(captured, cancellation.Token);
        await artifacts.Received(1).ResolveAsync(identity.Sha256, cancellation.Token);
    }
    private sealed class DescriptorReadFailureStream(Exception failure) : MemoryStream
    {
        public bool WasDisposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromException<int>(failure);
        public override ValueTask DisposeAsync() { WasDisposed = true; return base.DisposeAsync(); }
    }
}
