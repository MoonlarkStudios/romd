using System.Collections.Immutable;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Generation;
using Moonlark.Native.Engineering.Tests.TestSupport;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Generation;

/// <summary>The raw-output preflight and the contract renderer, checked on synthetic generated text without the tool.</summary>
public sealed class GeneratedBindingsTests
{
    private const string Import = "[DllImport(\"moonlark_chdr\", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]";

    /// <summary>Raw output is committed verbatim; only CRLF line endings are normalized.</summary>
    [Fact]
    public void RawOutputIsKeptVerbatimWithLfLineEndings()
    {
        string generated = Generated("chd_read");
        Assert.Equal(generated, GeneratedBindings.Validate(generated, ["chd_read"]).Value);
        Assert.Equal(generated, GeneratedBindings.Validate(generated.Replace("\n", "\r\n", StringComparison.Ordinal), ["chd_read"]).Value);
    }

    /// <summary>ClangSharp's public members and nested helpers inside internal containers are not externally visible.</summary>
    [Fact]
    public void PublicMembersAndNestedHelpersInsideInternalContainersAreAccepted()
    {
        string source = Generated("chd_read") + "internal partial struct chd_header\n{\n    public partial struct Buffer\n    {\n        public byte e0;\n    }\n}\n";
        Assert.True(GeneratedBindings.Validate(source, ["chd_read"]).Succeeded);
    }

    /// <summary>Indentation cannot hide a public top-level type.</summary>
    [Fact]
    public void IndentedPublicTopLevelTypeIsRejected() =>
        AssertFails(Generated("chd_read") + "    public partial struct LeakedHeader\n{\n}\n", ["chd_read"], "remain internal");

    /// <summary>Only the exact reviewed import attribute is accepted.</summary>
    [Theory]
    [InlineData("[DllImport(\"chdr\")]")]
    [InlineData("[DllImport(\"moonlark_chdr\", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]")]
    [InlineData(Import + "\n    [DllImport(\"other\")]")]
    public void UnknownImportAttributeFails(string attribute) =>
        AssertFails(GeneratedWith(attribute, ["chd_read"]), ["chd_read"], "import attributes");

    /// <summary>A function missing from, or absent in, the pinned header inventory fails before anything is written.</summary>
    [Theory]
    [InlineData("chd_close")]
    [InlineData("chd_read,chd_close")]
    public void MissingOrExtraFunctionFails(string expected) =>
        AssertFails(Generated("chd_read"), expected.Split(','), "disagree with pinned exports");

    /// <summary>The method container must stay internal.</summary>
    [Fact]
    public void PublicRawContainerFails() =>
        AssertFails(Generated("chd_read").Replace("internal static unsafe", "public static unsafe", StringComparison.Ordinal), ["chd_read"], "method container");

    /// <summary>Duplicate generated functions fail even when the names match the inventory.</summary>
    [Fact]
    public void DuplicateFunctionFails() =>
        AssertFails(Generated("chd_read") + Generated("chd_read"), ["chd_read"], "disagree with pinned exports");

    /// <summary>Output from any other generator version is rejected even with a matching manifest.</summary>
    [Fact]
    public void OutputFromAnotherGeneratorVersionFails() =>
        AssertFails(Generated("chd_read").Replace("21.1.8.4", "21.1.8.5", StringComparison.Ordinal), ["chd_read"], "pinned generator");

    /// <summary>Commented declarations never become required imports; the inventory comes from declarations only.</summary>
    [Fact]
    public void ExportInventoryUsesDeclarationsOnly()
    {
        ImmutableArray<string> exports = ExportInventory.FromHeader("/* chd_error chd_create(void); */\nCHD_EXPORT chd_error chd_read(void);\n").Value;
        Assert.Equal<string>(["chd_read", ExportInventory.BuildInfoExport], exports);
        Assert.True(GeneratedBindings.Validate(Generated("chd_read", ExportInventory.BuildInfoExport), exports).Succeeded);
    }

    /// <summary>The committed raw bindings pass the preflight against the pinned header.</summary>
    [Fact]
    public void CommittedBindingsPassPreflightAgainstPinnedHeader()
    {
        string committed = File.ReadAllText(Path.Combine(TestRepository.Root, BindingGenerator.BindingsPath));
        Assert.Equal(committed, GeneratedBindings.Validate(committed, PinnedExports()).Value);
    }

    /// <summary>The committed contract is exactly the rendering of the pin, family version and header inventory.</summary>
    [Fact]
    public void CommittedContractIsRenderedFromThePin()
    {
        LibchdrAuthority authority = Authorities.ReadLibchdr(TestRepository.Root).Value;
        string committed = File.ReadAllText(Path.Combine(TestRepository.Root, BindingGenerator.ContractPath));
        Assert.Equal(committed, NativeBuildContractSource.Render(authority, PinnedExports()).Value);
    }

    /// <summary>Contract exports are ordinally sorted, independent of header declaration order.</summary>
    [Fact]
    public void ContractExportsAreOrdinallySorted()
    {
        LibchdrAuthority authority = Authorities.ReadLibchdr(TestRepository.Root).Value;
        string contract = NativeBuildContractSource.Render(authority, ["chd_read", "chd_close", ExportInventory.BuildInfoExport]).Value;
        Assert.Contains("    [\n        \"chd_close\",\n        \"chd_read\",\n        \"moonlark_chdr_build_info\",\n    ];\n", contract, StringComparison.Ordinal);
    }

    /// <summary>A value that would need C# escaping is refused rather than emitted.</summary>
    [Theory]
    [InlineData("v1\"injected")]
    [InlineData("v1\\n")]
    [InlineData("v1é")]
    public void ContractRefusesValuesNeedingEscapes(string upstreamVersion)
    {
        LibchdrAuthority authority = Authorities.ReadLibchdr(TestRepository.Root).Value;
        LibchdrAuthority altered = authority with { Pin = authority.Pin with { UpstreamVersion = upstreamVersion } };
        Assert.Contains("plain C# string literal", NativeBuildContractSource.Render(altered, PinnedExports()).Failure.Message, StringComparison.Ordinal);
    }

    private static ImmutableArray<string> PinnedExports() =>
        ExportInventory.FromHeader(File.ReadAllText(Path.Combine(TestRepository.Root, GenerationConfiguration.SourcePath, Authorities.HeaderPaths[0]))).Value;

    private static string Generated(params string[] names) => GeneratedWith(Import, names);

    /// <summary>Mirrors the shape ClangSharp emits: public extern members in an internal unsafe partial container.</summary>
    private static string GeneratedWith(string attribute, string[] names) =>
        "using System.CodeDom.Compiler;\nusing System.Runtime.InteropServices;\n\n" +
        "[assembly: GeneratedCode(\"ClangSharp\", \"21.1.8.4\")]\n\nnamespace Moonlark.Libchdr.Interop;\n\n" +
        "internal static unsafe partial class NativeMethods\n{\n" +
        string.Concat(names.Select(name => $"    {attribute}\n    public static extern chd_error {name}(chd_file* chd, [NativeTypeName(\"uint32_t\")] uint hunknum, void* buffer);\n")) +
        "}\n";

    private static void AssertFails(string generated, IReadOnlyCollection<string> expected, string message)
    {
        Result<string> result = GeneratedBindings.Validate(generated, expected);
        Assert.False(result.Succeeded);
        Assert.Contains(message, result.Failure.Message, StringComparison.Ordinal);
    }
}
