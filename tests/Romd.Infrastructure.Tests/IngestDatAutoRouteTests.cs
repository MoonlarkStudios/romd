using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Domain.Source.Dat.Parsing;
using Romd.Domain.Storage;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;
using ErrorOr;

namespace Romd.Infrastructure.Tests;

/// <summary>
///     Covers DAT header auto-routing in <see cref="IngestDatCommandHandler" />:
///     when no platform is supplied, the header resolver decides routing;
///     when one is supplied, the resolver is bypassed. Title derivation is observed
///     through the claims handed to the derivation facade.
/// </summary>
public sealed class IngestDatAutoRouteTests
{
    private static readonly Sha256 TestSha256 =
        Sha256.Parse("1111111111111111111111111111111111111111111111111111111111111111");

    private const string HeaderName = "Nintendo - Super Nintendo Entertainment System";
    private const int ResolvedPlatformId = 10;

    private readonly IDatRepository _datRepository = Substitute.For<IDatRepository>();
    private readonly FakeTitleDerivationService _titleDerivation = new();
    private readonly IDatReader _datReader = Substitute.For<IDatReader>();
    private readonly IPlatformHeaderResolver _headerResolver = Substitute.For<IPlatformHeaderResolver>();
    private readonly IBiosGrouper _biosGrouper = Substitute.For<IBiosGrouper>();
    private readonly ITaxonomyResolver<Region> _regionResolver = Substitute.For<ITaxonomyResolver<Region>>();
    private readonly ITaxonomyResolver<GameLanguage> _languageResolver = Substitute.For<ITaxonomyResolver<GameLanguage>>();

    private DatFile? _capturedDatFile;

    private IngestDatCommandHandler CreateHandler(int? resolvedPlatformId)
    {
        var tempFileFactory = Substitute.For<ITempFileFactory>();
        var fileStorage = Substitute.For<IFileStorageService>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var outbox = Substitute.For<IAdminEventOutbox>();
        var libraryRepository = Substitute.For<ILibraryRepository>();
        var transaction = Substitute.For<ITransaction>();
        var tempFile = new MemoryTempFile([1], TestSha256);

        tempFileFactory.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(tempFile);
        fileStorage.StoreFromTempFileAsync(tempFile, Arg.Any<CancellationToken>())
            .Returns(new FileStoreResult(FileEntity.CreateNew(TestSha256, 1, 1, false), true, false));
        _datReader.ReadHeaderAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DatMetadata
            {
                Name = HeaderName,
                Description = "Test DAT",
                DatType = DatType.NoIntro
            });
        _datReader.StreamGamesAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => BiosAndGameStream());

        _datRepository.GetByFileIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => _capturedDatFile);
        _datRepository.AddStagedAsync(Arg.Any<DatFile>(), Arg.Any<DatSource>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _capturedDatFile = call.Arg<DatFile>();
                return Task.CompletedTask;
            });
        _datRepository.AddGamesBatchAsync(Arg.Any<IReadOnlyList<DatGameWithEntry>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
                (IReadOnlyList<int>)Enumerable.Range(1, call.Arg<IReadOnlyList<DatGameWithEntry>>().Count).ToList());

        _regionResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<int>)[]);
        _languageResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<int>)[]);

        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        _headerResolver.ResolvePlatformIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(resolvedPlatformId);

        var catalogProjection = Substitute.For<ICatalogProjectionService>();
        catalogProjection.RebuildPlatformAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        return new IngestDatCommandHandler(
            _datReader,
            _datRepository,
            _titleDerivation,
            tempFileFactory,
            fileStorage,
            _biosGrouper,
            unitOfWork,
            outbox,
            libraryRepository,
            _regionResolver,
            _languageResolver,
            catalogProjection,
            _headerResolver,
            NullLogger<IngestDatCommandHandler>.Instance,
            Substitute.For<ISourceLifecycle>());
    }

    private static IngestDatCommand CreateCommand(int? platformId = null) =>
        IngestDatCommand.Create(new MemoryStream([1]), "test.dat", platformId).Value;

    [Fact]
    public async Task HandleAsync_NoPlatform_HeaderResolves_CreatesRoutedDatAndClaimsAtResolvedPlatform()
    {
        var handler = CreateHandler(ResolvedPlatformId);

        var result = await handler.HandleAsync(CreateCommand());

        result.IsError.ShouldBeFalse();
        _capturedDatFile.ShouldNotBeNull();
        _capturedDatFile.PlatformId.ShouldBe(ResolvedPlatformId);
        await _headerResolver.Received(1).ResolvePlatformIdAsync(HeaderName, Arg.Any<CancellationToken>());
        _titleDerivation.UpsertClaims.Count.ShouldBe(2);
        _titleDerivation.UpsertClaims.ShouldAllBe(c => c.Claim.PlatformId == ResolvedPlatformId);
    }

    [Fact]
    public async Task HandleAsync_NoPlatform_HeaderUnresolved_CreatesUnroutedDatWithUnroutedClaims()
    {
        var handler = CreateHandler(resolvedPlatformId: null);

        var result = await handler.HandleAsync(CreateCommand());

        result.IsError.ShouldBeFalse();
        _capturedDatFile.ShouldNotBeNull();
        _capturedDatFile.PlatformId.ShouldBeNull();
        _titleDerivation.UpsertClaims.Count.ShouldBe(2);
        _titleDerivation.UpsertClaims.ShouldAllBe(c => c.Claim.PlatformId == null);
    }

    [Fact]
    public async Task HandleAsync_ExplicitPlatform_BypassesResolver()
    {
        var handler = CreateHandler(resolvedPlatformId: null);

        var result = await handler.HandleAsync(CreateCommand(platformId: 7));

        result.IsError.ShouldBeFalse();
        _capturedDatFile.ShouldNotBeNull();
        _capturedDatFile.PlatformId.ShouldBe(7);
        await _headerResolver.DidNotReceive()
            .ResolvePlatformIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_BiosGames_ClaimedWithBiosFlagPersistedWithEntriesAndGrouped()
    {
        var handler = CreateHandler(ResolvedPlatformId);

        var result = await handler.HandleAsync(CreateCommand());

        result.IsError.ShouldBeFalse();

        // Both games are claimed in stream order; only the BIOS entry carries the BIOS flag.
        _titleDerivation.UpsertClaims
            .Select(c => (c.Claim.EntryKey, c.Claim.IsBios))
            .ShouldBe([("[BIOS] Firmware", true), ("Real Game (USA)", false)]);

        // Both entries — including the BIOS one — are persisted as DatGames stitched to the
        // facade-assigned source entries, in claim order.
        await _datRepository.Received().AddGamesBatchAsync(
            Arg.Is<IReadOnlyList<DatGameWithEntry>>(games =>
                games.Count == 2 &&
                games[0].Game.Name == "[BIOS] Firmware" && games[0].SourceEntryId == 1 &&
                games[1].Game.Name == "Real Game (USA)" && games[1].SourceEntryId == 2),
            Arg.Any<CancellationToken>());

        // The BIOS entry (and only it) is grouped into the platform's BIOS catalog.
        await _biosGrouper.Received(1).GroupAsync(
            ResolvedPlatformId,
            Arg.Is<IReadOnlyList<(int GameId, string Name)>>(g => g.Count == 1 && g[0].Name == "[BIOS] Firmware"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_BiosGames_UnroutedDat_AreNotGrouped()
    {
        var handler = CreateHandler(resolvedPlatformId: null);

        var result = await handler.HandleAsync(CreateCommand());

        result.IsError.ShouldBeFalse();
        // With no platform, grouping is deferred to platform assignment.
        await _biosGrouper.DidNotReceive().GroupAsync(
            Arg.Any<int>(),
            Arg.Any<IReadOnlyList<(int, string)>>(),
            Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<ErrorOr<DatGame>> BiosAndGameStream()
    {
        await Task.Yield();
        yield return DatGame.CreateNew(1, "[BIOS] Firmware", isBios: true);
        yield return DatGame.CreateNew(1, "Real Game (USA)");
    }

    private sealed class MemoryTempFile(byte[] content, Sha256 sha256) : ITempFile
    {
        public string Path => "memory.dat";
        public Sha256 FileSha256 { get; } = sha256;
        public long FileSize => content.Length;
        public Stream OpenRead() => new MemoryStream(content);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
