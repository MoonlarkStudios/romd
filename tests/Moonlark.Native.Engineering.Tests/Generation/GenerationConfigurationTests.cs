using System.Collections.Immutable;
using Moonlark.Native.Engineering.Core;
using Moonlark.Native.Engineering.Generation;
using Xunit;

namespace Moonlark.Native.Engineering.Tests.Generation;

/// <summary>
/// The typed ClangSharp configuration is the reviewed argument list. These replace the response-file policing tests:
/// any added, changed, duplicated or removed option, remap or include is a reviewed source edit that fails here.
/// </summary>
public sealed class GenerationConfigurationTests
{
    private static readonly string[] ReviewedArguments =
    [
        "--file", "native/libchdr/generation/input.h",
        "--traverse", "native/libchdr/upstream/include/libchdr/chd.h",
        "--traverse", "native/libchdr/upstream/include/libchdr/coretypes.h",
        "--traverse", "native/libchdr/moonlark_chdr_build_info.h",
        "--include-directory", "native/libchdr/upstream/include",
        "--language", "c",
        "-std", "c11",
        "--namespace", "Moonlark.Libchdr.Interop",
        "--library-path", "moonlark_chdr",
        "--method-class-name", "NativeMethods",
        "--with-access-specifier", "*=Internal",
        "--with-callconv", "*=Cdecl",
        "--remap", "_chd_error=chd_error",
        "--remap", "_chd_file=chd_file",
        "--remap", "_chd_header=chd_header",
        "--remap", "FILE=void",
        "--remap", "char=sbyte",
        "--remap", "int32_t=int",
        "--remap", "uint32_t=uint",
        "--remap", "int64_t=long",
        "--remap", "uint64_t=ulong",
        "--remap-type", "chd_core_file_callbacks=core_file_callbacks",
        "--remap-type", "chd_core_file_callbacks_and_argp=core_file_callbacks_and_argp",
        "--remap-type", "chd_core_file=core_file",
        "--with-type", "chd_error=int",
        "--native-type-names-to-strip", "unsigned int",
        "--config", "codegen=latest",
        "--config", "file=single",
        "--generate", "file-scoped-namespaces",
        "--generate", "helper-types",
        "--generate", "funcs-with-body=false",
        "--generate", "using-statics-for-enums=false",
        "--output", "artifacts/generation/libchdr/Libchdr.g.cs",
    ];

    private static ImmutableArray<string> Arguments => GenerationConfiguration.Libchdr.ToolArguments([]);

    /// <summary>The tool receives exactly the reviewed options and values, in order.</summary>
    [Fact]
    public void TypedConfigurationIsExactlyTheReviewedArgumentList() => Assert.Equal<string>(ReviewedArguments, Arguments);

    /// <summary>Without these, ClangSharp emits host ABI types, e.g. nuint for uint64_t on LP64 Linux.</summary>
    [Theory]
    [InlineData("int32_t=int")]
    [InlineData("uint32_t=uint")]
    [InlineData("int64_t=long")]
    [InlineData("uint64_t=ulong")]
    public void FixedWidthIntegerRemapsArePresent(string remap) =>
        Assert.Single(Pairs(Arguments), pair => pair == ("--remap", remap));

    /// <summary>Plain C char is unsigned on Linux ARM64; its pointer representation must remain stable across hosts.</summary>
    [Fact]
    public void PlainCharPointerRemapIsHostIndependent() =>
        Assert.Single(Pairs(Arguments), pair => pair == ("--remap", "char=sbyte"));

    /// <summary>The signed enum backing is stable while only the unsigned host backing annotation is removed.</summary>
    [Fact]
    public void EnumPortabilityOptionsAreExplicitAndClosed()
    {
        (string Option, string Value)[] pairs = Pairs(Arguments);
        Assert.Equal<string>(["chd_error=int"], Values(pairs, "--with-type"));
        Assert.Equal<string>(["unsigned int"], Values(pairs, "--native-type-names-to-strip"));
    }

    /// <summary>Includes and inputs are exclusively the verified upstream tree, the entry header and the explicit shim.</summary>
    [Fact]
    public void InputsAndIncludesAreExclusivelyTheVerifiedTree()
    {
        (string Option, string Value)[] pairs = Pairs(Arguments);
        Assert.Equal<string>(["native/libchdr/upstream/include"], Values(pairs, "--include-directory"));
        Assert.Equal<string>(["native/libchdr/generation/input.h"], Values(pairs, "--file"));
        Assert.Equal<string>(["native/libchdr/upstream/include/libchdr/chd.h", "native/libchdr/upstream/include/libchdr/coretypes.h",
            "native/libchdr/moonlark_chdr_build_info.h"], Values(pairs, "--traverse"));
    }

    /// <summary>The tool can never be told to write the tracked bindings; its only output is under the ignored artifacts tree.</summary>
    [Fact]
    public void OutputIsOnlyTheArtifactsGenerationDirectory()
    {
        string output = Assert.Single(Values(Pairs(Arguments), "--output"));
        Assert.Equal(GenerationConfiguration.Libchdr.Output, output);
        string root = Path.GetTempPath();
        Assert.True(ArtifactsPath.IsWithin(Path.GetFullPath(output, root), Path.GetFullPath("artifacts/generation/libchdr", root)));
        Assert.NotEqual(BindingGenerator.BindingsPath, output);
    }

    /// <summary>No value can name a nested response file, smuggle an option or split into a second argument.</summary>
    [Fact]
    public void NoValueIsAResponseFileOrArgumentEscape()
    {
        Assert.Equal(0, Arguments.Length % 2);
        Assert.All(Pairs(Arguments), pair =>
        {
            Assert.StartsWith("-", pair.Option, StringComparison.Ordinal);
            Assert.NotEmpty(pair.Value);
            Assert.False("@-#".Contains(pair.Value[0], StringComparison.Ordinal), pair.Value);
            if (pair.Value.Any(char.IsWhiteSpace))
                Assert.Equal(("--native-type-names-to-strip", "unsigned int"), pair);
        });
    }

    /// <summary>Host-only arguments such as the macOS sysroot follow the reviewed list unchanged.</summary>
    [Fact]
    public void PlatformArgumentsFollowTheReviewedList() =>
        Assert.Equal<string>([.. ReviewedArguments, "--additional=-isysroot/sdk"], GenerationConfiguration.Libchdr.ToolArguments(["--additional=-isysroot/sdk"]));

    private static (string Option, string Value)[] Pairs(ImmutableArray<string> arguments) =>
        Enumerable.Range(0, arguments.Length / 2).Select(index => (arguments[2 * index], arguments[(2 * index) + 1])).ToArray();

    private static string[] Values((string Option, string Value)[] pairs, string option) =>
        pairs.Where(pair => pair.Option == option).Select(pair => pair.Value).ToArray();
}
