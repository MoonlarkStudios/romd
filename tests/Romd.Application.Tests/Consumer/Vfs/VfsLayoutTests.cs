using NSubstitute;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Vfs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Consumer.Vfs;

public sealed class VfsLayoutTests
{
    private static readonly ConsumerLibraryScope Scope = new(Guid.NewGuid());

    [Fact]
    public async Task Service_FileRead_UsesOneIndexedLookupWithoutBuildingReleaseTree()
    {
        var repository = Substitute.For<IConsumerVfsRepository>();
        var file = ResolveFile(repository);
        var result = await Service(repository).ReadAsync(Scope, DefaultLibraryLayout.LayoutKey,
            IdCoder.Encode(1) + " Platform/" + IdCoder.Encode(1) + " Game/" + file.RelativePath, false, 100);
        result.Value.Content.ShouldBe(file);
        result.Value.Listing.Entry.Name.ShouldBe("game.bin");
        await repository.Received(1).GetFileAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repository.DidNotReceive().GetReleaseAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Service_OriginalSetting_ForwardsCurrentFormatForSelectedFileAndDirectoryRead()
    {
        var repository = Substitute.For<IConsumerVfsRepository>();
        var file = new ConsumerVfsFile("Disc/game.iso", Romd.Domain.Hashing.Sha256.Parse(new string('a', 64)), 64);
        var summary = new ConsumerVfsReleaseSummary(1, 1, "Game", "Platform", "gc");
        repository.GetFileAsync(Scope, 1, file.RelativePath, Arg.Any<CancellationToken>(), VfsFormat.Original)
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found(1, new(summary, file, [file.RelativePath])));
        repository.GetReleaseAsync(Scope, 1, Arg.Any<CancellationToken>(), VfsFormat.Original)
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsRelease>.Found(1,
                new(summary, ConsumerVfsReleaseTree.Create([file]).Value)));
        string prefix = IdCoder.Encode(1) + " Platform/" + IdCoder.Encode(1) + " Game";
        var service = Service(repository);
        (await service.ReadAsync(Scope, DefaultLibraryLayout.LayoutKey, prefix + "/" + file.RelativePath,
            false, 100, default, VfsFormat.Original)).Value.Content.ShouldBe(file);
        var directory = await service.ReadAsync(Scope, DefaultLibraryLayout.LayoutKey, prefix, true, 100, default, VfsFormat.Original);
        directory.Value.Listing.Children.ShouldHaveSingleItem().Name.ShouldBe("Disc");
        await repository.Received(1).GetFileAsync(Scope, 1, file.RelativePath, Arg.Any<CancellationToken>(), VfsFormat.Original);
        await repository.Received(1).GetReleaseAsync(Scope, 1, Arg.Any<CancellationToken>(), VfsFormat.Original);
        await repository.DidNotReceive().GetReleaseAsync(Scope, 1, Arg.Any<CancellationToken>(), VfsFormat.Stored);
    }

    [Fact]
    public async Task Service_FileRead_UnportableReleasePathsAreRejectedWithoutContent()
    {
        var repository = Substitute.For<IConsumerVfsRepository>();
        var file = ResolveFile(repository, "DISC/track.bin");
        var result = await Service(repository).ReadAsync(Scope, DefaultLibraryLayout.LayoutKey,
            IdCoder.Encode(1) + " Platform/" + IdCoder.Encode(1) + " Game/" + file.RelativePath, false, 100);
        result.FirstError.ShouldBe(ConsumerVfsErrors.InvalidLayout());
    }

    [Fact]
    public async Task Default_PlatformListing_OmitsUnportableReleasesAndKeepsPaging()
    {
        var repository = Substitute.For<IConsumerVfsRepository>();
        repository.GetPlatformAsync(Scope, 1, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsPlatform>.Found(1, new(1, "Platform")));
        repository.ListReleasesAsync(Scope, 1, 0, 200, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found(1,
                new([new(1, 1, "One", "Platform"), new(2, 1, "Two", "Platform")], 2,
                    new Dictionary<int, IReadOnlyList<string>> { [1] = ["game.bin"], [2] = ["game.bin", "GAME.BIN"] })));
        repository.ListReleasesAsync(Scope, 1, 2, 200, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found(1,
                new([new(3, 1, "Three", "Platform")], null, Portable(3))));

        var result = await new DefaultLibraryLayout(repository).ReadAsync(Scope, IdCoder.Encode(1) + " Platform", true, 2);

        result.Value.Children.Select(entry => entry.Name).ShouldBe([IdCoder.Encode(1) + " One", IdCoder.Encode(3) + " Three"]);
    }

    [Theory]
    [InlineData(" Renamed/", " Game/")]
    [InlineData(" Platform/", " Renamed/")]
    public async Task Service_FileRead_NonCanonicalFoldersAreNotFoundWithoutDirectoryFallback(string platform, string release)
    {
        var repository = Substitute.For<IConsumerVfsRepository>();
        var file = ResolveFile(repository);
        var result = await Service(repository).ReadAsync(Scope, DefaultLibraryLayout.LayoutKey,
            IdCoder.Encode(1) + platform + IdCoder.Encode(1) + release + file.RelativePath, false, 100);
        result.FirstError.Code.ShouldBe(ConsumerVfsErrors.NotFound().Code);
        await repository.DidNotReceive().GetReleaseAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static ConsumerVfsFile ResolveFile(IConsumerVfsRepository repository, params string[] companions)
    {
        var file = new ConsumerVfsFile("Disc/game.bin", Romd.Domain.Hashing.Sha256.Parse(new string('a', 64)), 64);
        repository.GetFileAsync(Scope, 1, file.RelativePath, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.Found(1,
                new(new(1, 1, "Game", "Platform"), file, [file.RelativePath, .. companions])));
        return file;
    }

    private static Dictionary<int, IReadOnlyList<string>> Portable(params int[] releaseIds) =>
        releaseIds.ToDictionary(id => id, _ => (IReadOnlyList<string>)["game.bin"]);

    private static ConsumerVfsService Service(IConsumerVfsRepository repository) =>
        new(new VfsLayoutRegistry([new DefaultLibraryLayout(repository)]), repository,
            Substitute.For<IConsumerVfsContentResolver>());

    [Fact]
    public void Registry_DuplicateKeys_RejectsAmbiguousDispatch()
    {
        var first = Substitute.For<IVfsLayout>();
        var second = Substitute.For<IVfsLayout>();
        first.Key.Returns("library");
        second.Key.Returns("library");

        Should.Throw<InvalidOperationException>(() => new VfsLayoutRegistry([first, second]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Library")]
    [InlineData("../library")]
    [InlineData("library/other")]
    [InlineData("library name")]
    [InlineData("1library")]
    public void Registry_UnsafeKey_RejectsRegistration(string key)
    {
        var layout = Substitute.For<IVfsLayout>();
        layout.Key.Returns(key);

        Should.Throw<InvalidOperationException>(() => new VfsLayoutRegistry([layout]));
    }

    [Fact]
    public async Task Default_EnumerationLimit_StopsBeforeReadingAnotherPage()
    {
        var repository = Substitute.For<IConsumerVfsRepository>();
        repository.GetFileAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound(1));
        repository.ListPlatformsAsync(Scope, 0, 200, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Found(1,
                new([new(1, "First"), new(2, "Second")], 2)));
        var layout = new DefaultLibraryLayout(repository);

        var result = await layout.ReadAsync(Scope, "", true, 1);

        result.FirstError.ShouldBe(ConsumerVfsErrors.EnumerationLimit());
        repository.ReceivedCalls().Count().ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Default_NonadvancingCursor_FailsBoundedly(int next)
    {
        var repository = Substitute.For<IConsumerVfsRepository>();
        repository.GetFileAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound(1));
        repository.ListPlatformsAsync(Scope, 0, 200, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Found(1,
                new([new(1, "First")], 1)));
        repository.ListPlatformsAsync(Scope, 1, 200, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsPlatformPage>.Found(1,
                new([new(2, "Second")], next)));

        var result = await new DefaultLibraryLayout(repository).ReadAsync(Scope, "", true, 10);

        result.FirstError.ShouldBe(ConsumerVfsErrors.InconsistentProjection());
        repository.ReceivedCalls().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Default_PlatformListing_DuplicateReleaseLabelsHaveDistinctPaths()
    {
        var repository = Substitute.For<IConsumerVfsRepository>();
        repository.GetFileAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound(1));
        repository.GetPlatformAsync(Scope, 1, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsPlatform>.Found(1, new(1, "Platform")));
        repository.ListReleasesAsync(Scope, 1, 0, 200, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found(1,
                new([new(1, 1, "Same/Name", "Platform"), new(2, 1, "Same/Name", "Platform")], null, Portable(1, 2))));
        string platform = IdCoder.Encode(1) + " Platform";

        var result = await new DefaultLibraryLayout(repository).ReadAsync(Scope, platform, true, 10);

        result.IsError.ShouldBeFalse();
        result.Value.Children.Select(entry => entry.Path).ShouldBe([
            platform + "/" + IdCoder.Encode(1) + " Same_Name",
            platform + "/" + IdCoder.Encode(2) + " Same_Name"]);
    }

    [Theory]
    [InlineData(2, 1, "Platform")]
    [InlineData(1, 2, "Platform")]
    [InlineData(1, 1, "Renamed")]
    public async Task Default_PlatformListing_InconsistentPageFailsClosed(int libraryId, int platformId, string platformName)
    {
        var repository = Substitute.For<IConsumerVfsRepository>();
        repository.GetFileAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound(1));
        repository.GetPlatformAsync(Scope, 1, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsPlatform>.Found(1, new(1, "Platform")));
        repository.ListReleasesAsync(Scope, 1, 0, 200, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found(libraryId,
                new([new(1, platformId, "Game", platformName)], null, Portable(1))));

        var result = await new DefaultLibraryLayout(repository).ReadAsync(Scope, IdCoder.Encode(1) + " Platform", true, 10);

        result.FirstError.ShouldBe(ConsumerVfsErrors.InconsistentProjection());
    }

    [Fact]
    public async Task Default_PlatformListing_EnforcesReleaseLimit()
    {
        var repository = Substitute.For<IConsumerVfsRepository>();
        repository.GetFileAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound(1));
        repository.GetPlatformAsync(Scope, 1, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsPlatform>.Found(1, new(1, "Platform")));
        repository.ListReleasesAsync(Scope, 1, 0, 200, Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found(1,
                new([new(1, 1, "One", "Platform"), new(2, 1, "Two", "Platform")], 2, Portable(1, 2))));

        var result = await new DefaultLibraryLayout(repository).ReadAsync(Scope, IdCoder.Encode(1) + " Platform", true, 1);

        result.FirstError.ShouldBe(ConsumerVfsErrors.EnumerationLimit());
        repository.ReceivedCalls().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Default_CancelledRead_DoesNotQueryRepository()
    {
        var repository = Substitute.For<IConsumerVfsRepository>();
        repository.GetFileAsync(Arg.Any<ConsumerLibraryScope>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsumerLibraryReadResult<ConsumerVfsResolvedFile>.ItemNotFound(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            new DefaultLibraryLayout(repository).ReadAsync(Scope, "", true, 10, cancellation.Token));

        repository.ReceivedCalls().ShouldBeEmpty();
    }
}
