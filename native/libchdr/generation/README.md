# libchdr binding generation boundary

`dotnet run --project tools/Moonlark.Native.Engineering -c Release -- generate`
drives the ClangSharpPInvokeGenerator version pinned in `.config/dotnet-tools.json`
(run `dotnet tool restore` once) over `input.h`, the pinned `chd.h` and
`coretypes.h` and the build-info shim. The configuration is typed C# in
`tools/Moonlark.Native.Engineering/Generation/GenerationConfiguration.cs`; there is
no response file. It remaps the fixed-width integer types so that the output does
not depend on the generating host's ABI spellings.

The pinned C error enum's native backing signedness varies by host compiler.
Typed `--with-type chd_error=int` selects the same managed four-byte representation as the public `ChdError`. The tool strips only
the annotation substring `unsigned int`, which occurs only on this enum in the
pinned output. Do not strip `int`: substring replacement would corrupt fixed-width
and callback annotations. All 28 pinned values are nonnegative and fit both
representations. Native probes still record actual signedness, size and
alignment, and ABI tests verify every value plus actual enum arguments and
returns. Regeneration must leave every other annotation unchanged. The emitted
C# remains raw tool output, with no enum rewriting afterward.

The raw ClangSharp output is committed unchanged as
`src/Moonlark.Libchdr/Interop/Libchdr.g.cs`: internal blittable `DllImport`
declarations, with runtime marshalling disabled for the assembly.
`NativeBuildContract.g.cs` is generated from the pin and the header's export list.
The generator verifies the source identity before and after the tool runs, writes
only under `artifacts/generation/libchdr` first (including the exact argument list
in `generation-command.json`), and rejects output whose functions differ from the
header. Reflection tests check the compiled interop boundary.

`generate --check` compares regenerated output with the committed files and fails
on drift; CI runs it. `upstream update --commit <sha>` moves the pin to a reviewed
commit already fetched into the submodule and regenerates the pin, props, export
allowlist, bindings and contract together, restoring all of them if any step
fails. On Linux, the pinned libclang has no builtin headers of its own and
accepts only a version-matched Clang 21 resource directory; Clang 18 headers fail.
Either install Clang 21, which ClangSharp detects, or copy its resource
directory's `include` tree below `artifacts/` (for example
`artifacts/generation/clang-resource/include`) and pass that directory with
`--clang-resources`; the tree is then fingerprinted before and after generation.
