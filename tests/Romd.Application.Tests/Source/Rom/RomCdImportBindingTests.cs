using Romd.Admin.Application.Source.Rom.Compressed;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RomCdImportBindingTests
{
    [Fact]
    public void Select_ShuffledCompleteRequirements_BindsExactNamesInMeasuredTrackOrderWithoutOriginalCueClaim()
    {
        var tracks = Tracks();
        var entry = Entry(tracks);
        var result = RomCdImportBindings.Select(tracks, [entry with { Files = entry.Files.Reverse().ToArray() }], ["registered"]);
        result.IsError.ShouldBeFalse();
        result.Value.Bins.Select(bin => bin.Requirement.Name).ShouldBe(["first.bin", "second.bin"]);
        result.Value.Bins.Select(bin => bin.Track.Number).ShouldBe([1, 2]);
        result.Value.Cue.Path.ShouldBe("disc.cue");
        result.Value.Cue.CatalogFileId.ShouldBe(3);
        result.Value.Cue.ReleaseId.ShouldBe(11);
        result.Value.Cue.Sha1.ShouldBe(Hash1('c'));
        result.Value.TitleId.ShouldBe(12);
        result.Value.PlatformId.ShouldBe(13);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("sha1")]
    [InlineData("md5")]
    [InlineData("crc32")]
    [InlineData("missing-hash")]
    [InlineData("foreign-platform")]
    public void Select_PartialOrContradictoryBinIdentity_NeverMatchesBySha1Alone(string mutation)
    {
        var tracks = Tracks();
        var entry = Entry(tracks);
        var files = entry.Files.ToArray();
        files[0] = mutation switch
        {
            "size" => files[0] with { Size = files[0].Size + 1 },
            "sha1" => files[0] with { Sha1 = Hash1('e') },
            "md5" => files[0] with { Md5 = Md5.Parse(new string('e', 32)) },
            "crc32" => files[0] with { Crc32 = Crc32.FromUInt32(999) },
            "missing-hash" => files[0] with { Md5 = null },
            _ => files[0]
        };
        entry = entry with { Files = files, PlatformKey = mutation == "foreign-platform" ? "other" : entry.PlatformKey };
        var result = RomCdImportBindings.Select(tracks, [entry], ["registered"]);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Rom.CdImport.NoCatalogMatch");
    }

    [Theory]
    [InlineData("sidecar")]
    [InlineData("missing-cue")]
    [InlineData("two-cues")]
    [InlineData("missing-cue-hash")]
    [InlineData("unsafe-name")]
    [InlineData("path-collision")]
    [InlineData("disk-requirement")]
    public void Select_TrackMatchesButUnsupportedCompleteRequirements_RefusesWithoutOmittingFiles(string mutation)
    {
        var tracks = Tracks();
        var entry = Entry(tracks);
        var files = entry.Files.ToList();
        switch (mutation)
        {
            case "sidecar":
                files.Add(files[2] with { Id = 4, Name = "disc.sbi", Fingerprint = "sidecar" });
                break;
            case "missing-cue":
                files.RemoveAt(2);
                break;
            case "two-cues":
                files.Add(files[2] with { Id = 4, Name = "other.cue", Fingerprint = "other" });
                break;
            case "missing-cue-hash":
                files[2] = files[2] with { Md5 = null };
                break;
            case "unsafe-name":
                files[0] = files[0] with { Name = "../first.bin" };
                break;
            case "path-collision":
                files[1] = files[1] with { Name = "FIRST.BIN" };
                break;
            case "disk-requirement":
                files[0] = files[0] with { IsDisk = true };
                break;
        }
        var result = RomCdImportBindings.Select(tracks, [entry with { Files = files }], ["registered"]);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Rom.CdImport.UnsupportedLayout");
    }

    [Fact]
    public void Select_TwoEntriesShareTheFullBinSet_RefusesEvenWhenOneNeedsAnUnsupportedSidecar()
    {
        var tracks = Tracks();
        var first = Entry(tracks);
        var second = first with { ReleaseId = 21, Files = [.. first.Files, first.Files[2] with { Id = 4, Name = "disc.sbi" }] };
        var result = RomCdImportBindings.Select(tracks, [first, second], ["registered"]);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Rom.CdImport.AmbiguousCatalog");
    }

    [Fact]
    public void Select_IdenticalTrackHashesCannotChooseDifferentDatNames_RefusesAmbiguousMemberAssignment()
    {
        var tracks = Tracks();
        tracks[1] = tracks[1] with { Identity = tracks[0].Identity };
        var result = RomCdImportBindings.Select(tracks, [Entry(tracks)], ["registered"]);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Rom.CdImport.AmbiguousCatalog");
    }

    [Fact]
    public void Select_CallerMutatesInputCollections_DoesNotChangeTheCapturedBinding()
    {
        var tracks = Tracks();
        var entry = Entry(tracks);
        var files = entry.Files.ToArray();
        var result = RomCdImportBindings.Select(tracks, [entry with { Files = files }], ["registered"]);
        result.IsError.ShouldBeFalse();
        tracks[0] = tracks[1];
        files[0] = files[1];
        result.Value.Bins.Select(bin => bin.Track.Number).ShouldBe([1, 2]);
        result.Value.Bins.Select(bin => bin.Requirement.Name).ShouldBe(["first.bin", "second.bin"]);
    }

    [Theory]
    [InlineData("track-order")]
    [InlineData("partial-frame")]
    [InlineData("empty")]
    public void Select_IncompleteOrContradictoryRawProjection_RefusesBeforeCatalogBinding(string mutation)
    {
        var tracks = Tracks();
        var entry = Entry(tracks);
        if (mutation == "track-order")
            tracks[0] = tracks[0] with { Number = 2 };
        else if (mutation == "partial-frame")
            tracks[0] = tracks[0] with { Identity = Identity('a', 2353) };
        else
            tracks = [];
        var result = RomCdImportBindings.Select(tracks, [entry], ["registered"]);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Rom.CdImport.InvalidMeasurement");
    }

    private static RomCdImportTrack[] Tracks() => [new(1, Identity('a', 4 * 2352)), new(2, Identity('b', 8 * 2352))];
    private static CatalogCdImportEntry Entry(IReadOnlyList<RomCdImportTrack> tracks) => new(11, 12, 13, "registered", "release-fingerprint",
        [File(1, "first.bin", tracks[0].Identity), File(2, "second.bin", tracks[1].Identity), File(3, "disc.cue", Identity('c', 150))]);
    private static CatalogCdFileRequirement File(int id, string name, RomLogicalIdentity identity) =>
        new(id, "file-" + id, name, identity.Size, identity.Sha1, identity.Md5, identity.Crc32, false);
    private static RomLogicalIdentity Identity(char value, long size) => new(size, Sha256.Parse(new string(value, 64)), Hash1(value),
        Md5.Parse(new string(value, 32)), Crc32.FromUInt32(value));
    private static Sha1 Hash1(char value) => Sha1.Parse(new string(value, 40));
}
