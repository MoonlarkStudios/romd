using Romd.Admin.Application.Titles.Queries.OpenTitleMedia;
using Romd.Domain.Hashing;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class TitleMediaFileReaderTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task FindAsync_ExistingAndMissingMedia_ReturnsFileAndContentTypeOrNull()
    {
        await using var db = _database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
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
            Manufacturer = "Nintendo",
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 1,
            PlatformId = 1,
            Name = "Title",
            NormalizedName = "title",
            EnrichmentStatus = "None",
            FieldProvenanceJson = "{}",
            FieldSourceOverridesJson = "{}",
            ScreenshotPrefsJson = "{}",
            CreatedAt = now,
            CreatedByUserId = userId
        });
        db.Files.Add(new FileEntityPersistence
        {
            Id = 9,
            Sha256 = Sha256.Parse(new string('a', 64)),
            Size = 3,
            SizeOnDisk = 3,
            CreatedAt = now,
            CreatedByUserId = userId
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
            CreatedAt = now,
            CreatedByUserId = userId
        });
        await db.SaveChangesAsync();
        var reader = new TitleMediaFileReader(db);

        (await reader.FindAsync(5, CancellationToken.None)).ShouldBe(new TitleMediaFile(9, "image/webp"));
        (await reader.FindAsync(6, CancellationToken.None)).ShouldBeNull();
    }
}
