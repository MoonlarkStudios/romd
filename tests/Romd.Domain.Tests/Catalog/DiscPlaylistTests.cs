using System.Security.Cryptography;
using System.Text;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class DiscPlaylistTests
{
    [Fact]
    public void Layout_MutableInputAndDowncastViews_CannotChangeDerivedPlaylistFacts()
    {
        var entries = new List<string> { "disc-01/a.cue", "disc-02/b.cue" };
        var playlist = DiscPlaylistLayout.Create(entries);
        var expectedText = playlist.Text;
        var expectedHash = playlist.Sha256;
        entries[0] = "replaced.cue";
        entries.Clear();

        var exposed = (IList<string>)playlist.EntryPoints;
        Should.Throw<NotSupportedException>(() => exposed[0] = "replaced.cue");
        Should.Throw<NotSupportedException>(() => exposed.Add("extra.cue"));
        Should.Throw<NotSupportedException>(exposed.Clear);

        playlist.EntryPoints.ShouldBe(["disc-01/a.cue", "disc-02/b.cue"]);
        playlist.Text.ShouldBe(expectedText);
        playlist.Text.ShouldBe(string.Join('\n', playlist.EntryPoints) + "\n");
        playlist.Sha256.ShouldBe(expectedHash);
        playlist.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(playlist.Text))));
        foreach (var property in typeof(DiscPlaylistLayout).GetProperties())
            property.SetMethod.ShouldBeNull("Derived facts cannot be changed independently through a record initializer.");
        typeof(DiscPlaylistLayout).GetConstructors().ShouldBeEmpty();
    }

    [Fact]
    public void TryCreate_RepeatedNamesAcrossDiscs_PreservesCompanionsAndOrder()
    {
        var discs = new (IReadOnlyList<string>, string)[] { (new[] { "tracks/game.cue", "tracks/track.bin", "tracks/game.sbi" }, "tracks/game.cue"),
            (new[] { "tracks/game.cue", "tracks/track.bin" }, "tracks/game.cue") };
        DiscPlaylist.TryCreate(discs, out var playlist, out var error).ShouldBeTrue();
        error.ShouldBeNull();
        playlist!.Text.ShouldBe("disc-01/tracks/game.cue\ndisc-02/tracks/game.cue\n");
        playlist.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(playlist.Text))));
        DiscPlaylist.TryCreate(discs, out var again, out _).ShouldBeTrue();
        again!.Sha256.ShouldBe(playlist.Sha256);
    }

    [Theory]
    [InlineData("../game.cue")]
    [InlineData("/game.cue")]
    [InlineData("C:/game.cue")]
    [InlineData("CON.cue")]
    [InlineData("game\n.cue")]
    [InlineData("game\\file.cue")]
    public void TryCreate_UnsafePath_RejectsWithoutArtifact(string path) =>
        DiscPlaylist.TryCreate(new (IReadOnlyList<string>, string)[] { (new[] { path }, path), (new[] { "b.iso" }, "b.iso") }, out _, out _).ShouldBeFalse();

    [Theory]
    [InlineData("GAME.cue", "game.cue")]
    [InlineData("game.cue", "game.cue")]
    [InlineData("dir", "dir/game.cue")]
    [InlineData("é.cue", "é.cue")]
    public void TryCreate_PathAliasesOrDirectoryCollision_Rejects(string first, string second) =>
        DiscPlaylist.TryCreate(new (IReadOnlyList<string>, string)[] { (new[] { first, second }, first), (new[] { "b.iso" }, "b.iso") }, out _, out _).ShouldBeFalse();

    [Fact]
    public void TryCreate_EntryPointNotInFiles_Rejects() =>
        DiscPlaylist.TryCreate(new (IReadOnlyList<string>, string)[] { (new[] { "track.bin" }, "game.cue"), (new[] { "b.iso" }, "b.iso") }, out _, out _).ShouldBeFalse();
}
