# chdman notices

The ROMD worker image includes `chdman`, MAME's CHD manager, at
`/app/native/chdman/native/chdman`, with its build record beside it in
`/app/native/chdman/build-manifest.json`. The worker runs it as a separate
program; it is not linked into ROMD or into libchdr. libchdr's own notices are
in `native/libchdr/notices/LICENSE.txt`. This directory is installed in the
worker image at `/usr/share/doc/romd/native/chdman/notices/`.

## Source

ROMD's Docker build (the `chdman-build` stage of the `Dockerfile`) compiles
chdman from the MAME 0.289 source archive pinned in `native/chdman/mame.json`:

- archive: `mame0289.tar.gz`, from
  https://github.com/mamedev/mame/archive/refs/tags/mame0289.tar.gz
- size: 213,237,316 bytes
- SHA-256: `0929cc749afabcef892900e10dd90bd8b05f94a7dde8f367ac6a5d2082589f84`
- tag `mame0289`, commit `f34f02505e32c1993c6a782b6814232cbfc74e36`

Docker checks the archive's SHA-256 when it downloads it, and the build checks
its size and SHA-256 again before extracting it. No MAME source file is
patched. The scripts that control the build are part of ROMD's source at the
same release tag: the `Dockerfile`, `native/chdman/mame.json`, the builder
package pins in `native/builder/`, and the build recipe in
`tools/Moonlark.Native.Engineering/Chdman/`.

## Source offer

Each ROMD release that publishes container images attaches this exact archive,
as `mame0289.tar.gz`, to the same GitHub release. The release workflow checks
its size and SHA-256 against `native/chdman/mame.json` before it publishes any
image. Together with the build scripts in ROMD's source for that release, the
archive is the corresponding source of the chdman binary in those images.

## Licenses

`mame-mame0289/COPYING` states that MAME as a whole is distributed under the GNU
General Public License, version 2. The full text is
`mame-mame0289/docs/legal/GPL-2.0`. `src/tools/chdman.cpp` carries a
BSD-3-Clause header; that does not make the linked chdman executable
BSD-licensed. All eight license texts in MAME's `docs/legal/` directory are
included; some cover MAME components that chdman does not link.

## Linked components

The build's linker map shows which static archives contribute to chdman. Their
license texts, copied unchanged from the verified archive, are shipped beside
this file:

| Component | Source in the archive | License texts shipped here |
| --- | --- | --- |
| chdman and MAME's utility library (`libutils`) | `src/tools/chdman.cpp`, `src/lib/util/` | `mame-mame0289/COPYING` and `mame-mame0289/docs/legal/`; per-file headers are in the source |
| MAME's OS-dependent core (`libocore_sdl`) | `src/osd/` | As above |
| LZMA SDK (`lib7z`) | `3rdparty/lzma/C/` | `mame-mame0289/3rdparty/lzma/DOC/7zC.txt` (public domain) |
| FLAC (`libflac`) | `3rdparty/flac/src/libFLAC/` | `mame-mame0289/3rdparty/flac/COPYING.Xiph` (libFLAC's BSD-style license), plus the `COPYING.GPL`, `COPYING.LGPL` and `COPYING.FDL` texts FLAC distributes |
| utf8proc (`libutf8proc`) | `3rdparty/utf8proc/` | `mame-mame0289/3rdparty/utf8proc/LICENSE.md` (MIT and the Unicode data license) |
| zlib (`libzlib`) | `3rdparty/zlib/` | `mame-mame0289/3rdparty/zlib/LICENSE` |
| zstd (`libzstd`) | `3rdparty/zstd/lib/` | `mame-mame0289/3rdparty/zstd/LICENSE` (BSD-3-Clause) and `COPYING` (GPL-2.0), its two license alternatives |
| expat (`libexpat`) | `3rdparty/expat/` | `mame-mame0289/3rdparty/expat/COPYING` (MIT); the archive is on the link line, and its text is included whether or not objects are taken from it |

## Build inputs that are not shipped

GENie, the .NET engineering tool, Clang, GNU Make, binutils and the other
builder packages run during the build; none of them is shipped. The build reads
SDL2 2.32.10 headers when it compiles MAME's SDL OS layer, from
https://github.com/libsdl-org/SDL/releases/download/release-2.32.10/SDL2-2.32.10.tar.gz (SHA-256
`5f5993c530f084535c65a6879e9b26ad441169b3e25d789d83287040a9ca5165`), which the
`Dockerfile` pins. The binary has no SDL library dependency; `SDL2-LICENSE.txt`
is SDL's zlib license, included for those headers.

## Runtime libraries

chdman is built on AlmaLinux 8.10 with the packages pinned in `native/builder/`.
It statically links parts of the C and C++ runtimes:

- libstdc++ and libgcc from GCC Toolset 15 (`gcc-toolset-15-gcc` and
  `gcc-toolset-15-libstdc++-devel` 15.2.1-7.1.el8_10), licensed under GPL-3.0
  with the GCC Runtime Library Exception. Their texts are in
  `runtime/gcc-toolset-15/` (`COPYING3`, `COPYING.RUNTIME` and the package's
  other license files).
- glibc 2.28 startup objects and `libc_nonshared.a` (`glibc-devel`
  2.28-251.el8_10.40). glibc's license texts are in `runtime/glibc-2.28/`.

The image build copies these texts from the same builder that links chdman.
`runtime/SOURCE_REFERENCES.md` names the source packages. The dynamic
dependencies (libc, libm, libpthread, libdl, libutil, libgcc_s and the dynamic
loader) come from the runtime image's operating-system packages, whose notices
are under `/usr/share/doc/` in the image.
