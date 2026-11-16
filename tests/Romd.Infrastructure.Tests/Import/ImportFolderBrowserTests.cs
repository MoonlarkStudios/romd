using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Import;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Import;

public sealed class ImportFolderBrowserTests : IDisposable
{
    private readonly IRomdOptions _options = Substitute.For<IRomdOptions>();
    private readonly string _temp = Directory.CreateTempSubdirectory("romd-folder-browser-").FullName;
    private readonly string _data;
    private readonly string _library;

    public ImportFolderBrowserTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_temp, "data")).FullName;
        _library = Directory.CreateDirectory(Path.Combine(_temp, "library")).FullName;
        _options.DataDirectory.Returns(_data);
        _options.AllowedImportPaths.Returns([_library + Path.DirectorySeparatorChar]);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_temp, true); }
        catch (IOException) { }
    }

    private ImportFolderBrowser Browser() => new(_options, new PathValidator(_options));

    [Fact]
    public void List_NoAllowedRoots_ReportsDisabledWithoutTouchingTheFileSystem()
    {
        _options.AllowedImportPaths.Returns([]);

        var listing = Browser().List(null).Value;

        listing.Enabled.ShouldBeFalse();
        listing.Folders.ShouldBeEmpty();
        Browser().List(_library).Value.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void List_WithoutPath_ReturnsOnlyCanonicalExistingRoots()
    {
        _options.AllowedImportPaths.Returns([_library, _library + Path.DirectorySeparatorChar, Path.Combine(_temp, "missing")]);

        var listing = Browser().List(null).Value;

        listing.Enabled.ShouldBeTrue();
        listing.Path.ShouldBeNull();
        listing.ParentPath.ShouldBeNull();
        listing.Folders.Select(folder => folder.Path).ShouldBe([_library]);
    }

    [Fact]
    public void List_Folder_ReturnsSortedRealSubfoldersAndCountsOnlyDirectFiles()
    {
        foreach (string name in new[] { "snes", "PSX", "gba" })
            Directory.CreateDirectory(Path.Combine(_library, name));
        Directory.CreateDirectory(Path.Combine(_library, ".snapshots"));
        Directory.CreateSymbolicLink(Path.Combine(_library, "data-link"), _data);
        File.WriteAllText(Path.Combine(_library, "top.rom"), "a");
        File.WriteAllText(Path.Combine(_library, "second.rom"), "b");
        File.CreateSymbolicLink(Path.Combine(_library, "linked.rom"), Path.Combine(_library, "top.rom"));
        File.WriteAllText(Path.Combine(_library, "snes", "nested.rom"), "c");

        var listing = Browser().List(_library).Value;

        listing.Path.ShouldBe(_library);
        listing.ParentPath.ShouldBeNull();
        listing.Folders.Select(folder => folder.Name).ShouldBe(["gba", "PSX", "snes"]);
        listing.Folders.ShouldAllBe(folder => folder.Path == Path.Combine(_library, folder.Name));
        listing.FileCount.ShouldBe(2);
        listing.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void List_Subfolder_PointsBackToItsParent()
    {
        string nested = Directory.CreateDirectory(Path.Combine(_library, "psx", "disc-sets")).FullName;

        var listing = Browser().List(nested + Path.DirectorySeparatorChar).Value;

        listing.Path.ShouldBe(nested);
        listing.ParentPath.ShouldBe(Path.Combine(_library, "psx"));
        Browser().List(listing.ParentPath!).Value.ParentPath.ShouldBe(_library);
    }

    [Fact]
    public void List_ManyFolders_StopsAtTheLimitAndSaysSo()
    {
        for (int index = 0; index <= ImportFolderBrowser.MaxFolders; index++)
            Directory.CreateDirectory(Path.Combine(_library, $"folder-{index:D4}"));

        var listing = Browser().List(_library).Value;

        listing.Folders.Count.ShouldBe(ImportFolderBrowser.MaxFolders);
        listing.Truncated.ShouldBeTrue();
    }

    /// <summary>
    ///     The data directory (signing keys, content store) shares the container with the library,
    ///     so every route toward it must be refused before anything is enumerated.
    /// </summary>
    [Theory]
    [InlineData("dot-dot", "Import.PathNotAllowed")]
    [InlineData("data-directory", "Import.PathNotAllowed")]
    [InlineData("filesystem-root", "Import.PathNotAllowed")]
    [InlineData("relative", "Import.PathNotAbsolute")]
    [InlineData("symlink", "Import.LinkRejected")]
    [InlineData("below-symlink", "Import.LinkRejected")]
    [InlineData("missing", "Import.PathNotFound")]
    [InlineData("file", "Import.PathNotFound")]
    public void List_PathOutsideTheAllowedRoots_IsRefused(string route, string code)
    {
        Directory.CreateDirectory(Path.Combine(_data, "keys"));
        string link = Path.Combine(_library, "escape");
        Directory.CreateSymbolicLink(link, _data);
        File.WriteAllText(Path.Combine(_library, "game.rom"), "rom");
        string path = route switch
        {
            "dot-dot" => Path.Combine(_library, "..", "data"),
            "data-directory" => _data,
            "filesystem-root" => Path.GetPathRoot(_library)!,
            "relative" => Path.Combine("library", "psx"),
            "symlink" => link,
            "below-symlink" => Path.Combine(link, "keys"),
            "missing" => Path.Combine(_library, "absent"),
            "file" => Path.Combine(_library, "game.rom"),
            _ => throw new ArgumentOutOfRangeException(nameof(route))
        };

        var result = Browser().List(path);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe(code);
    }

    [Fact]
    public void List_FolderReplacedByALinkWhileListing_ReturnsNoNames()
    {
        Directory.CreateDirectory(Path.Combine(_library, "psx"));
        var validator = Substitute.For<IPathValidator>();
        validator.ValidateImportRoot(_library).Returns(
            _library, (ErrorOr<string>)ImportErrors.LinkRejected(_library));

        var result = new ImportFolderBrowser(_options, validator).List(_library);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.LinkRejected");
    }
}
