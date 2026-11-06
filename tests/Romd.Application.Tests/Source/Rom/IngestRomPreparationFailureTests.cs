using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Security;
using Romd.Admin.Application.Titles;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class IngestRomPreparationFailureTests
{
    [Theory]
    [InlineData("Rom.Media.Protocol", false)]
    [InlineData("Rom.Media.Crash", false)]
    [InlineData("Rom.Media.LimitExceeded", false)]
    [InlineData("Rom.Media.WallDeadline", false)]
    [InlineData("Rom.Media.Containment", false)]
    [InlineData("Rom.Media.SourceCustody", false)]
    [InlineData("Rom.Media.Protocol", true)]
    public async Task HandleAsync_AdmittedMediaFailure_ReturnsExactErrorBeforeCatalogOrPublication(string code, bool adaptivePrefix)
    {
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        var temp = Substitute.For<ITempFile>();
        var tempFiles = Substitute.For<ITempFileFactory>();
        Func<ReadOnlyMemory<byte>, int>? observedSelector = null;
        tempFiles.CreateAsync(Arg.Any<Stream>(), ct, Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(),
                Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>(), Arg.Any<Func<ReadOnlyMemory<byte>, int>?>())
            .Returns(call => { observedSelector = call.ArgAt<Func<ReadOnlyMemory<byte>, int>>(4); return temp; });
        var preparation = Substitute.For<IRomPreparationService>();
        preparation.SelectPrefixLength(Arg.Any<ReadOnlyMemory<byte>>(), "disc.rvz").Returns(adaptivePrefix ? 32 : 4);
        var error = Error.Validation(code, "Permanent inspection failure.");
        preparation.PrepareAsync(temp, "disc.rvz", ct).Returns(error);
        var roms = Substitute.For<IRomRepository>();
        var catalog = Substitute.For<IRomCatalogMatchReader>();
        var storage = Substitute.For<IFileStorageService>();
        var work = Substitute.For<IUnitOfWork>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var locks = Substitute.For<IFileMutationLock>();
        var audit = Substitute.For<IAuditContext>();
        audit.ActorId.Returns(Romd.Domain.Identity.SystemActor.UserId);
        audit.IsSystem.Returns(true);
        var publisher = new RomRepresentationPublisher(storage, work, roms, TestRomMediaPolicies.Create(), TestRomEncodings.Create(), locks, null,
            Substitute.For<IMediaFactPublisher>(), Substitute.For<IRomMediaObservationStore>(), Substitute.For<IRomMediaCreationStore>(), audit, TimeProvider.System, Substitute.For<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationRequests>(), Substitute.For<IRomPs2CurrentReferenceAuthority>());
        var handler = new IngestRomCommandHandler(roms, Substitute.For<IDatRepository>(), catalog, tempFiles, preparation, Substitute.For<Romd.Admin.Application.Source.Rom.Compressed.IRomCdIngestor>(), publisher, outbox, Substitute.For<ILibraryRepository>(), Substitute.For<ITrackedTitleReader>(),
            Substitute.For<ITrackedTitleRepository>(),
            Substitute.For<IRomPayloadAssertionImpactReader>(), Substitute.For<ITitlePayloadAvailabilityProjection>(),
            NullLogger<IngestRomCommandHandler>.Instance, clock: TimeProvider.System);

        using var input = new MemoryStream("RVZ\u0001"u8.ToArray());
        var result = await handler.HandleAsync(new(input, "disc.rvz", AllowUnidentified: true), ct);

        result.IsError.ShouldBeTrue();
        result.Errors.ShouldBe(new[] { error });
        roms.ReceivedCalls().ShouldBeEmpty();
        catalog.ReceivedCalls().ShouldBeEmpty();
        storage.ReceivedCalls().ShouldBeEmpty();
        work.ReceivedCalls().ShouldBeEmpty();
        outbox.ReceivedCalls().ShouldBeEmpty();
        locks.ReceivedCalls().ShouldBeEmpty();
        await preparation.Received(1).PrepareAsync(temp, "disc.rvz", ct);
        await tempFiles.Received(1).CreateAsync(input, ct, Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(),
            Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>(),
            Arg.Is<Func<ReadOnlyMemory<byte>, int>?>(value => ReferenceEquals(value, observedSelector)));
        observedSelector.ShouldNotBeNull()("RVZ\u0001"u8.ToArray()).ShouldBe(adaptivePrefix ? 32 : 4);
        preparation.Received(1).SelectPrefixLength(Arg.Any<ReadOnlyMemory<byte>>(), "disc.rvz");
        if (code == "Rom.Media.Containment")
            temp.Received(1).Quarantine();
        else
            temp.DidNotReceive().Quarantine();
        await temp.Received(1).DisposeAsync();
    }
}
