# Synthetic 7z import fixtures

These files contain only synthetic bytes, empty files/directories, and a synthetic
nested ZIP. No game data is included. They are checked in so test execution does
not need a 7-Zip installation. Generated with official 7-Zip 26.03 (arm64 macOS).

To regenerate deliberately (Python 3.9+):

```sh
python3 generate.py /absolute/path/to/7zz
```

`commands.txt` records the exact 7-Zip commands, run in the temporary input
directory created by `generate.py`. That script specifies every input byte:
`a.rom` = `alpha`, `b.rom` = `beta`, `tree/nested.rom` = `nested`, `empty.rom`
is empty, and `emptydir` is a directory. `large.rom` is 160,000 bytes generated
by Python `random.Random(1234).randbytes`. Archive timestamps are disabled.
Encryption uses the public test password `fixture`.

| Fixture | Construction/purpose |
| --- | --- |
| `non-solid.7z` | Non-solid LZMA2, raw header, empty file/directories and nested directory |
| `solid.7z` | Solid LZMA2, compressed header, multiple payload entries |
| `lzma.7z` | Solid LZMA, compressed header |
| `large.7z` | Incompressible LZMA2 data spanning multiple bounded writes |
| `encrypted.7z` | AES payload encryption, readable header |
| `encrypted-headers.7z` | AES encrypted header |
| `copy-seed.7z` | Copy coder seed for security header variants |
| `split.7z.001` | First volume of a split archive; other volumes deliberately absent |
| `zip-in-7z.7z` | 7-Zip wraps Python-generated `inner.zip`, containing `inside.rom` = `zip payload` |
| `7z-in-zip.zip` | Python ZIP wraps the exact `solid.7z` fixture |
| `traversal.7z`, `absolute.7z`, `backslash.7z`, `drive.7z` | Raw Copy headers with unsafe paths |
| `nul.7z` | Embedded UTF-16 NUL in a single declared name, followed by extra name bytes |
| `overlong.7z` | Relative path exceeds 1,000 characters |
| `duplicate.7z`, `case-collision.7z`, `parent-collision.7z` | Duplicate, case alias, and file-as-parent conflicts |
| `implicit-parents.7z` | `one/two/a.rom` without explicit directory entries |
| `declared-size.7z` | Declares 1 TiB for five packed bytes, for preflight budget rejection |
| `oversized-dictionary.7z` | LZMA2 property 40 (approximately 4 GiB dictionary); invalid payload is never decoded |
| `oversized-lzma.7z` | LZMA property dictionary of 2 GiB; invalid payload is never decoded |
| `unsupported-coder.7z` | Unknown coder ID FF |
| `anti.7z` | Ordinary `a.rom` plus deletion marker `gone.rom` |
| `truncated.7z` | Last ten bytes removed from `solid.7z` |
| `corrupt.7z` | One payload byte flipped in `large.7z`, headers unchanged |

7-Zip refuses to create some unsafe names or duplicate entries. For those
fixtures, `synthetic()` in `generate.py` re-encodes a minimal raw Copy header
using the seed's synthetic payload bytes. The script writes the precise coder,
pack/unpack sizes, file names and optional empty/anti flags, recomputing both
header CRCs and payload CRCs. It also constructs the deliberately hostile coder
properties and declared size. This avoids relying on a malformed header CRC to
reject the security fixtures. Do not extract them with an unrestricted tool.

ZIP wrapper timestamps/encryption randomness can change on regeneration;
checked-in fixture bytes are the reproducible input for extraction replay tests.

Additional raw-header variants: `ppmd.7z` declares a 2 GiB PPMd model (rejected
coder); `symlink.7z` stores POSIX symlink type bits in WinAttributes;
`backslash-directory.7z` uses valid backslash separators;
`parent-case.7z` aliases an implicit directory's case;
`normalized-collision.7z` uses an internal `one/..` alias;
`overflow-declared.7z` declares two 4 EiB files;
`oversized-header-dictionary.7z` patches the compressed header's LZMA dictionary
to 2 GiB, with recomputed header CRCs. The last fixture must be rejected before
header decoding itself. The exact transformations are in `generate.py`.
