using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class BiosTests
{
    [Fact]
    public void CreateNew_WithValidArguments_InitializesEntry()
    {
        var bios = Bios.CreateNew(7, "[BIOS] Sony PlayStation (USA)", "sonyplaystationusa", createdAt: DateTimeOffset.UnixEpoch);

        bios.Id.ShouldBe(0);
        bios.PlatformId.ShouldBe(7);
        bios.Name.ShouldBe("[BIOS] Sony PlayStation (USA)");
        bios.NormalizedName.ShouldBe("sonyplaystationusa");
        bios.CreatedAt.ShouldBe(DateTimeOffset.UnixEpoch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateNew_WithMissingName_Throws(string? name)
    {
        Should.Throw<ArgumentException>(() => Bios.CreateNew(1, name!, "normalized", createdAt: DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateNew_WithMissingNormalizedName_Throws(string? normalizedName)
    {
        Should.Throw<ArgumentException>(() => Bios.CreateNew(1, "[BIOS] Whatever", normalizedName!, createdAt: DateTimeOffset.UnixEpoch));
    }
}
