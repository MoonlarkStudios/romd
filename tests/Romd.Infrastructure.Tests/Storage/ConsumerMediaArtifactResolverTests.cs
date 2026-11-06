using NSubstitute;
using Romd.Consumer.Application.Delivery;
using Romd.Domain.Hashing;
using Romd.Infrastructure.Storage;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;
using Romd.PostgreSql.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class ConsumerMediaArtifactResolverTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly IContentAddressableStore _cas = Substitute.For<IContentAddressableStore>();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task ResolveMediaAsync_ExistingMedia_UsesLinkedFileHashAndTransfersStreamOwnership()
    {
        await using var db = _database.CreateContext();
        await SeedMediaAsync(db);
        using var content = new MemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        var key = StorageKey.FromHash(Sha256.Parse(new string('b', 64)));
        _cas.RetrieveAsync(key, cancellation.Token).Returns(content);

        var result = await new ConsumerMediaArtifactResolver(new ConsumerMediaMetadataReader(db), _cas)
            .ResolveMediaAsync(new ConsumerMediaArtifactRequest(5), cancellation.Token);

        result.ShouldNotBeNull();
        result.ContentType.ShouldBe("image/webp");
        result.Content.ShouldBeSameAs(content);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        content.CanRead.ShouldBeTrue();
        await _cas.Received(1).RetrieveAsync(key, cancellation.Token);
    }

    [Fact]
    public async Task ResolveMediaAsync_MissingMedia_DoesNotReadContentStore()
    {
        await using var db = _database.CreateContext();

        var result = await new ConsumerMediaArtifactResolver(new ConsumerMediaMetadataReader(db), _cas)
            .ResolveMediaAsync(new ConsumerMediaArtifactRequest(999));

        result.ShouldBeNull();
        _cas.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveMediaAsync_MissingContent_ReturnsNull()
    {
        await using var db = _database.CreateContext();
        await SeedMediaAsync(db);

        var result = await new ConsumerMediaArtifactResolver(new ConsumerMediaMetadataReader(db), _cas)
            .ResolveMediaAsync(new ConsumerMediaArtifactRequest(5));

        result.ShouldBeNull();
        await _cas.Received(1).RetrieveAsync(
            StorageKey.FromHash(Sha256.Parse(new string('b', 64))), default);
    }

    private static async Task SeedMediaAsync(Romd.Persistence.RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Super Nintendo",
            BaseCompactLabel = "Super Nintendo",
            CanonicalKey = "snes",
            ShortName = "snes",
            CreatedAt = now
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 1,
            PlatformId = 1,
            Name = "Title",
            NormalizedName = "title",
            EnrichmentStatus = "None",
            CreatedAt = now
        });
        db.Files.AddRange(
            new FileEntityPersistence
            {
                Id = 5,
                Sha256 = Sha256.Parse(new string('a', 64)),
                Size = 1,
                SizeOnDisk = 1,
                CreatedAt = now
            },
            new FileEntityPersistence
            {
                Id = 9,
                Sha256 = Sha256.Parse(new string('b', 64)),
                Size = 3,
                SizeOnDisk = 3,
                CreatedAt = now
            });
        db.TitleMedia.Add(new TitleMediaEntity
        {
            Id = 5,
            TitleId = 1,
            Type = "Cover",
            FileId = 9,
            SourceId = "user",
            IsPrimary = true,
            ContentType = "image/webp",
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
