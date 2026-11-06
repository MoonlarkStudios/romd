# Moonlark.Libchdr

A safe .NET API with allocation-free steady-state reads over [libchdr](https://github.com/rtissera/libchdr) for
reading CHD (Compressed Hunks of Data) images: headers, hunks, metadata, CD tracks and frames, and full integrity
verification. The verified native libchdr builds are produced separately, by the engineering tool in
`tools/Moonlark.Native.Engineering`.

## Quick start

```csharp
using Moonlark.Libchdr;

using ChdFile chd = ChdFile.Open(args[0]);
byte[] hunk = new byte[chd.Header.HunkBytes];
chd.ReadHunk(0, hunk);
bool verified = ChdIntegrity.Verify(chd).IsVerified;
```

This is a complete top-level program that takes the CHD path as its argument. `Verify` decodes every byte and
reports mismatching or absent checksums in its result rather than throwing. `QuickStartTests` compiles and runs
exactly these statements against a synthetic fixture.

## Safety

libchdr parses its input in native code. Opening a file validates its codecs and its whole hunk map, which
rejects every map the pinned libchdr is known to mishandle, but it cannot make the native decoders memory-safe:
inspect untrusted files in a separate, resource-limited process. A `ChdFile` is not thread-safe: a read that
overlaps another read of the same instance throws `InvalidOperationException`.
