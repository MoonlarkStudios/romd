using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore;
using Romd.Consumer.Application.Delivery;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Identity;
using Romd.Persistence.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>
///     Consumer BIOS listing honors the effective-source boundary: firmware known only through
///     a non-Active source's game vanishes from the listing even while the platform itself
///     stays exposed through an active source's materialized release.
///     World: one platform exposed via an owned+exposed materialized release; one BIOS group
///     with two firmware roms — one mapped through source A's game (stays Active), one through
///     source D's game (flipped per test).
/// </summary>
public sealed class ConsumerBiosRepositoryTests : IAsyncDisposable
{
    private const int PlatformId = 1;
    private const string PlatformShortName = "n64";
    private const int SourceAId = 1;
    private const int SourceDId = 2;
    private const int DatFileAId = 1;
    private const int DatFileDId = 2;
    private const int EntryAId = 1;
    private const int EntryDId = 2;
    private const int GameAId = 1;
    private const int GameDId = 2;
    private const int TitleId = 101;
    private const int BiosId = 900;

    private static readonly Guid UserId = Guid.Parse("7c1f0f6e-24bb-4f39-9c7d-0f6a3f6a9d21");

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _options;
    private readonly int _libraryId;

    public ConsumerBiosRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .UseOpenIddict()
            .Options;

        using var context = CreateContext();
        _libraryId = SeedLibraryAndUser(context);
        SeedBiosWorld(context, _libraryId);
    }

    [Fact]
    public async Task GetPlatformBios_DisabledSourceFirmware_ExcludedFromListing()
    {
        var control = await ReadBiosAsync();
        control.IsExposed.ShouldBeTrue();
        control.Files.Select(file => file.FileName).ShouldBe(["bios-active.bin", "bios-dormant.bin"]);

        await SetSourceStatusAsync(SourceDId, nameof(CatalogSourceStatus.Disabled));

        var filtered = await ReadBiosAsync();
        filtered.IsExposed.ShouldBeTrue();
        filtered.Files.Select(file => file.FileName).ShouldBe(["bios-active.bin"]);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private RomdDbContext CreateContext() => new(_options);

    private async Task<ConsumerPlatformBios> ReadBiosAsync()
    {
        await using var context = CreateContext();
        var repository = new ConsumerBiosRepository(context);

        var result = await repository.GetPlatformBiosAsync(
            new ConsumerPlatformBiosRequest(new ConsumerLibraryScope(UserId), PlatformShortName));

        return result.ShouldBeOfType<ConsumerLibraryReadResult<ConsumerPlatformBios>.Found>().Value;
    }

    private async Task SetSourceStatusAsync(int catalogSourceId, string status)
    {
        await using var context = CreateContext();
        await context.CatalogSources
            .Where(source => source.Id == catalogSourceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(source => source.Status, status));
    }

    private static int SeedLibraryAndUser(RomdDbContext context)
    {
        var library = LibraryEntity.FromDomain(Library.CreateNew("Bios Library", new LibraryConfiguration()));
        library.NeedsMaterialization = false;
        context.Libraries.Add(library);
        context.SaveChanges();
        context.Users.Add(new RomdUser
        {
            Id = UserId,
            UserName = "bios-user",
            NormalizedUserName = "BIOS-USER",
            Email = "bios@example.test",
            NormalizedEmail = "BIOS@EXAMPLE.TEST",
            LibraryId = library.Id,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.SaveChanges();
        return library.Id;
    }

    private static void SeedBiosWorld(RomdDbContext context, int libraryId)
    {
        context.Platforms.Add(new PlatformEntity
        {
            Id = PlatformId,
            Name = "Nintendo 64",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Nintendo 64", BaseCompactLabel = "Nintendo 64", CanonicalKey = PlatformShortName, ShortName = PlatformShortName
        });

        context.Files.AddRange(NewFile(DatFileAId), NewFile(DatFileDId));
        SeedSource(context, SourceAId, DatFileAId);
        SeedSource(context, SourceDId, DatFileDId);

        context.Titles.Add(new TitleEntity
        {
            Id = TitleId,
            PlatformId = PlatformId,
            Name = "Exposed Title",
            NormalizedName = "exposedtitle",
            EnrichmentStatus = "None"
        });

        // The platform stays exposed through this release regardless of source D's status.
        context.MaterializedLibraryReleases.Add(new MaterializedLibraryReleaseEntity
        {
            LibraryId = libraryId,
            TitleId = TitleId,
            DatGameId = GameAId,
            DatFileId = DatFileAId,
            PlatformId = PlatformId,
            IsEligible = true,
            IsComplete = true,
            IsOwned = true,
            IsPlayable = true,
            IsExposed = true
        });

        context.SourceEntries.AddRange(
            NewBiosEntry(EntryAId, SourceAId),
            NewBiosEntry(EntryDId, SourceDId));
        context.DatGames.AddRange(
            NewBiosGame(GameAId, DatFileAId, EntryAId),
            NewBiosGame(GameDId, DatFileDId, EntryDId));
        context.DatRoms.AddRange(
            NewDatRom(1, GameAId, "bios-active.bin", NewSha1(0xA1)),
            NewDatRom(2, GameDId, "bios-dormant.bin", NewSha1(0xD1)));

        context.Bios.Add(new BiosEntity
        {
            Id = BiosId,
            PlatformId = PlatformId,
            Name = "Console BIOS",
            NormalizedName = "consolebios"
        });
        context.BiosGameMappings.AddRange(
            new BiosGameMappingEntity { DatGameId = GameAId, BiosId = BiosId },
            new BiosGameMappingEntity { DatGameId = GameDId, BiosId = BiosId });

        context.SaveChanges();
        context.ChangeTracker.Clear();
    }

    private static void SeedSource(RomdDbContext context, int catalogSourceId, int datFileId) =>
        context.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity
                {
                    Id = catalogSourceId,
                    Kind = nameof(CatalogSourceKind.Dat),
                    Status = nameof(CatalogSourceStatus.Active)
                }
            },
            Id = datFileId,
            Name = $"DAT {datFileId}",
            Description = $"DAT {datFileId}",
            Type = "NoIntro",
            PlatformId = PlatformId,
            OriginalFilename = $"dat-{datFileId}.dat",
            FileId = datFileId
        });

    private static FileEntityPersistence NewFile(byte value) => new()
    {
        Id = value,
        Sha256 = NewSha256(value),
        Size = 1,
        SizeOnDisk = 1
    };

    private static SourceEntryEntity NewBiosEntry(int id, int catalogSourceId) => new()
    {
        Id = id,
        CatalogSourceId = catalogSourceId,
        EntryKey = $"BIOS {id}",
        Name = $"BIOS {id}",
        PlatformId = PlatformId
    };

    private static DatGameEntity NewBiosGame(int id, int datFileId, int sourceEntryId) => new()
    {
        Id = id,
        DatFileId = datFileId,
        SourceEntryId = sourceEntryId,
        Name = $"BIOS Game {id}",
        IsBios = true
    };

    private static DatRomEntity NewDatRom(int id, int datGameId, string name, Sha1 sha1) => new()
    {
        Id = id,
        DatGameId = datGameId,
        Name = name,
        Size = 1024,
        Sha1 = sha1
    };

    private static Sha256 NewSha256(byte value) => Sha256.FromBytes(NewBytes(Sha256.ByteLength, value));
    private static Sha1 NewSha1(byte value) => Sha1.FromBytes(NewBytes(Sha1.ByteLength, value));

    private static byte[] NewBytes(int length, byte value)
    {
        var bytes = new byte[length];
        bytes[0] = value;
        return bytes;
    }
}
