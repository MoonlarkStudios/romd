using System.Collections.Immutable;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Core;

/// <summary>The export allowlist is exactly the pinned header's declarations plus the shim.</summary>
public sealed class ExportInventoryTests
{
    /// <summary>The committed allowlist matches the pinned upstream header.</summary>
    [Fact]
    public void CommittedAllowlistMatchesPinnedHeader()
    {
        ImmutableArray<string> allowlist = ExportInventory.ReadAllowlist(TestRepository.Root).Value;
        string header = File.ReadAllText(Path.Combine(TestRepository.Root, "native/libchdr/upstream", Authorities.HeaderPaths[0]));
        Assert.Null(ExportInventory.MatchesHeader(allowlist, header));
        Assert.Equal(ExportInventory.BuildInfoExport, allowlist[^1]);
    }

    /// <summary>Only declarations count; comments, indentation and the first chd_ token in a return type do not.</summary>
    [Fact]
    public void InventoryUsesDeclarationsOnlyInOrder()
    {
        const string header = "/* chd_error chd_create(void); */\n  CHD_EXPORT chd_error chd_indented(void);\n" +
            "CHD_EXPORT const chd_header *chd_get_header(chd_file *chd);\nCHD_EXPORT chd_error chd_read2 (void);\n";
        Assert.Equal<string>(["chd_get_header", "chd_read2", ExportInventory.BuildInfoExport], ExportInventory.FromHeader(header).Value);
    }

    /// <summary>Added, removed or reordered functions all require regeneration.</summary>
    [Theory]
    [InlineData("CHD_EXPORT void chd_a(void);\nCHD_EXPORT void chd_b(void);\nCHD_EXPORT void chd_c(void);\n")]
    [InlineData("CHD_EXPORT void chd_a(void);\n")]
    [InlineData("CHD_EXPORT void chd_b(void);\nCHD_EXPORT void chd_a(void);\n")]
    public void DriftFromHeaderFails(string header) =>
        Assert.NotNull(ExportInventory.MatchesHeader(["chd_a", "chd_b", ExportInventory.BuildInfoExport], header));

    /// <summary>Duplicate, empty and unexpected spellings fail closed.</summary>
    [Theory]
    [InlineData("CHD_EXPORT void chd_a(void);\nCHD_EXPORT void chd_a(void);\n")]
    [InlineData("int nothing;\n")]
    [InlineData("CHD_EXPORT void chd_Upper(void);\n")]
    public void InvalidHeaderInventoryFails(string header) => Assert.False(ExportInventory.FromHeader(header).Succeeded);
}
