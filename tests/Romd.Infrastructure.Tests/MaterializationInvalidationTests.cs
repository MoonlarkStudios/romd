using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.AssignPlatform;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Commands.DeleteRom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Commands.MoveGame;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Romd.Domain.Source.Platform;
using Romd.Domain.Source.Rom;
using Romd.Domain.Storage;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed class MaterializationInvalidationTests
{
    private static readonly Sha1 TestSha1 = Sha1.Parse("1111111111111111111111111111111111111111");
    private static readonly Md5 TestMd5 = Md5.Parse("22222222222222222222222222222222");
    private static readonly Crc32 TestCrc32 = Crc32.Parse("33333333");
    private static readonly Sha256 TestSha256 =
        Sha256.Parse("4444444444444444444444444444444444444444444444444444444444444444");

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IngestRomCommandHandler_TrackedOnlyWithoutTrackedMatch_SkipsBeforeAnyMutation(bool duplicate, bool unidentified)
    {
        var (handler, ports) = CreateIngestRomHandler();
        SetupCatalogMatch(ports, unidentified ? [] : [123], unidentified ? [] : [10]);
        if (duplicate)
            ports.RomRepository.GetBySha1Async(TestSha1, Arg.Any<CancellationToken>()).Returns(
                RomFile.Rehydrate(7, "existing.rom", 42, 1, TestSha1, TestMd5, TestCrc32, DateTimeOffset.UtcNow, TestSha256));

        var result = await handler.HandleAsync(new IngestRomCommand(
            new MemoryStream([1]), "skip.rom", AllowUnidentified: true, TrackedOnly: true));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Library.UntrackedRom");
        await ports.FileStorage.DidNotReceiveWithAnyArgs().StageAsync(default(ITempFile)!, default);
        await ports.DatRepository.DidNotReceiveWithAnyArgs().LinkDatRomsToRomFileAsync(default, default, default);
        await ports.Tracking.DidNotReceiveWithAnyArgs().TrackByTitleIdsAsync(default!, default);
        await ports.UnitOfWork.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestRomCommandHandler_TrackedOnlySharedRom_RetainsWithoutTrackingOtherMatches(bool duplicate)
    {
        var (handler, ports) = CreateIngestRomHandler();
        SetupCatalogMatch(ports, [123, 456], [10]);
        ports.TrackedTitles.HasTrackedAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 123, 456 })), Arg.Any<CancellationToken>()).Returns(true);
        if (duplicate)
            ports.RomRepository.GetBySha1Async(TestSha1, Arg.Any<CancellationToken>()).Returns(
                RomFile.Rehydrate(7, "existing.rom", 42, 1, TestSha1, TestMd5, TestCrc32, DateTimeOffset.UtcNow, TestSha256));

        var result = await handler.HandleAsync(new IngestRomCommand(new MemoryStream([1]), "keep.rom", TrackedOnly: true));

        result.IsError.ShouldBeFalse();
        result.Value.IsNew.ShouldBe(!duplicate);
        await ports.Tracking.DidNotReceiveWithAnyArgs().TrackByTitleIdsAsync(default!, default);
    }

    [Fact]
    public async Task IngestRomCommandHandler_UnmatchedAllowedRom_DoesNotFlagLibraries()
    {
        var (handler, ports) = CreateIngestRomHandler();
        SetupCatalogMatch(ports);
        ports.DatRepository.LinkDatRomsToRomFileAsync(TestSha1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var result = await handler.HandleAsync(new IngestRomCommand(new MemoryStream([1]), "unmatched.rom", true));

        result.IsError.ShouldBeFalse();
        await ports.LibraryRepository.DidNotReceive()
            .MarkUpdatesAvailableByPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestRomCommandHandler_MatchedRomLink_FlagsMatchedTitlePlatform()
    {
        var (handler, ports) = CreateIngestRomHandler();
        SetupCatalogMatch(ports, [123], [10]);
        ports.DatRepository.LinkDatRomsToRomFileAsync(TestSha1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var result = await handler.HandleAsync(new IngestRomCommand(new MemoryStream([1]), "matched.rom"));

        result.IsError.ShouldBeFalse();
        await ports.LibraryRepository.Received(1)
            .MarkUpdatesAvailableByPlatformAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestRomCommandHandler_BiosCatalogMatch_IngestsAndLinksWithoutTitleSideEffects()
    {
        var (handler, ports) = CreateIngestRomHandler();
        SetupCatalogMatch(ports, biosPlatformIds: [10]);
        ports.DatRepository.LinkDatRomsToRomFileAsync(TestSha1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var result = await handler.HandleAsync(new IngestRomCommand(new MemoryStream([1]), "scph5500.bin"));

        result.IsError.ShouldBeFalse();
        result.Value.MatchedTitleIds.ShouldBeEmpty();
        result.Value.PlatformId.ShouldBe(10);
        await ports.DatRepository.Received(1)
            .LinkDatRomsToRomFileAsync(TestSha1, Arg.Any<int>(), Arg.Any<CancellationToken>());
        await ports.LibraryRepository.DidNotReceive()
            .MarkUpdatesAvailableByPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await ports.Tracking.DidNotReceive()
            .TrackByTitleIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestRomCommandHandler_DefaultUpload_MarksEveryMatchedTitleTracked()
    {
        var (handler, ports) = CreateIngestRomHandler();
        SetupCatalogMatch(ports, [123], [10]);
        ports.DatRepository.LinkDatRomsToRomFileAsync(TestSha1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);
        var result = await handler.HandleAsync(new IngestRomCommand(new MemoryStream([1]), "matched.rom"));

        result.IsError.ShouldBeFalse();
        await ports.Tracking.Received(1).TrackByTitleIdsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Contains(123)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestRomCommandHandler_AlreadyOwnedTitle_RetracksIdempotently()
    {
        var (handler, ports) = CreateIngestRomHandler();
        SetupCatalogMatch(ports, [123], [10]);
        ports.DatRepository.LinkDatRomsToRomFileAsync(TestSha1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);
        var result = await handler.HandleAsync(new IngestRomCommand(new MemoryStream([1]), "matched.rom"));

        result.IsError.ShouldBeFalse();
        await ports.Tracking.Received(1).TrackByTitleIdsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 123 })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestRomCommandHandler_ArchiveOnlyMatchedTitle_DoesNotTrack()
    {
        var (handler, ports) = CreateIngestRomHandler();
        SetupCatalogMatch(ports, [123], [10]);
        ports.DatRepository.LinkDatRomsToRomFileAsync(TestSha1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var result = await handler.HandleAsync(
            new IngestRomCommand(new MemoryStream([1]), "matched.rom", ArchiveOnly: true));

        result.IsError.ShouldBeFalse();
        await ports.Tracking.DidNotReceive().TrackByTitleIdsAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestRomCommandHandler_DeduplicatedDefaultRerun_TracksEvenWhenDatLinksExist()
    {
        var (handler, ports) = CreateIngestRomHandler();
        var existing = RomFile.Rehydrate(
            7,
            "existing.rom",
            42,
            1,
            TestSha1,
            TestMd5,
            TestCrc32,
            DateTimeOffset.UtcNow, TestSha256);
        ports.RomRepository.GetBySha1Async(TestSha1, Arg.Any<CancellationToken>()).Returns(existing);
        SetupCatalogMatch(ports, [123], [10]);
        ports.DatRepository.LinkDatRomsToRomFileAsync(TestSha1, 7, Arg.Any<CancellationToken>())
            .Returns(0);

        var result = await handler.HandleAsync(new IngestRomCommand(new MemoryStream([1]), "existing.rom"));

        result.IsError.ShouldBeFalse();
        result.Value.IsNew.ShouldBeFalse();
        await ports.Tracking.Received(1).TrackByTitleIdsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 123 })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestRomCommandHandler_ArchiveOnlyRerun_DoesNotRemoveOrRewriteTracking()
    {
        var (handler, ports) = CreateIngestRomHandler();
        var existing = RomFile.Rehydrate(
            7,
            "existing.rom",
            42,
            1,
            TestSha1,
            TestMd5,
            TestCrc32,
            DateTimeOffset.UtcNow, TestSha256);
        ports.RomRepository.GetBySha1Async(TestSha1, Arg.Any<CancellationToken>()).Returns(existing);
        SetupCatalogMatch(ports, [123], [10]);
        ports.DatRepository.LinkDatRomsToRomFileAsync(TestSha1, 7, Arg.Any<CancellationToken>())
            .Returns(0);

        var result = await handler.HandleAsync(
            new IngestRomCommand(new MemoryStream([1]), "existing.rom", ArchiveOnly: true));

        result.IsError.ShouldBeFalse();
        await ports.Tracking.DidNotReceive().TrackByTitleIdsAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestRomCommandHandler_UnmatchedUploadThenDuplicateAfterMatch_FlagsOnlyOnMatch()
    {
        var (handler, ports) = CreateIngestRomHandler();
        SetupCatalogMatch(ports);
        ports.DatRepository.LinkDatRomsToRomFileAsync(TestSha1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var unmatchedResult = await handler.HandleAsync(
            new IngestRomCommand(new MemoryStream([1]), "unmatched.rom", true));

        unmatchedResult.IsError.ShouldBeFalse();
        await ports.LibraryRepository.DidNotReceive()
            .MarkUpdatesAvailableByPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        ports.LibraryRepository.ClearReceivedCalls();
        ports.DatRepository.ClearReceivedCalls();

        var existing = unmatchedResult.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile;
        ports.RomRepository.GetBySha1Async(TestSha1, Arg.Any<CancellationToken>()).Returns(existing);
        // A DAT now matches this previously-unidentified ROM, so the title lookup and platform
        // lookup are consistent (both resolve the same matched title).
        SetupCatalogMatch(ports, [123], [10]);
        ports.DatRepository.LinkDatRomsToRomFileAsync(TestSha1, existing.Id, Arg.Any<CancellationToken>())
            .Returns(1);

        var matchedResult = await handler.HandleAsync(
            new IngestRomCommand(new MemoryStream([1]), "unmatched.rom", true));

        matchedResult.IsError.ShouldBeFalse();
        matchedResult.Value.IsNew.ShouldBeFalse();
        matchedResult.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.Id.ShouldBe(existing.Id);
        matchedResult.Value.ShouldBeOfType<RomIngestResult.WholeFile>().RomFile.FileId.ShouldBe(existing.FileId);
        await ports.LibraryRepository.Received(1)
            .MarkUpdatesAvailableByPlatformAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IngestRomThenDatMatch_UnmatchedUploadDoesNotFlagThenTitleMatchFlagsPlatform()
    {
        var (romHandler, ports) = CreateIngestRomHandler();
        SetupCatalogMatch(ports);
        ports.DatRepository.LinkDatRomsToRomFileAsync(TestSha1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var unmatchedResult = await romHandler.HandleAsync(
            new IngestRomCommand(new MemoryStream([1]), "unmatched.rom", true));

        unmatchedResult.IsError.ShouldBeFalse();
        await ports.LibraryRepository.DidNotReceive()
            .FlagForRematerializationByPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        ports.LibraryRepository.ClearReceivedCalls();
        ports.DatRepository.ClearReceivedCalls();

        var datHandler = CreateIngestDatHandler(ports.DatRepository, ports.LibraryRepository);

        var datResult = await datHandler.HandleAsync(
            IngestDatCommand.Create(new MemoryStream([1]), "test.dat", platformId: 10).Value);

        datResult.IsError.ShouldBeFalse();
        await ports.DatRepository.Received(1)
            .LinkDatRomsToExistingRomFilesStagedAsync(
                Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 55 })),
                Arg.Any<CancellationToken>());
        await ports.LibraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(10, Arg.Any<CancellationToken>());
    }


    [Fact]
    public async Task DeleteRomCommandHandler_MatchedRomDelete_FlagsMatchedTitlePlatform()
    {
        var romRepository = Substitute.For<IRomRepository>();
        var ownershipImpact = Substitute.For<IRomOwnershipImpactReader>();
        var fileStorage = Substitute.For<IFileStorageService>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<ITransaction>());
        var libraryRepository = Substitute.For<ILibraryRepository>();
        var rom = RomFile.Rehydrate(7, "matched.rom", 42, 1, TestSha1, TestMd5, TestCrc32, DateTimeOffset.UtcNow);

        romRepository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(rom);
        ownershipImpact.ReadTitlePlatformIdsAsync(7, Arg.Any<CancellationToken>())
            .Returns([10]);
        fileStorage.DeleteIfUnreferencedAsync(42, Arg.Any<CancellationToken>()).Returns(true);
        var handler = new DeleteRomCommandHandler(
            romRepository,
            ownershipImpact,
            Substitute.For<IRomPayloadAssertionImpactReader>(),
            fileStorage,
            outbox,
            unitOfWork,
            libraryRepository,
            Substitute.For<ITitlePayloadAvailabilityProjection>(),
            NullLogger<DeleteRomCommandHandler>.Instance);

        var result = await handler.HandleAsync(new DeleteRomCommand(7));

        result.IsError.ShouldBeFalse();
        await libraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRomCommandHandler_UnmatchedRomDelete_DoesNotFlagLibraries()
    {
        var romRepository = Substitute.For<IRomRepository>();
        var ownershipImpact = Substitute.For<IRomOwnershipImpactReader>();
        var fileStorage = Substitute.For<IFileStorageService>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Substitute.For<ITransaction>());
        var libraryRepository = Substitute.For<ILibraryRepository>();
        var rom = RomFile.Rehydrate(7, "unmatched.rom", 42, 1, TestSha1, TestMd5, TestCrc32, DateTimeOffset.UtcNow);

        romRepository.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(rom);
        ownershipImpact.ReadTitlePlatformIdsAsync(7, Arg.Any<CancellationToken>())
            .Returns([]);
        fileStorage.DeleteIfUnreferencedAsync(42, Arg.Any<CancellationToken>()).Returns(true);
        var handler = new DeleteRomCommandHandler(
            romRepository,
            ownershipImpact,
            Substitute.For<IRomPayloadAssertionImpactReader>(),
            fileStorage,
            outbox,
            unitOfWork,
            libraryRepository,
            Substitute.For<ITitlePayloadAvailabilityProjection>(),
            NullLogger<DeleteRomCommandHandler>.Instance);

        var result = await handler.HandleAsync(new DeleteRomCommand(7));

        result.IsError.ShouldBeFalse();
        await libraryRepository.DidNotReceive()
            .FlagForRematerializationByPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichmentJobExecutor_EffectiveContentRatingClaimChange_FlagsAffectedPlatformLibraries()
    {
        var titleRepository = Substitute.For<ITitleMetadataStore>();
        var titleRepositoryAggregates = Substitute.For<ITitleAggregateReader>();
        var platformRepository = Substitute.For<IPlatformRepository>();
        var orchestrator = Substitute.For<IEnrichmentOrchestrator>();
        var fileStorage = Substitute.For<IFileStorageService>();
        var materializationService = Substitute.For<ILibraryMaterializationService>();
        bool insideOwnedMutation = false;
        titleRepository.ApplyEnrichmentStagedAsync(Arg.Any<Title>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                insideOwnedMutation.ShouldBeTrue();
                return Task.CompletedTask;
            });
        materializationService.FlagAffectedLibrariesAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                insideOwnedMutation.ShouldBeTrue();
                return Task.CompletedTask;
            });
        var title = CreateTitle(1, 10, contentRatings: [Rating(RatingBoard.Esrb, "E", 0)]);
        var platform = Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow);

        titleRepositoryAggregates.GetAggregateAsync(1, Arg.Any<CancellationToken>()).Returns(title);
        platformRepository.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(platform);
        orchestrator.EnrichTitleAsync(title, platform, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                title.StoreProviderLayer("igdb", MetadataSourceType.Provider, new TitleMetadataPayload
                {
                    ContentRatings = [Claim(RatingBoard.Esrb, "M")]
                }, updatedAt: DateTimeOffset.UnixEpoch);
                title.Rematerialize(["igdb"]);
                return OrchestrationResult.Found(1f);
            });

        var executor = new EnrichmentJobExecutor(new TitleEnrichmentPipeline(
            titleRepositoryAggregates,
            titleRepository,
            Substitute.For<ITitleRowLock>(),
            platformRepository,
            orchestrator,
            fileStorage,
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Romd.Admin.Application.Dashboard.IStatsNotifier>(),
            materializationService,
            Options.Create(new EnrichmentOptions { MinimumAutoEnrichConfidence = 0.6f }),
            NullLogger<TitleEnrichmentPipeline>.Instance, Substitute.For<Romd.Admin.Application.Artwork.IAutomaticArtworkService>(),
            Substitute.For<Romd.Admin.Application.Artwork.IArtworkCompletionStore>(), clock: TimeProvider.System));

        await executor.ExecuteAsync(
            EnrichmentJob.Create("Title 1", 1, 10, TimeProvider.System.GetUtcNow()),
            new JobContext(
                null,
                _ => Task.CompletedTask,
                CancellationToken.None,
                ownedMutationDelegate: async (mutation, mutationCt) =>
                {
                    insideOwnedMutation = true;
                    try
                    {
                        await mutation(mutationCt);
                    }
                    finally
                    {
                        insideOwnedMutation = false;
                    }
                }));

        await materializationService.Received(1)
            .FlagAffectedLibrariesAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignPlatformCommandHandler_TitleAttribution_FlagsAssignedPlatform()
    {
        var datRepository = Substitute.For<IDatRepository>();
        var titleDerivation = new FakeTitleDerivationService();
        var platformRepository = Substitute.For<IPlatformRepository>();
        var biosGrouper = Substitute.For<IBiosGrouper>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var libraryRepository = Substitute.For<ILibraryRepository>();
        var catalogProjection = Substitute.For<ICatalogProjectionService>();
        catalogProjection.RebuildPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        var transaction = Substitute.For<ITransaction>();

        datRepository.GetByIdAsync(5, Arg.Any<CancellationToken>())
            .Returns(DatFile.Rehydrate(
                5,
                "DAT",
                "DAT",
                null,
                null,
                null,
                DatType.NoIntro,
                null,
                "test.dat",
                1,
                DateTimeOffset.UtcNow,
                null,
                1,
                1,
                0,
                5,
                DatFileLifecycle.Active,
                null));
        platformRepository.GetByIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(Platform.Rehydrate(10, "Super Nintendo", "snes", "Nintendo", DateTimeOffset.UtcNow));
        datRepository.GetAllGamesByDatIdAsync(5, BiosFilter.Include, Arg.Any<CancellationToken>())
            .Returns([CreateGame(55, "Matched Game")]);
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var sourceLifecycle = Substitute.For<ISourceLifecycle>();
        sourceLifecycle.GetLinkedTitleIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([123]);

        var handler = new AssignPlatformCommandHandler(
            datRepository,
            titleDerivation,
            platformRepository,
            biosGrouper,
            unitOfWork,
            libraryRepository,
            catalogProjection,
            NullLogger<AssignPlatformCommandHandler>.Instance,
            sourceLifecycle);

        var command = AssignPlatformCommand.Create(5, 10).Value;
        var result = await handler.HandleAsync(command);

        result.IsError.ShouldBeFalse();
        await catalogProjection.Received(1).MarkPlatformDirtyAsync(
            10,
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyCollection<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 123 })));
        await libraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoveGameCommandHandler_Deattribution_FlagsGamePlatform()
    {
        var titleSourceAssignments = Substitute.For<ITitleSourceAssignmentStore>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var libraryRepository = Substitute.For<ILibraryRepository>();
        var catalogProjection = Substitute.For<ICatalogProjectionService>();
        catalogProjection.RebuildPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        var transaction = Substitute.For<ITransaction>();

        titleSourceAssignments.GetAssignmentContextAsync(55, Arg.Any<CancellationToken>())
            .Returns(new TitleSourceAssignmentContext(55, 10, 123));
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var titleLock = Substitute.For<ITitleRowLock>();
        titleLock.LockAsync(123, Arg.Any<CancellationToken>()).Returns(true);
        var titleReader = Substitute.For<ITitleReader>();
        titleReader.GetSummaryAsync(123, Arg.Any<CancellationToken>())
            .Returns(new TitleSummary(123, 10, "Attributed title", EnrichmentStatus.None,
                null, null, null, null, null, null, null, DateTimeOffset.UtcNow, null, false));

        var handler = new MoveGameCommandHandler(Substitute.For<Romd.Application.Common.ReferenceCatalog.IReferenceCatalogService>(),
            Substitute.For<ITitleCreationStore>(),
            Substitute.For<ITitleOrphanCleanup>(),
            titleReader,
            titleLock,
            titleSourceAssignments,
            unitOfWork,
            libraryRepository,
            catalogProjection,
            NullLogger<MoveGameCommandHandler>.Instance, clock: TimeProvider.System);

        var result = await handler.HandleAsync(new MoveGameCommand(123, 55, null, null));

        result.IsError.ShouldBeFalse();
        result.Value.NewTitle.ShouldBeNull();
        await titleReader.Received(1).GetSummaryAsync(123, Arg.Any<CancellationToken>());
        await titleSourceAssignments.Received(1)
            .ClearAssignmentsAsync(Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 55 })),
                Arg.Any<CancellationToken>());
        await catalogProjection.Received(1).MarkPlatformDirtyAsync(
            10, Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 123 })));
        await libraryRepository.Received(1)
            .FlagForRematerializationByPlatformAsync(10, Arg.Any<CancellationToken>());
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    private static (IngestRomCommandHandler Handler, IngestRomPorts Ports) CreateIngestRomHandler()
    {
        var romRepository = Substitute.For<IRomRepository>();
        var datRepository = Substitute.For<IDatRepository>();
        var catalogMatches = Substitute.For<IRomCatalogMatchReader>();
        var fileStorage = Substitute.For<IFileStorageService>();
        var tempFileFactory = Substitute.For<ITempFileFactory>();
        var hashingService = Substitute.For<IHashingService>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var libraryRepository = Substitute.For<ILibraryRepository>();
        var trackedTitles = Substitute.For<ITrackedTitleReader>();
        var tracking = Substitute.For<ITrackedTitleRepository>();
        var transaction = Substitute.For<ITransaction>();

        var tempFile = new MemoryTempFile([1], TestSha256);
        tempFileFactory.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>(), Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(), Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>(), Arg.Is<Func<ReadOnlyMemory<byte>, int>?>(selector => selector != null)).Returns(tempFile);
        hashingService.ComputeDatHashesAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DatHashResult(TestSha1, TestMd5, TestCrc32, 1));
        var persistedRom = RomFile.Rehydrate(5, "persisted.rom", 41, 1, TestSha1, TestMd5, TestCrc32, DateTimeOffset.UtcNow, TestSha256);
        romRepository.GetBySha1Async(TestSha1, Arg.Any<CancellationToken>()).Returns((RomFile?)null, persistedRom);
        romRepository.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
            romRepository.GetBySha1Async(TestSha1, call.Arg<CancellationToken>()));
        romRepository.ListRepresentationsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var rom = (await romRepository.GetByIdAsync(call.Arg<int>(), call.Arg<CancellationToken>()))!;
            IReadOnlyList<RetainedRepresentation> rows = [new(rom.Id + 10, rom.Id + 100, rom.FileId, TestSha256, RomContainerFacts.Identity.Instance, true, true)];
            return rows;
        });
        romRepository.RecordWholeMeasurementStagedAsync(Arg.Any<int>(), Arg.Any<int?>(), RomContainerFacts.Identity.Instance,
            Arg.Any<RomRepresentationVerification>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            int romId = call.ArgAt<int>(0);
            var rom = (await romRepository.GetByIdAsync(romId, call.Arg<CancellationToken>()))!;
            return (RomRetainedMeasurement?)new(romId + 10, romId + 100, call.ArgAt<int?>(1) ?? rom.FileId, call.Arg<RomRepresentationVerification>().Id);
        });
        var publication = Substitute.For<IStagedFilePublication>();
        publication.ReadAfterFlush().Returns(new FileStoreResult(FileEntity.Rehydrate(41, TestSha256, 1, 1, false, DateTimeOffset.UtcNow), true, false));
        fileStorage.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>()).Returns(publication);
        var facts = Substitute.For<IMediaFactPublisher>();
        facts.CreateLogicalAsync(Arg.Any<string>(), Arg.Any<RomLogicalIdentity>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(5);
        var publisher = new RomRepresentationPublisher(fileStorage, unitOfWork, romRepository, TestRomMediaPolicies.Create(),
            Romd.Infrastructure.Storage.RomEncodingComposition.CreateRegistry(), Substitute.For<IFileMutationLock>(), null, facts,
            Substitute.For<IRomMediaObservationStore>(), Substitute.For<IRomMediaCreationStore>(), new Romd.Infrastructure.Jobs.JobAuditContext(), TimeProvider.System, Substitute.For<Romd.Admin.Application.Source.Rom.Reconciliation.IMediaReconciliationRequests>(), Substitute.For<IRomPs2CurrentReferenceAuthority>());
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        var handler = new IngestRomCommandHandler(
            romRepository,
            datRepository,
            catalogMatches,
            tempFileFactory,
            Romd.Infrastructure.Tests.Storage.MediaInfrastructureTestServices.ManagedPreparation(hashingService, () => new Romd.Infrastructure.Storage.RvzVerifier(new()), new Romd.Infrastructure.Tests.Storage.MediaInfrastructureTestServices.EmptyReferences()), Substitute.For<Romd.Admin.Application.Source.Rom.Compressed.IRomCdIngestor>(),
            publisher,
            outbox,
            libraryRepository,
            trackedTitles,
            tracking,
            Substitute.For<IRomPayloadAssertionImpactReader>(),
            Substitute.For<ITitlePayloadAvailabilityProjection>(),
            NullLogger<IngestRomCommandHandler>.Instance, clock: TimeProvider.System);

        return (handler, new IngestRomPorts(
            romRepository, datRepository, catalogMatches, libraryRepository, trackedTitles, tracking, fileStorage,
            unitOfWork));
    }

    private static void SetupCatalogMatch(
        IngestRomPorts ports,
        IReadOnlyList<int>? titleIds = null,
        IReadOnlyList<int>? titlePlatformIds = null,
        IReadOnlyList<int>? biosPlatformIds = null)
    {
        ports.CatalogMatches.ReadAsync(TestSha1, Arg.Any<CancellationToken>())
            .Returns(new RomCatalogMatch(titleIds ?? [], titlePlatformIds ?? [], biosPlatformIds ?? []));
    }

    private static IngestDatCommandHandler CreateIngestDatHandler(
        IDatRepository datRepository,
        ILibraryRepository libraryRepository)
    {
        var datReader = Substitute.For<IDatReader>();
        var tempFileFactory = Substitute.For<ITempFileFactory>();
        var fileStorage = Substitute.For<IFileStorageService>();
        var titleDerivation = new FakeTitleDerivationService();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var regionResolver = Substitute.For<ITaxonomyResolver<Region>>();
        var languageResolver = Substitute.For<ITaxonomyResolver<GameLanguage>>();
        var transaction = Substitute.For<ITransaction>();
        var tempFile = new MemoryTempFile([1], TestSha256);
        var datFile = DatFile.Rehydrate(
            5,
            "DAT",
            "DAT",
            null,
            null,
            null,
            DatType.NoIntro,
            10,
            "test.dat",
            1,
            DateTimeOffset.UtcNow,
            null,
            0,
            0,
            0,
            5,
            DatFileLifecycle.Active,
            null);

        tempFileFactory.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>(), Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(), Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>()).Returns(tempFile);
        var publication = Substitute.For<IStagedFilePublication>();
        publication.ReadAfterFlush().Returns(new FileStoreResult(FileEntity.Rehydrate(1, TestSha256, 1, 1, false,
            DateTimeOffset.UnixEpoch), false, true));
        fileStorage.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>()).Returns(publication);
        datReader.ReadHeaderAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DatMetadata
            {
                Name = "DAT",
                Description = "DAT",
                DatType = DatType.NoIntro
            });
        datReader.StreamGamesAsync(Arg.Any<Stream>(), 5, Arg.Any<CancellationToken>())
            .Returns(_ => SingleGameStream(CreateGameWithRom("Matched Game")));
        datRepository.GetByFileIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(datFile);
        datRepository.AddStagedAsync(Arg.Any<DatFile>(), Arg.Any<DatSource>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        datRepository.AddGamesBatchStagedAsync(Arg.Any<IReadOnlyList<DatGameWithEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new FixedStagedDatGameBatch([55]));
        datRepository.LinkDatRomsToExistingRomFilesStagedAsync(
                Arg.Is<IReadOnlyList<int>>(ids => ids.SequenceEqual(new[] { 55 })),
                Arg.Any<CancellationToken>())
            .Returns(1);
        datRepository.AddGameRegionsBatchStagedAsync(Arg.Any<IReadOnlyList<(int GameId, int RegionId)>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        datRepository.AddGameLanguagesBatchStagedAsync(Arg.Any<IReadOnlyList<(int GameId, int LanguageId)>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        datRepository.UpdateCountsAsync(5, 1, 1, 0, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        regionResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        languageResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var platformHeaderResolver = Substitute.For<IPlatformHeaderResolver>();
        platformHeaderResolver.ResolvePlatformIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var biosGrouper = Substitute.For<IBiosGrouper>();
        var catalogProjection = Substitute.For<ICatalogProjectionService>();
        catalogProjection.RebuildPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        return new IngestDatCommandHandler(
            datReader,
            datRepository,
            titleDerivation,
            tempFileFactory,
            fileStorage,
            biosGrouper,
            unitOfWork,
            outbox,
            libraryRepository,
            regionResolver,
            languageResolver,
            catalogProjection,
            platformHeaderResolver,
            NullLogger<IngestDatCommandHandler>.Instance,
            Substitute.For<ISourceLifecycle>(), clock: TimeProvider.System);
    }

    private static async IAsyncEnumerable<ErrorOr<DatGame>> SingleGameStream(DatGame game)
    {
        await Task.Yield();
        yield return game;
    }

    private static Title CreateTitle(int id, int platformId, IReadOnlyList<ContentRating>? contentRatings = null) =>
        Title.Rehydrate(
            id,
            platformId,
            $"Title {id}",
            $"title {id}",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            EnrichmentStatus.None,
            null,
            DateTimeOffset.UtcNow,
            contentRatings: contentRatings);

    private static ContentRating Rating(RatingBoard board, string code, int minimumAge) =>
        new()
        {
            Board = board,
            Code = code,
            Designation = RatingDesignation.Rated,
            MinimumAge = minimumAge,
            SourceId = "igdb"
        };

    private static ContentRatingClaim Claim(RatingBoard board, string rawCode) =>
        new() { Board = board, RawCode = rawCode };

    private static DatGame CreateGame(int id, string name) =>
        DatGame.Rehydrate(
            id,
            5,
            name,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            [],
            []);

    private static DatGame CreateGameWithRom(string name)
    {
        var game = DatGame.CreateNew(5, name);
        game.AddRom(DatRom.CreateNew("matched.rom", 1, sha1: TestSha1));
        return game;
    }

    private static void SetupStatsNotifier(IStatsNotifier statsNotifier)
    {
        statsNotifier.NotifyStorageChangedAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        statsNotifier.NotifyCoverageChangedAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        statsNotifier.NotifyHealthChangedAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private sealed record IngestRomPorts(
        IRomRepository RomRepository,
        IDatRepository DatRepository,
        IRomCatalogMatchReader CatalogMatches,
        ILibraryRepository LibraryRepository,
        ITrackedTitleReader TrackedTitles,
        ITrackedTitleRepository Tracking,
        IFileStorageService FileStorage,
        IUnitOfWork UnitOfWork);

    private sealed class MemoryTempFile(byte[] content, Sha256 sha256) : ITempFile
    {
        public bool TryBeginChildUse() => true;
        public bool TryEndChildUse() => true;
        public bool Quarantine() => true;
        public string Path => "memory.rom";
        public Sha256 FileSha256 { get; } = sha256;
        public long FileSize => content.Length;
        public Stream OpenRead() => new MemoryStream(content);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
