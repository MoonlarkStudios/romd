using Romd.Consumer.Application.Vfs;
using Romd.Domain.Hashing;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Vfs;

public sealed class ConsumerVfsReleaseTreeTests
{
    private static readonly Sha256 Hash = Sha256.Parse(new string('a', 64));

    [Fact]
    public void Create_CompanionFiles_PreservesPathsAndListsImmediateChildren()
    {
        var result = ConsumerVfsReleaseTree.Create([
            File("Disc 1/game.cue"), File("Disc 1/track 01.bin"), File("playlist.m3u"),
            File("Disc 1/extras/readme.txt"), File("100% complete #1.bin")]);

        result.IsError.ShouldBeFalse();
        ConsumerVfsReleaseTree tree = result.Value;
        tree.List("")!.Select(entry => entry.Name)
            .ShouldBe(["100% complete #1.bin", "Disc 1", "playlist.m3u"]);
        tree.List("Disc 1")!.Select(entry => entry.Name).ShouldBe(["extras", "game.cue", "track 01.bin"]);
        tree.Find("Disc 1/game.cue")!.File.ShouldBe(File("Disc 1/game.cue"));
        tree.Find("disc 1/game.cue").ShouldBeNull();
        tree.List("playlist.m3u").ShouldBeNull();
        tree.List("missing").ShouldBeNull();
    }

    [Theory]
    [InlineData("../game.bin")]
    [InlineData("./game.bin")]
    [InlineData("/game.bin")]
    [InlineData("folder//game.bin")]
    [InlineData("folder/game.bin/")]
    [InlineData("folder\\game.bin")]
    [InlineData("C:/game.bin")]
    [InlineData("game.bin.")]
    [InlineData("game.bin ")]
    [InlineData("NUL.bin")]
    [InlineData("com1.bin")]
    [InlineData("LPT¹.bin")]
    [InlineData("aux .bin")]
    [InlineData("bad\u0000.bin")]
    [InlineData("bad\uffff.bin")]
    [InlineData("")]
    public void Create_UnsafePath_RejectsLayout(string path)
    {
        ConsumerVfsReleaseTree.Create([File(path)]).IsError.ShouldBeTrue();
    }

    [Theory]
    [InlineData("game.bin", "game.bin")]
    [InlineData("game.bin", "GAME.BIN")]
    [InlineData("disc/game.bin", "Disc/other.bin")]
    [InlineData("disc", "disc/game.bin")]
    [InlineData("disc/game.bin", "disc")]
    [InlineData("caf\u00e9.bin", "cafe\u0301.bin")]
    public void Create_AmbiguousPaths_RejectsLayoutInEitherOrder(string first, string second)
    {
        ConsumerVfsReleaseTree.Create([File(first), File(second)]).IsError.ShouldBeTrue();
        ConsumerVfsReleaseTree.Create([File(second), File(first)]).IsError.ShouldBeTrue();
    }

    [Fact]
    public void Create_EmptyRelease_ContainsEmptyRoot()
    {
        ConsumerVfsReleaseTree tree = ConsumerVfsReleaseTree.Create([]).Value;
        tree.Find("")!.IsDirectory.ShouldBeTrue();
        tree.List("")!.ShouldBeEmpty();
    }

    [Fact]
    public void Create_InvalidMetadata_RejectsLayout()
    {
        ConsumerVfsReleaseTree.Create([new("game.bin", default, 1)]).IsError.ShouldBeTrue();
        ConsumerVfsReleaseTree.Create([new("game.bin", Hash, -1)]).IsError.ShouldBeTrue();
        ConsumerVfsReleaseTree.Create([new("game.bin", Hash, 0)]).IsError.ShouldBeFalse();
    }

    [Fact]
    public void Create_ExcessiveInput_StopsEnumerationAtBound()
    {
        int enumerated = 0;
        IEnumerable<ConsumerVfsFile> Files()
        {
            while (true)
            {
                yield return File($"game{enumerated++}.bin");
            }
        }

        ConsumerVfsReleaseTree.Create(Files()).IsError.ShouldBeTrue();
        enumerated.ShouldBe(ConsumerVfsReleaseTree.MaximumFiles + 1);
    }

    [Fact]
    public void Create_ExcessivePathDimensions_RejectsLayout()
    {
        ConsumerVfsReleaseTree.Create([File(new string('a', 256))]).IsError.ShouldBeTrue();
        ConsumerVfsReleaseTree.Create([File(new string('\u00e9', 128))]).IsError.ShouldBeTrue();
        ConsumerVfsReleaseTree.Create([File(string.Join('/', Enumerable.Repeat("a", 33)))]).IsError.ShouldBeTrue();
        ConsumerVfsReleaseTree.Create([File("bad" + '\ud800')]).IsError.ShouldBeTrue();
        ConsumerVfsReleaseTree.Create([File(string.Join('/', Enumerable.Repeat(new string('a', 255), 17)))])
            .IsError.ShouldBeTrue();
    }

    [Fact]
    public void Create_ManyDirectories_EnforcesTotalEntryBound()
    {
        ConsumerVfsReleaseTree.Create(Enumerable.Range(0, 7000)
            .Select(index => File($"{index}/disc/track.bin"))).IsError.ShouldBeTrue();
    }

    private static ConsumerVfsFile File(string path) => new(path, Hash, 123);
}
