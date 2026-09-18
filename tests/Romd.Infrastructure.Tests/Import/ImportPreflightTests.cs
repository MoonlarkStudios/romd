using Romd.Infrastructure.Import;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Import;

public sealed class ImportPreflightTests
{
    private static readonly ArchiveExtractionLimits Limits = new();

    [Fact]
    public void Validate_FileCountAtLimit_Succeeds()
    {
        var result = ImportPreflight.Validate(
            ImportPreflight.MaxFileCount, totalSourceBytes: 1, availableBytes: long.MaxValue);

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public void Validate_FileCountOverLimit_ReturnsTooManyFiles()
    {
        var result = ImportPreflight.Validate(
            ImportPreflight.MaxFileCount + 1, totalSourceBytes: 1, availableBytes: long.MaxValue);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.TooManyFiles");
        result.FirstError.Description.ShouldContain("100,000");
    }

    [Fact]
    public void Validate_SmallSource_RequiresMinimumOneGigabyteHeadroom()
    {
        long required = 100 + ImportPreflight.MinimumHeadroomBytes;

        ImportPreflight.Validate(1, totalSourceBytes: 100, availableBytes: required).IsError.ShouldBeFalse();

        var result = ImportPreflight.Validate(1, totalSourceBytes: 100, availableBytes: required - 1);
        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.InsufficientSpace");
        result.FirstError.Description.ShouldContain(required.ToString("N0"));
        result.FirstError.Description.ShouldContain((required - 1).ToString("N0"));
    }

    [Fact]
    public void Validate_LargeSource_RequiresTenPercentHeadroom()
    {
        long totalBytes = 100L << 30; // 100GB source: 10% headroom (10GB) exceeds the 1GB floor.
        long required = totalBytes + totalBytes / 10;

        ImportPreflight.Validate(1, totalBytes, availableBytes: required).IsError.ShouldBeFalse();
        ImportPreflight.Validate(1, totalBytes, availableBytes: required - 1)
            .FirstError.Code.ShouldBe("Import.InsufficientSpace");
    }

    [Fact]
    public void ValidateArchiveExpansion_EntryCountAtLimit_Succeeds()
    {
        var result = ImportPreflight.ValidateArchiveExpansion(
            "a.zip",
            Limits.MaxPerArchiveEntries,
            totalUncompressedBytes: 1_000,
            compressedBytes: 1_000,
            Limits);

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public void ValidateArchiveExpansion_EntryCountOverLimit_ReturnsNamedError()
    {
        var result = ImportPreflight.ValidateArchiveExpansion(
            "a.zip",
            Limits.MaxPerArchiveEntries + 1,
            totalUncompressedBytes: 1_000,
            compressedBytes: 1_000,
            Limits);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.ArchiveEntryLimitExceeded");
        result.FirstError.Description.ShouldContain("a.zip");
    }

    [Fact]
    public void ValidateArchiveExpansion_RatioAtLimit_Succeeds()
    {
        var result = ImportPreflight.ValidateArchiveExpansion(
            "a.zip",
            entryCount: 1,
            totalUncompressedBytes: 1_000 * Limits.MaxExpansionRatio,
            compressedBytes: 1_000,
            Limits);

        result.IsError.ShouldBeFalse();
    }

    [Fact]
    public void ValidateArchiveExpansion_RatioOverLimit_ReturnsNamedError()
    {
        var result = ImportPreflight.ValidateArchiveExpansion(
            "a.zip",
            entryCount: 1,
            totalUncompressedBytes: 1_000 * Limits.MaxExpansionRatio + 1,
            compressedBytes: 1_000,
            Limits);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.ArchiveExpansionRatioExceeded");
        result.FirstError.Description.ShouldContain("a.zip");
    }

    [Fact]
    public void ValidateArchiveExpansion_ZeroByteArchiveDeclaringContent_ReturnsRatioError()
    {
        var result = ImportPreflight.ValidateArchiveExpansion(
            "a.zip", entryCount: 1, totalUncompressedBytes: 1, compressedBytes: 0, Limits);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("Import.ArchiveExpansionRatioExceeded");
    }
}
