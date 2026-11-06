using System.Collections.Immutable;

namespace Moonlark.Native.Engineering.Generation;

/// <summary>
/// The reviewed ClangSharp invocation as typed data. Arguments reach the tool through
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, so no response file, shell or extra include can widen it.
/// </summary>
internal sealed record GenerationConfiguration(
    string File,
    ImmutableArray<string> Traverse,
    ImmutableArray<string> IncludeDirectories,
    string Language,
    string Standard,
    string Namespace,
    string LibraryPath,
    string MethodClassName,
    string AccessSpecifier,
    string CallingConvention,
    ImmutableArray<string> Remaps,
    ImmutableArray<string> TypeRemaps,
    ImmutableArray<string> TypeOverrides,
    ImmutableArray<string> NativeTypeNamesToStrip,
    ImmutableArray<string> Config,
    ImmutableArray<string> Generate,
    string OutputDirectory,
    string OutputFileName)
{
    internal const string EntryPath = "native/libchdr/generation/input.h";
    internal const string SourcePath = "native/libchdr/upstream";
    internal const string ShimPath = "native/libchdr/moonlark_chdr_build_info.h";

    internal static GenerationConfiguration Libchdr { get; } = new(
        File: EntryPath,
        Traverse: [SourcePath + "/include/libchdr/chd.h", SourcePath + "/include/libchdr/coretypes.h", ShimPath],
        // Exclusively the verified upstream tree; the shim is included by explicit path, never through a wrapper root.
        IncludeDirectories: [SourcePath + "/include"],
        Language: "c",
        Standard: "c11",
        Namespace: "Moonlark.Libchdr.Interop",
        LibraryPath: "moonlark_chdr",
        MethodClassName: "NativeMethods",
        AccessSpecifier: "*=Internal",
        CallingConvention: "*=Cdecl",
        // Fixed-width remaps keep host ABI spellings out (LP64 Linux emits nuint for uint64_t).
        // Plain char occurs only behind pointers in the pinned API. Keep the existing sbyte* spelling
        // on unsigned-char hosts such as Linux ARM64; pointer width and native bytes are unchanged.
        Remaps:
        [
            "_chd_error=chd_error", "_chd_file=chd_file", "_chd_header=chd_header", "FILE=void", "char=sbyte",
            "int32_t=int", "uint32_t=uint", "int64_t=long", "uint64_t=ulong",
        ],
        TypeRemaps:
        [
            "chd_core_file_callbacks=core_file_callbacks",
            "chd_core_file_callbacks_and_argp=core_file_callbacks_and_argp",
            "chd_core_file=core_file",
        ],
        // Native chd_error signedness varies by host; its measured width, values and calls remain ABI-checked.
        // Use a common signed backing; strip only the unsigned spelling so fixed-width and callback names remain intact.
        TypeOverrides: ["chd_error=int"],
        NativeTypeNamesToStrip: ["unsigned int"],
        Config: ["codegen=latest", "file=single"],
        Generate: ["file-scoped-namespaces", "helper-types", "funcs-with-body=false", "using-statics-for-enums=false"],
        OutputDirectory: "artifacts/generation/libchdr",
        OutputFileName: "Libchdr.g.cs");

    /// <summary>Root-relative output; the tool runs from the repository root.</summary>
    internal string Output => OutputDirectory + "/" + OutputFileName;

    /// <summary>The tool arguments in reviewed order, followed by host-only arguments such as the macOS sysroot.</summary>
    internal ImmutableArray<string> ToolArguments(IReadOnlyList<string> platformArguments) =>
    [
        .. Option("--file", [File]),
        .. Option("--traverse", Traverse),
        .. Option("--include-directory", IncludeDirectories),
        .. Option("--language", [Language]),
        .. Option("-std", [Standard]),
        .. Option("--namespace", [Namespace]),
        .. Option("--library-path", [LibraryPath]),
        .. Option("--method-class-name", [MethodClassName]),
        .. Option("--with-access-specifier", [AccessSpecifier]),
        .. Option("--with-callconv", [CallingConvention]),
        .. Option("--remap", Remaps),
        .. Option("--remap-type", TypeRemaps),
        .. Option("--with-type", TypeOverrides),
        .. Option("--native-type-names-to-strip", NativeTypeNamesToStrip),
        .. Option("--config", Config),
        .. Option("--generate", Generate),
        .. Option("--output", [Output]),
        .. platformArguments,
    ];

    private static IEnumerable<string> Option(string name, IEnumerable<string> values) =>
        values.SelectMany(value => (string[])[name, value]);
}
