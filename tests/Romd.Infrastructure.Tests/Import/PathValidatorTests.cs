using NSubstitute;
using Romd.Application.Common.Configuration;
using Romd.Infrastructure.Import;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Import;

public sealed class PathValidatorTests : IDisposable
{
    private readonly string _allowedRoot;
    private readonly string _tempRoot;

    public PathValidatorTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("romd-pathvalidator-").FullName;
        _allowedRoot = Path.Combine(_tempRoot, "allowed");
        Directory.CreateDirectory(_allowedRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private PathValidator CreateValidator(params string[] allowedPaths)
    {
        var options = Substitute.For<IRomdOptions>();
        options.AllowedImportPaths.Returns(allowedPaths);
        return new PathValidator(options);
    }

    /// <summary>
    ///     Creates a real symlink, returning false on platforms where creation is not permitted
    ///     (e.g. Windows without developer mode) so the test can skip with a reason.
    /// </summary>
    private static bool TryCreateSymlink(string linkPath, string targetPath, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            else
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void ValidateImportRoot_RelativePath_ReturnsPathNotAbsolute()
    {
        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportRoot(Path.Combine("relative", "path"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.PathNotAbsolute");
    }

    [Fact]
    public void ValidateImportRoot_EmptyAllowlist_ReturnsFeatureDisabled()
    {
        var validator = CreateValidator();

        var result = validator.ValidateImportRoot(_allowedRoot);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.FeatureDisabled");
    }

    [Fact]
    public void ValidateImportRoot_UnlistedRoot_ReturnsPathNotAllowed()
    {
        string unlisted = Path.Combine(_tempRoot, "unlisted");
        Directory.CreateDirectory(unlisted);
        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportRoot(unlisted);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.PathNotAllowed");
    }

    [Fact]
    public void ValidateImportRoot_DotDotEscape_ReturnsPathNotAllowed()
    {
        string escape = Path.Combine(_allowedRoot, "..", "escaped");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "escaped"));
        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportRoot(escape);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.PathNotAllowed");
    }

    [Fact]
    public void ValidateImportRoot_SiblingWithAllowedRootPrefix_ReturnsPathNotAllowed()
    {
        // Boundary safety: "/tmp/x/allowed-evil" must not match allowed root "/tmp/x/allowed".
        string sibling = _allowedRoot + "-evil";
        Directory.CreateDirectory(sibling);
        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportRoot(sibling);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.PathNotAllowed");
    }

    [Fact]
    public void ValidateImportRoot_MissingDirectory_ReturnsPathNotFound()
    {
        string missing = Path.Combine(_allowedRoot, "does-not-exist");
        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportRoot(missing);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.PathNotFound");
    }

    [Fact]
    public void ValidateImportRoot_SymlinkedSourceDirectory_ReturnsLinkRejected()
    {
        string target = Path.Combine(_tempRoot, "outside-target");
        Directory.CreateDirectory(target);
        string link = Path.Combine(_allowedRoot, "linked-source");
        if (!TryCreateSymlink(link, target, isDirectory: true))
        {
            return; // Skip: symlink creation is not permitted on this platform.
        }

        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportRoot(link);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.LinkRejected");
    }

    [Fact]
    public void ValidateImportRoot_SymlinkNestedMidTree_ReturnsLinkRejected()
    {
        string target = Path.Combine(_tempRoot, "outside-mid-target");
        Directory.CreateDirectory(Path.Combine(target, "leaf"));
        string realParent = Path.Combine(_allowedRoot, "real");
        Directory.CreateDirectory(realParent);
        string link = Path.Combine(realParent, "link");
        if (!TryCreateSymlink(link, target, isDirectory: true))
        {
            return; // Skip: symlink creation is not permitted on this platform.
        }

        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportRoot(Path.Combine(link, "leaf"));

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.LinkRejected");
    }

    [Fact]
    public void ValidateImportFile_SymlinkedFile_ReturnsLinkRejected()
    {
        string target = Path.Combine(_tempRoot, "outside-file.bin");
        File.WriteAllBytes(target, [1, 2, 3]);
        string link = Path.Combine(_allowedRoot, "linked-file.bin");
        if (!TryCreateSymlink(link, target, isDirectory: false))
        {
            return; // Skip: symlink creation is not permitted on this platform.
        }

        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportFile(link);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.LinkRejected");
    }

    [Fact]
    public void ValidateImportFile_MissingFile_ReturnsPathNotFound()
    {
        string missing = Path.Combine(_allowedRoot, "missing.bin");
        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportFile(missing);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.PathNotFound");
    }

    [Fact]
    public void ValidateImportRoot_AllowedDirectory_ReturnsContainingAllowedRoot()
    {
        string source = Path.Combine(_allowedRoot, "roms");
        Directory.CreateDirectory(source);
        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportRoot(source);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(_allowedRoot);
    }

    [Fact]
    public void ValidateImportRoot_AllowedRootItself_ReturnsContainingAllowedRoot()
    {
        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportRoot(_allowedRoot);

        result.IsError.ShouldBeFalse();
        result.Value.ShouldBe(_allowedRoot);
    }

    [Fact]
    public void ValidateImportFile_RegularFileUnderAllowedRoot_ReturnsSuccess()
    {
        string source = Path.Combine(_allowedRoot, "roms", "nested");
        Directory.CreateDirectory(source);
        string file = Path.Combine(source, "game.rom");
        File.WriteAllBytes(file, [1, 2, 3]);
        var validator = CreateValidator(_allowedRoot);

        var result = validator.ValidateImportFile(file);

        result.IsError.ShouldBeFalse();
    }
}
