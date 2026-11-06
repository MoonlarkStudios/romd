using System.Buffers.Binary;
using System.IO.Abstractions;
using System.Security.Cryptography;
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
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomIngestAdmissionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-precopy-").FullName;
    private readonly IRomCatalogMatchReader _catalog = Substitute.For<IRomCatalogMatchReader>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestRom_NintendoIsoInspectionOccupied_ReadsOnlyAdaptivePrefixWithoutStaging(bool wii)
    {
        using var verifier = Verifier();
        using var occupied = await verifier.AcquireAsync(CancellationToken.None);
        using var source = new TrackedInput(wii ? SyntheticNintendoIso.Wii(0) : SyntheticNintendoIso.GameCube());

        await Should.ThrowAsync<ResourceCapacityException>(() => Handler(verifier).HandleAsync(new(source, "disc.iso")));

        source.BytesRead.ShouldBe(32);
        source.CanRead.ShouldBeTrue();
        Payloads().ShouldBeEmpty();
        File.Exists(Path.Combine(_root, "temp", "staging-v1", "reservations.json")).ShouldBeFalse();
        await _catalog.DidNotReceiveWithAnyArgs().ReadAsync(default, default);
    }

    [Fact]
    public async Task IngestRom_OrdinaryIsoAboveMediaLimitWithOccupiedMediaSlot_StillHashesAndMatchesDat()
    {
        byte[] bytes = Enumerable.Repeat((byte)0x5A, 128).ToArray();
        using var verifier = new RvzVerifier(new() { MaxConcurrent = 1, AdmissionTimeoutSeconds = 1, MaxArtifactBytes = 64 });
        using var occupied = await verifier.AcquireAsync(CancellationToken.None);
        _catalog.ReadAsync(Arg.Any<Sha1>(), Arg.Any<CancellationToken>()).Returns(new RomCatalogMatch([], [], []));
        using var source = new TrackedInput(bytes);

        var result = await Handler(verifier).HandleAsync(new(source, "ordinary.iso"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Library.UnidentifiedRom");
        await _catalog.Received(1).ReadAsync(Sha1.FromSpan(SHA1.HashData(bytes)), Arg.Any<CancellationToken>());
        source.BytesRead.ShouldBe(bytes.Length);
        source.CanRead.ShouldBeTrue();
        Payloads().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestRom_InspectionOccupied_ReadsOnlyDetectionPrefixWithoutStaging(bool cancel)
    {
        using var verifier = Verifier();
        using var occupied = await verifier.AcquireAsync(default);
        using var source = new TrackedInput(RvzBytes(1 << 20));
        using var cancellation = new CancellationTokenSource();
        if (cancel)
            cancellation.CancelAfter(100);
        Task running = Handler(verifier).HandleAsync(new(source, "disc.bin"), cancellation.Token);
        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(() => running);
        else
            await Should.ThrowAsync<ResourceCapacityException>(() => running);
        source.BytesRead.ShouldBe(4);
        source.CanRead.ShouldBeTrue();
        Payloads().ShouldBeEmpty();
        File.Exists(Path.Combine(_root, "temp", "staging-v1", "reservations.json")).ShouldBeFalse();
        await _catalog.DidNotReceiveWithAnyArgs().ReadAsync(default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestRom_StagingFailsOrIsCancelled_ReleasesInspectionReservation(bool cancel)
    {
        using var verifier = Verifier();
        using var cancellation = new CancellationTokenSource();
        using var source = new TrackedInput(RvzBytes(100), onBody: () =>
        {
            if (cancel)
                cancellation.Cancel();
            else
                throw new IOException("fixture source interrupted");
        });
        Task running = Handler(verifier).HandleAsync(new(source, "disc.rvz"), cancellation.Token);
        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(() => running);
        else
            await Should.ThrowAsync<IOException>(() => running);
        Payloads().ShouldBeEmpty();
        using var recovered = await verifier.AcquireAsync(default);
        await Should.ThrowAsync<ResourceCapacityException>(() => verifier.AcquireAsync(default));
    }

    [Fact]
    public async Task IngestRom_InvalidRvzPrefix_IsAFileFailureEvenWhenInspectionIsOccupied()
    {
        using var verifier = Verifier();
        using var occupied = await verifier.AcquireAsync(default);
        using var source = new TrackedInput([1, 2, 3, 4, 5, 6]);
        var result = await Handler(verifier).HandleAsync(new(source, "disc.rvz"));
        result.IsError.ShouldBeTrue();
        result.FirstError.Description.ShouldContain("Invalid RVZ header");
        source.BytesRead.ShouldBe(4);
        Payloads().ShouldBeEmpty();
    }

    [Fact]
    public async Task IngestRom_AdmittedRvz_CopiesOnceAndReleasesSlotBeforeCatalogPublication()
    {
        var disc = new SyntheticRvzBuilder(0x100000, 0x20000).Build();
        using var verifier = Verifier();
        _catalog.ReadAsync(Arg.Any<Sha1>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            call.Arg<Sha1>().ShouldBe(Sha1.FromSpan(SHA1.HashData(disc.ExpectedDisc)));
            using var reusable = await verifier.AcquireAsync(call.Arg<CancellationToken>());
            return new RomCatalogMatch([], [], []);
        });
        using var source = new TrackedInput(disc.File);
        var result = await Handler(verifier).HandleAsync(new(source, "disc.bin"));
        result.IsError.ShouldBeTrue(); // No DAT match; inspection completes before this decision.
        result.FirstError.Code.ShouldBe("Library.UnidentifiedRom");
        source.BytesRead.ShouldBe(disc.File.Length);
        source.CanRead.ShouldBeTrue();
        Payloads().ShouldBeEmpty();
        using var final = await verifier.AcquireAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestRom_AdmittedOriginal_CopiesOnceAndReleasesBeforeCatalog(bool wii)
    {
        using var fixture = new MediaWorkerTestFixture();
        using var verifier = fixture.Verifier();
        byte[] bytes = wii ? SyntheticNintendoIso.Wii(0) : SyntheticNintendoIso.GameCube();
        _catalog.ReadAsync(Arg.Any<Sha1>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            call.Arg<Sha1>().ShouldBe(Sha1.FromSpan(SHA1.HashData(bytes)));
            using var releasedBeforeCatalog = await verifier.AcquireAsync(call.Arg<CancellationToken>());
            return new RomCatalogMatch([], [], []);
        });
        using var source = new TrackedInput(bytes);
        var result = await Handler(verifier).HandleAsync(new(source, "disc.iso"));
        result.FirstError.Code.ShouldBe("Library.UnidentifiedRom");
        source.BytesRead.ShouldBe(bytes.Length);
        source.CanRead.ShouldBeTrue();
        Payloads().ShouldBeEmpty();
        await _catalog.Received(1).ReadAsync(Sha1.FromSpan(SHA1.HashData(bytes)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestRom_OriginalStagingFailsOrCancels_ReleasesUnusedAdmission(bool cancel)
    {
        using var verifier = Verifier();
        using var cancellation = new CancellationTokenSource();
        using var source = new TrackedInput(SyntheticNintendoIso.GameCube(), onBody: () =>
        {
            if (cancel)
                cancellation.Cancel();
            else
                throw new IOException("fixture original interrupted");
        }, bodyAfter: 32);
        Task running = Handler(verifier).HandleAsync(new(source, "disc.iso"), cancellation.Token);
        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(() => running);
        else
            await Should.ThrowAsync<IOException>(() => running);
        Payloads().ShouldBeEmpty();
        await _catalog.DidNotReceiveWithAnyArgs().ReadAsync(default, default);
        using var released = await verifier.AcquireAsync(default);
    }

    private IngestRomCommandHandler Handler(RvzVerifier verifier)
    {
        var options = Substitute.For<IRomdOptions>();
        options.DataDirectory.Returns(_root);
        options.MaxUploadBytes.Returns(8L << 20);
        var factory = new LocalTempFileFactory(options, new FileSystem(), new TempStorageOptions
        {
            MaximumBytes = 16L << 20,
            MaximumFiles = 2,
            MinimumFreeBytes = 0,
            AdmissionTimeoutSeconds = 1
        });
        var roms = Substitute.For<IRomRepository>();
        var audit = Substitute.For<IAuditContext>();
        audit.ActorId.Returns(Romd.Domain.Identity.SystemActor.UserId);
        audit.IsSystem.Returns(true);
        var publisher = new RomRepresentationPublisher(Substitute.For<IFileStorageService>(), Substitute.For<IUnitOfWork>(), roms,
            TestRomMediaPolicies.Create(), RomEncodingComposition.CreateRegistry(), Substitute.For<IFileMutationLock>(), null,
            Substitute.For<IMediaFactPublisher>(), Substitute.For<IRomMediaObservationStore>(), Substitute.For<IRomMediaCreationStore>(),
            audit, TimeProvider.System, Substitute.For<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationRequests>(), Substitute.For<IRomPs2CurrentReferenceAuthority>());
        return new(roms, Substitute.For<IDatRepository>(), _catalog, factory, MediaInfrastructureTestServices.ManagedPreparation(new HashingService(), () => verifier, new MediaInfrastructureTestServices.EmptyReferences()), Substitute.For<Romd.Admin.Application.Source.Rom.Compressed.IRomCdIngestor>(), publisher,
            Substitute.For<IAdminEventOutbox>(), Substitute.For<ILibraryRepository>(),
            Substitute.For<ITrackedTitleReader>(), Substitute.For<ITrackedTitleRepository>(),
            Substitute.For<IRomPayloadAssertionImpactReader>(), Substitute.For<ITitlePayloadAvailabilityProjection>(), NullLogger<IngestRomCommandHandler>.Instance, TimeProvider.System);
    }

    private static RvzVerifier Verifier() => new(new MediaProcessingOptions
    {
        MaxConcurrent = 1,
        AdmissionTimeoutSeconds = 1,
        WallTimeoutSeconds = 10
    });
    private static byte[] RvzBytes(int length)
    {
        byte[] bytes = new byte[length];
        "RVZ\u0001"u8.CopyTo(bytes);
        if (length >= 0x58)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x54), 32768);
        return bytes;
    }
    private string[] Payloads() => Directory.GetFiles(_root, "*.data", SearchOption.AllDirectories);
    public void Dispose() => Directory.Delete(_root, true);

    private sealed class TrackedInput(byte[] bytes, Action? onBody = null, int bodyAfter = 4) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (BytesRead >= bodyAfter)
                onBody?.Invoke();
            ct.ThrowIfCancellationRequested();
            int count = await base.ReadAsync(buffer, ct);
            BytesRead += count;
            return count;
        }
    }
}
