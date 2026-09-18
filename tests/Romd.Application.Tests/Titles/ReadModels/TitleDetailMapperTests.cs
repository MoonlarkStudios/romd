using Romd.Application.Common.Ids;
using Romd.Admin.Application.Titles.ReadModels;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles.ReadModels;

public class TitleDetailMapperTests
{
    [Fact]
    public void ToContract_MapsScalarFields()
    {
        var data = CreateMinimalData();

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        result.Id.ShouldBe(IdCoder.Encode(1));
        result.SystemKey.ShouldBe(TestSystemCatalog.Keys.Required(10));
        result.Name.ShouldBe("Test Title");
        result.EnrichmentStatus.ShouldBe("Completed");
        result.Description.ShouldBe("A description");
        result.Publisher.ShouldBe("Publisher");
        result.Developer.ShouldBe("Developer");
        result.Genre.ShouldBe("Action");
        result.ReleaseDate.ShouldBe(new DateOnly(2024, 1, 15));
        result.Players.ShouldBe(2);
        result.Rating.ShouldBe(8.5);
        result.ContentRatings.Count.ShouldBe(1);
        result.ContentRatings[0].Code.ShouldBe("E");
    }

    [Fact]
    public void ToContract_NullProvenance_MapsToNull()
    {
        var data = CreateMinimalData() with { FieldProvenance = null };

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        result.FieldProvenance.ShouldBeNull();
    }

    [Fact]
    public void ToContract_EmptyProvenance_MapsToNull()
    {
        var data = CreateMinimalData() with
        {
            FieldProvenance = new Dictionary<string, string>()
        };

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        result.FieldProvenance.ShouldBeNull();
    }

    [Fact]
    public void ToContract_PopulatedProvenance_MapsCorrectly()
    {
        var data = CreateMinimalData() with
        {
            FieldProvenance = new Dictionary<string, string>
            {
                ["Description"] = "igdb",
                ["Publisher"] = "user"
            }
        };

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        result.FieldProvenance.ShouldNotBeNull();
        result.FieldProvenance.Count.ShouldBe(2);
        result.FieldProvenance["Description"].ShouldBe("igdb");
        result.FieldProvenance["Publisher"].ShouldBe("user");
    }

    [Fact]
    public void ToContract_CompletionPercent_AllOwned()
    {
        var data = CreateMinimalData() with
        {
            HasLocalPayload = true,
            Releases =
            [
                new TitleReleaseData
                {
                    Id = 100,
                    DatFileId = 200,
                    Name = "Release 1",
                    Files =
                    [
                        CreateFileData(1, romFileId: 50),
                        CreateFileData(2, romFileId: 51)
                    ]
                }
            ]
        };

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        result.CompletionPercent.ShouldBe(100.0);
        result.HasLocalPayload.ShouldBeTrue();
    }

    [Fact]
    public void ToContract_CompletionPercent_NoneOwned()
    {
        var data = CreateMinimalData() with
        {
            Releases =
            [
                new TitleReleaseData
                {
                    Id = 100,
                    DatFileId = 200,
                    Name = "Release 1",
                    Files =
                    [
                        CreateFileData(1, romFileId: null),
                        CreateFileData(2, romFileId: null)
                    ]
                }
            ]
        };

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        result.CompletionPercent.ShouldBe(0.0);
        result.HasLocalPayload.ShouldBeFalse();
    }

    [Fact]
    public void ToContract_CompletionPercent_PartialOwned()
    {
        var data = CreateMinimalData() with
        {
            HasLocalPayload = true,
            Releases =
            [
                new TitleReleaseData
                {
                    Id = 100,
                    DatFileId = 200,
                    Name = "Release 1",
                    Files =
                    [
                        CreateFileData(1, romFileId: 50),
                        CreateFileData(2, romFileId: null),
                        CreateFileData(3, romFileId: null)
                    ]
                }
            ]
        };

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        result.CompletionPercent.ShouldBe(33.3);
        result.HasLocalPayload.ShouldBeTrue();
    }

    [Fact]
    public void ToContract_CompletionPercent_NodumpExcludedFromTotal()
    {
        var data = CreateMinimalData() with
        {
            Releases =
            [
                new TitleReleaseData
                {
                    Id = 100,
                    DatFileId = 200,
                    Name = "Release 1",
                    Files =
                    [
                        CreateFileData(1, romFileId: 50),
                        CreateFileData(2, romFileId: null, status: "nodump")
                    ]
                }
            ]
        };

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        // Only 1 non-nodump file, and it's owned
        result.CompletionPercent.ShouldBe(100.0);
    }

    [Fact]
    public void ToContract_NoReleases_ZeroCompletion()
    {
        var data = CreateMinimalData() with { Releases = [] };

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        result.CompletionPercent.ShouldBe(0.0);
        result.HasLocalPayload.ShouldBeFalse();
        result.Releases.ShouldBeEmpty();
    }

    [Fact]
    public void ToContract_MapsMedia()
    {
        var data = CreateMinimalData() with
        {
            Media =
            [
                new TitleMediaData
                {
                    Id = 5,
                    Type = "Cover",
                    FileId = 99,
                    SourceId = "igdb",
                    IsPrimary = true
                }
            ]
        };

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        result.Media.Count.ShouldBe(1);
        result.Media[0].Id.ShouldBe(IdCoder.Encode(5));
        result.Media[0].Type.ShouldBe("Cover");
        result.Media[0].Url.ShouldBe($"/media/{IdCoder.Encode(5)}");
        result.Media[0].SourceId.ShouldBe("igdb");
        result.Media[0].IsPrimary.ShouldBeTrue();
    }

    [Fact]
    public void ToContract_Release_IsComplete_WhenAllOwnedOrNodump()
    {
        var data = CreateMinimalData() with
        {
            Releases =
            [
                new TitleReleaseData
                {
                    Id = 100,
                    DatFileId = 200,
                    Name = "Release 1",
                    Files =
                    [
                        CreateFileData(1, romFileId: 50),
                        CreateFileData(2, romFileId: null, status: "nodump")
                    ]
                }
            ]
        };

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        result.Releases[0].IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void ToContract_Release_NotComplete_WhenMissingFiles()
    {
        var data = CreateMinimalData() with
        {
            Releases =
            [
                new TitleReleaseData
                {
                    Id = 100,
                    DatFileId = 200,
                    Name = "Release 1",
                    Files =
                    [
                        CreateFileData(1, romFileId: 50),
                        CreateFileData(2, romFileId: null)
                    ]
                }
            ]
        };

        var result = TitleDetailMapper.ToContract(data, TestSystemCatalog.Keys);

        result.Releases[0].IsComplete.ShouldBeFalse();
    }

    private static TitleDetailData CreateMinimalData() =>
        new()
        {
            Id = 1,
            PlatformId = 10,
            Name = "Test Title",
            EnrichmentStatus = "Completed",
            IsTracked = false,
            Description = "A description",
            Publisher = "Publisher",
            Developer = "Developer",
            Genre = "Action",
            ReleaseDate = new DateOnly(2024, 1, 15),
            Players = 2,
            Rating = 8.5,
            ContentRatings =
            [
                new TitleContentRatingData
                {
                    Board = 0,
                    Code = "E",
                    Designation = 0,
                    MinimumAge = 0,
                    SourceId = "igdb"
                }
            ],
            CreatedAt = DateTimeOffset.UnixEpoch,
            LastEnrichedAt = DateTimeOffset.UnixEpoch
        };

    private static TitleFileData CreateFileData(int id, int? romFileId, string? status = null) =>
        new()
        {
            Id = id,
            Name = $"file{id}.bin",
            Size = 1024,
            Sha1 = "abc123",
            Status = status,
            RomFileId = romFileId
        };
}
