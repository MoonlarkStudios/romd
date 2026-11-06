using NSubstitute;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.Nintendo;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RvzDiscFamilyAdapterTests
{
    [Theory]
    [InlineData(RomIsoDiscKind.GameCube, NintendoDiscKind.GameCube)]
    [InlineData(RomIsoDiscKind.Wii, NintendoDiscKind.Wii)]
    public void For_KnownKind_MapsBothBoundariesToTheSameFamily(RomIsoDiscKind domain, NintendoDiscKind media)
    {
        var family = RvzDiscFamily.For(domain).ShouldNotBeNull();
        RvzDiscFamily.For(media).ShouldBeSameAs(family);
        family.DiscKind.ShouldBe(domain);
        family.Codec.DiscKind.ShouldBe(media);
    }

    [Theory]
    [InlineData(RomIsoDiscKind.PlayStation2Dvd)]
    [InlineData(RomIsoDiscKind.PlayStationPortableUmd)]
    [InlineData((RomIsoDiscKind)99)]
    public void For_OtherKind_DoesNotBorrowNintendoBehavior(RomIsoDiscKind kind) => RvzDiscFamily.For(kind).ShouldBeNull();

    [Fact]
    public void For_AbsentOrUnknownMediaKind_ReturnsNoFamily()
    {
        RvzDiscFamily.For((RomIsoDiscKind?)null).ShouldBeNull();
        RvzDiscFamily.For((NintendoDiscKind?)null).ShouldBeNull();
        RvzDiscFamily.For((NintendoDiscKind)99).ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodeAsync_UnknownFamily_NeverInvokesSupervisorAndPreservesCancellation(bool cancel)
    {
        var supervisor = Substitute.For<IMediaWorkerSupervisor>();
        using var verifier = new RvzVerifier(new(), supervisor);
        var source = Substitute.For<ITempFile>();
        var output = Substitute.For<IWritableTempFile>();
        using var cancellation = new CancellationTokenSource();
        using (var admission = await verifier.AcquireAsync(CancellationToken.None))
        {
            if (cancel)
            {
                await cancellation.CancelAsync();
                var error = await Should.ThrowAsync<OperationCanceledException>(() =>
                    admission.EncodeAsync(source, output, 131072, (RomIsoDiscKind)99, cancellation.Token));
                error.CancellationToken.ShouldBe(cancellation.Token);
            }
            else
                (await admission.EncodeAsync(source, output, 131072, (RomIsoDiscKind)99, CancellationToken.None))
                    .FirstError.Code.ShouldBe("Rom.Media.Unsupported");
        }
        supervisor.ReceivedCalls().ShouldBeEmpty();
        source.DidNotReceive().Quarantine();
        output.DidNotReceive().Quarantine();
        using var recovered = await verifier.AcquireAsync(CancellationToken.None);
    }
}
