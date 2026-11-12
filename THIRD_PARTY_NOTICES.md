# Credits and Third-Party Notices

ROMD, including its Ottercade console client, benefits from the work of the
following creators, projects, and organizations. This is the central attribution record for artwork,
browser emulation, and typography used by the project.

| Contribution | Credit | Details |
| --- | --- | --- |
| Platform logo collection | Dan Patrick; respective platform trademark owners | [Platform logos](#platform-logos) |
| Content-rating marks | ACB, CERO, Classificação Indicativa, ESRB, GRAC, PEGI, and USK | [Content-rating marks](#content-rating-marks) |
| Browser emulation | EmulatorJS contributors and the individual emulator-core authors | [EmulatorJS and emulator cores](#emulatorjs-browser-runtime) |
| CHD media | libchdr contributors; MAME contributors | [CHD media: libchdr and chdman](#chd-media-libchdr-and-chdman) |
| Interface typography | Archivo Project Authors; IBM Corp. | [Fonts](#fonts) |
| Bundled brand fonts | DM Sans Project Authors; Fraunces Project Authors | [Fonts](#fonts) |

The root [LICENSE](LICENSE) covers the project's original material under
AGPL-3.0-only. Third-party components retain their own licenses, copyright
notices, and trademark rights; this document does not relicense them. See also
[Trademark Policy](TRADEMARKS.md). Package dependencies retain the notices and
licenses supplied by their authors; this asset-focused document is not a complete
dependency license inventory.

## Managed archive extraction and disc decompression

ROMD uses [SharpCompress 0.50.4](https://github.com/adamhathcock/sharpcompress/tree/0.50.4)
for 7z extraction and for LZMA, LZMA2 and bzip2 decompression in RVZ disc images.
It is a managed runtime dependency licensed under MIT. The upstream license text
follows; package-supplied notices also remain applicable.

```text
The MIT License (MIT)

Copyright (c) 2014  Adam Hathcock

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

## CHD media: libchdr and chdman

- **Moonlark.Libchdr** (`src/Moonlark.Libchdr/`), the safe .NET wrapper over
  libchdr, originated in MoonlarkStudios/romd-native and
  keeps its MIT license in [`src/Moonlark.Libchdr/LICENSE`](src/Moonlark.Libchdr/LICENSE).
- **libchdr** is built from the pinned submodule `native/libchdr/upstream`
  ([rtissera/libchdr `607694c`](https://github.com/rtissera/libchdr/tree/607694ca0812edfc9cc2030c64634fc2393668de)),
  BSD-3-Clause, with bundled miniz (MIT), LZMA SDK (public domain), zstd
  (BSD-3-Clause, selected from its dual license) and dr_flac (MIT-0). The
  complete component texts are in
  [`native/libchdr/notices/LICENSE.txt`](native/libchdr/notices/LICENSE.txt) and
  must accompany every distributed libchdr binary.
- **chdman** is built from the pinned MAME 0.289 source archive
  (`mame0289.tar.gz`, SHA-256
  `0929cc749afabcef892900e10dd90bd8b05f94a7dde8f367ac6a5d2082589f84`). MAME as a
  whole is distributed under GNU GPL version 2; `chdman.cpp` itself is
  BSD-3-Clause, which does not make the linked tool BSD-licensed. Wherever a
  chdman binary is distributed, its complete corresponding source is made
  available alongside it: the pinned archive, which releases attach to the GitHub
  release that publishes the images, together with the build recipe in this
  repository (`tools/Moonlark.Native.Engineering/Chdman/` and the `chdman-build`
  Dockerfile stage). Tool notices are in
  [`native/chdman/notices/`](native/chdman/notices/); MAME's license texts are the
  archive's `COPYING` and `docs/legal/`.

No native binary is committed to this repository; native code is built from
these pins.

## Platform logos

Location: `reference-data/assets/presentation/platforms/`

The 49 PNG files are optimized monochrome derivatives of the “Light - Just
White” recommended set from:

> Console Logos Professionally Redrawn + Official Versions, version 2.1
> Created and curated by Dan Patrick, 2022

[Collection and creator notes](https://archive.org/details/console-logos-professionally-redrawn-plus-official-versions).

Thank you to **Dan Patrick** for creating and curating this collection. ROMD and
Ottercade use optimized monochrome versions to identify platforms in the catalog.
The creator requests credit, permits use outside LaunchBox, and asks that the
logos not be sold. Preserve that credit and the creator's stated conditions when
redistributing the collection. The underlying platform marks belong to their
respective owners and are not covered by ROMD's AGPL license.

Complete repository inventory:

- `2600.png`, `32x.png`, `3do.png`, `3ds.png`, `5200.png`, `7800.png`
- `cdi.png`, `coleco.png`, `dc.png`, `gb.png`, `gba.png`, `gbc.png`, `gc.png`
- `genesis.png`, `gg.png`, `intv.png`, `jaguar.png`, `lynx.png`, `n64.png`
- `nds.png`, `neogeo.png`, `neogeocd.png`, `nes.png`, `ngp.png`, `ngpc.png`
- `ps2.png`, `ps3.png`, `ps4.png`, `ps5.png`, `psp.png`, `psx.png`
- `saturn.png`, `segacd.png`, `sgx.png`, `sms.png`, `snes.png`, `switch.png`
- `tg16.png`, `tgcd.png`, `vb.png`, `vita.png`, `wii.png`, `wiiu.png`
- `ws.png`, `wsc.png`, `x360.png`, `xbox.png`, `xone.png`, `xsx.png`

Additional directory-level details appear in
[Platform logo attribution](reference-data/assets/presentation/platforms/ATTRIBUTION.md).

## Content-rating marks

Location: `reference-data/assets/presentation/ratings/`

ROMD and Ottercade display these marks to identify the classification recorded
for a catalog title. Credit and ownership belong to the respective rating authorities.
Their use does not imply endorsement or a rating of ROMD or Ottercade itself, and the
marks are not covered by the project's AGPL license.

The table below lists the included marks and official authority references.

| Authority | Repository files | Official reference |
| --- | --- | --- |
| Australian Classification Board | `ACB_G.svg`, `ACB_M.svg`, `ACB_MA15+.svg`, `ACB_PG.svg`, `ACB_R18+.svg`, `ACB_RC.svg` | https://www.classification.gov.au/classification-ratings/what-do-ratings-mean |
| Computer Entertainment Rating Organization | `CERO_A.svg`, `CERO_B.svg`, `CERO_C.svg`, `CERO_D.svg`, `CERO_Z.svg` | https://www.cero.gr.jp/en/ |
| Classificação Indicativa | `ClassInd_L.svg`, `ClassInd_10.svg`, `ClassInd_12.svg`, `ClassInd_14.svg`, `ClassInd_16.svg`, `ClassInd_18.svg` | https://www.gov.br/mj/pt-br/assuntos/seus-direitos/classificacao-1 |
| Entertainment Software Rating Board | `ESRB_2013_AO_Rating.svg`, `ESRB_2013_E10+_Rating.svg`, `ESRB_2013_EC_Rating.svg`, `ESRB_2013_E_Rating.svg`, `ESRB_2013_M_Rating.svg`, `ESRB_2013_Rating_Pending.svg`, `ESRB_2013_T_Rating.svg` | https://www.esrb.org/ratings/ |
| Game Rating and Administration Committee | `GRAC_12.svg`, `GRAC_15.svg`, `GRAC_18.svg`, `GRAC_All.svg`, `GRAC_Test.svg` | https://www.grac.or.kr/english/ |
| Pan European Game Information | `PEGI_3.svg`, `PEGI_7.svg`, `PEGI_12.svg`, `PEGI_16.svg`, `PEGI_18.svg` | https://pegi.info/what-do-the-labels-mean |
| Unterhaltungssoftware Selbstkontrolle | `USK_0.svg`, `USK_6.svg`, `USK_12.svg`, `USK_16.svg`, `USK_18.svg` | https://usk.de/en/the-usk-age-ratings/ |

Additional directory-level details appear in
[Content-rating mark attribution](reference-data/assets/presentation/ratings/ATTRIBUTION.md).

## EmulatorJS browser runtime

Thanks to the [EmulatorJS project](https://github.com/EmulatorJS/EmulatorJS)
and the authors and contributors of its emulator cores.

This repository does not contain EmulatorJS or any compiled emulator cores.
Browser playback defaults to the numeric versioned official EmulatorJS CDN
path selected by `web/tools/emulatorjs/pin.json`. Operators can instead point
the dedicated player origin at a compatible mirror. CDN mode discloses the
player's IP address and requested EmulatorJS/core assets to the CDN; it does
not disclose ROMD bearer tokens or signed content-grant URLs. A deterministic
self-hosted bundle is deferred to browser-player Phase 2.

The EmulatorJS runtime is distributed under the
[GNU General Public License, version 3](https://cdn.emulatorjs.org/4.2.3/LICENSE). Each compiled core archive embeds its own license text (`license.txt`
inside the `.data` archive). The default selection includes FCEUmm and Gambatte
(GPL-2.0), mGBA (MPL-2.0), and Snes9x (custom non-commercial/personal-use
terms). The root ROMD AGPL license covers none of these fetched
components.

The [Snes9x license](https://github.com/snes9xgit/snes9x/blob/master/LICENSE)
includes non-commercial restrictions. Operators considering commercial use must
review those terms and exclude the core or obtain permission where required.
Using a CDN does not change a component's license. Preserve upstream notices and
satisfy the applicable source-distribution requirements if hosting your own copies.

## Fonts

Archivo and IBM Plex Mono are used in both the console and web interfaces
(`clients/romd_console/assets/fonts/` and `web/packages/romd-foundation/src/fonts/`).
Fraunces is used for Ottercade's outlined wordmark. ROMD's wordmark is
original vector lettering and uses no font. The DM Sans notice is retained from
the earlier ROMD wordmark exploration. `brand/fonts/` contains compatibility
files retained for compatibility. DM Sans comes from
[Google Fonts](https://github.com/google/fonts/tree/main/ofl/dmsans).

The copied [ROMD product kit](brand/romd/SOURCE.md) also retains its original
[asset and font notices](brand/romd/NOTICES.md), including the complete DM Sans
title-family files. The console's copied [Ottercade product kit](brand/ottercade/SOURCE.md)
retains its [asset and font notices](brand/ottercade/NOTICES.md), including
[Fraunces](brand/ottercade/fonts/fraunces/Fraunces-OFL.txt) for its outlined
wordmark. Title-font adoption is separate; active interface and metadata fonts
remain Archivo and IBM Plex Mono in both clients.

| Typeface | Credit | Use | License text |
| --- | --- | --- | --- |
| Archivo Variable | Copyright 2020 The Archivo Project Authors | Interface typography | [Archivo OFL](web/packages/romd-foundation/src/fonts/Archivo-OFL.txt) |
| IBM Plex Mono | Copyright 2017 IBM Corp.; Reserved Font Name “Plex” | Interface metadata | [IBM Plex Mono OFL](web/packages/romd-foundation/src/fonts/IBMPlexMono-OFL.txt) |
| DM Sans Variable | Copyright 2014 The DM Sans Project Authors | Retained earlier ROMD brand exploration | [DM Sans OFL](web/packages/romd-foundation/src/fonts/DMSans-OFL.txt) |
| Fraunces Variable | Copyright 2018 The Fraunces Project Authors | Ottercade wordmark artwork | [Fraunces OFL](brand/ottercade/fonts/fraunces/Fraunces-OFL.txt) |

All four fonts use the SIL Open Font License 1.1. Their copyright notices and
reserved-name conditions remain in force. Project branding is separately covered
by the [Trademark Policy](TRADEMARKS.md).

## Original project test fixtures

These files are original project material under the root license, not
third-party assets. They are recorded here so their provenance is explicit.

- `tests/Romd.Dat.Parsing.Tests/Resources/`: synthetic DAT catalogs produced by
  the directory's `generate.py`. They follow No-Intro and Redump header
  conventions but contain no data from those projects; the directory's
  `README.md` describes them.

## Game artwork in documentation screenshots

The consumer library capture at `docs/assets/screenshots/library.jpg` shows
game covers acquired through [IGDB](https://www.igdb.com/) and
[SteamGridDB](https://www.steamgriddb.com/) as part of ROMD's interface.
The underlying artwork, game names and marks belong to their respective owners
and are not covered by the project's AGPL license. Their appearance does not
imply endorsement of ROMD. See the [capture provenance](docs/assets/screenshots/README.md)
for the screenshot's scope; private publication review records the exact asset
and applicable conditions. Provider access and attribution do not relicense
the underlying artwork.

## Where these notices are distributed

This file is linked from the repository README and the local asset attribution
files. Deployment bundles include it alongside `LICENSE`, `TRADEMARKS.md`, the
asset attribution files, and the font license texts. Container images carry the
same notice set under `/usr/share/doc/romd/`. The server images add the libchdr
and Moonlark.Libchdr licenses, and the worker image adds the chdman notices and
the license texts they list, at the same repository paths.

When adding or updating assets, update this central record with the creator,
source, repository location, modifications, and applicable terms. Keep supplied
license texts with their components. Preserve these notices when redistributing
project assets, including screenshots that reproduce them.
