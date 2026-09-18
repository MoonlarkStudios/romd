using Microsoft.EntityFrameworkCore;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Source;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed class DatCatalogSourceSnapshotProviderTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _options;

    public DatCatalogSourceSnapshotProviderTests()
    {
        _connection = PostgreSqlTestDatabase.Create();
        _options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var context = CreateContext();
    }

    [Fact]
    public async Task ReadPlatformAsync_VersionsShareEntry_LocalPayloadIsEntryLevelFact()
    {
        using var context = CreateContext();
        SeedSourceGraph(context);
        await context.SaveChangesAsync();
        var reader = new DatCatalogSourceSnapshotProvider(context);

        var snapshot = await reader.ReadPlatformAsync(1);

        var sharedEntry = snapshot.Entries.Single(entry => entry.SourceEntryId == 1);
        sharedEntry.Claims.Length.ShouldBe(2);
        sharedEntry.HasLocalPayload.ShouldBeTrue();
        snapshot.Entries.Single(entry => entry.SourceEntryId == 2).HasLocalPayload.ShouldBeFalse();
        sharedEntry.Claims.SelectMany(claim => claim.Requirements)
            .Select(requirement => requirement.ProviderRequirementKey)
            .ShouldBe(["1", "2"], ignoreOrder: true);
    }

    [Fact]
    public async Task ReadPlatformAsync_Canceled_PropagatesCancellation()
    {
        using var context = CreateContext();
        var reader = new DatCatalogSourceSnapshotProvider(context);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            reader.ReadPlatformAsync(1, cancellation.Token));
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateContext() => new(_options);

    private static void SeedSourceGraph(RomdDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        context.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "SNES",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "SNES", BaseCompactLabel = "SNES", CanonicalKey = "snes", ShortName = "snes",
            Manufacturer = "Nintendo",
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        context.Files.AddRange(
            File(id: 10, hashByte: 0x10, now),
            File(id: 11, hashByte: 0x11, now));
        context.CatalogSources.Add(new CatalogSourceEntity
        {
            Id = 1,
            Kind = "Dat",
            Status = "Active"
        });
        context.DatSources.Add(new DatSourceEntity { Id = 1, CatalogSourceId = 1 });
        context.DatFiles.Add(new DatFileEntity
        {
            Id = 1,
            DatSourceId = 1,
            Name = "DAT",
            Description = "DAT",
            Type = "NoIntro",
            PlatformId = 1,
            OriginalFilename = "source.dat",
            FileId = 10,
            GameCount = 3,
            RomCount = 3,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        context.SourceEntries.AddRange(
            Entry(id: 1, name: "Shared", now),
            Entry(id: 2, name: "Remote only", now));
        context.DatGames.AddRange(
            Game(id: 1, sourceEntryId: 1, name: "Shared v1", now),
            Game(id: 2, sourceEntryId: 1, name: "Shared v2", now),
            Game(id: 3, sourceEntryId: 2, name: "Remote only", now));
        context.RomFiles.Add(new RomFileEntity
        {
            Id = 1,
            OriginalFilename = "shared.sfc",
            FileId = 11,
            Sha1 = NewSha1(0x21),
            Md5 = Md5.Parse(new string('2', Md5.ByteLength * 2)),
            Crc32 = Crc32.Parse(new string('3', Crc32.ByteLength * 2)),
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        context.DatRoms.AddRange(
            Rom(id: 1, gameId: 1, shaByte: 0x21, romFileId: 1, now),
            Rom(id: 2, gameId: 2, shaByte: 0x22, romFileId: null, now),
            Rom(id: 3, gameId: 3, shaByte: 0x23, romFileId: null, now));
    }

    private static FileEntityPersistence File(int id, byte hashByte, DateTimeOffset now) => new()
    {
        Id = id,
        Sha256 = NewSha256(hashByte),
        Size = 1,
        SizeOnDisk = 1,
        CreatedAt = now,
        CreatedByUserId = SystemActor.UserId
    };

    private static SourceEntryEntity Entry(int id, string name, DateTimeOffset now) => new()
    {
        Id = id,
        CatalogSourceId = 1,
        EntryKey = name,
        Name = name,
        PlatformId = 1,
        CreatedAt = now,
        CreatedByUserId = SystemActor.UserId
    };

    private static DatGameEntity Game(int id, int sourceEntryId, string name, DateTimeOffset now) => new()
    {
        Id = id,
        DatFileId = 1,
        SourceEntryId = sourceEntryId,
        Name = name,
        CreatedAt = now,
        CreatedByUserId = SystemActor.UserId
    };

    private static DatRomEntity Rom(
        int id,
        int gameId,
        byte shaByte,
        int? romFileId,
        DateTimeOffset now) => new()
    {
        Id = id,
        DatGameId = gameId,
        Name = $"rom-{id}.sfc",
        Size = 1024,
        Sha1 = NewSha1(shaByte),
        RomFileId = romFileId,
        CreatedAt = now,
        CreatedByUserId = SystemActor.UserId
    };

    private static Sha1 NewSha1(byte firstByte)
    {
        var bytes = new byte[Sha1.ByteLength];
        bytes[0] = firstByte;
        return Sha1.FromSpan(bytes);
    }

    private static Sha256 NewSha256(byte firstByte)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = firstByte;
        return Sha256.FromSpan(bytes);
    }
}
