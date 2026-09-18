# Credits and Third-Party Notices

Ottercade (the ROMD repository) benefits from the work of the following creators,
projects, and organizations. This is the central attribution record for artwork,
browser emulation, and typography used by the project.

| Contribution | Credit | Details |
| --- | --- | --- |
| Platform logo collection | Dan Patrick; respective platform trademark owners | [Platform logos](#platform-logos) |
| Content-rating marks | ACB, CERO, Classificação Indicativa, ESRB, GRAC, PEGI, and USK | [Content-rating marks](#content-rating-marks) |
| Browser emulation | EmulatorJS contributors and the individual emulator-core authors | [EmulatorJS and emulator cores](#emulatorjs-browser-runtime) |
| Interface typography | Archivo Project Authors; IBM Corp. | [Fonts](#fonts) |
| Wordmark typography | Funnel Project Authors | [Fonts](#fonts) |

The root [LICENSE](LICENSE) covers the project's original material under
AGPL-3.0-only. Third-party components retain their own licenses, copyright
notices, and trademark rights; this document does not relicense them. See also
[Trademark Policy](TRADEMARKS.md). Package dependencies retain the notices and
licenses supplied by their authors; this asset-focused document is not a complete
dependency license inventory.

## Platform logos

Location: `reference-data/assets/presentation/platforms/`

The 49 PNG files are optimized monochrome derivatives of the “Light - Just
White” recommended set from:

> Console Logos Professionally Redrawn + Official Versions, version 2.1
> Created and curated by Dan Patrick, 2022

[Collection and creator notes](https://archive.org/details/console-logos-professionally-redrawn-plus-official-versions).

Thank you to **Dan Patrick** for creating and curating this collection. Ottercade
uses optimized monochrome versions to identify platforms in the catalog.
The creator requests credit, permits use outside LaunchBox, and asks that the
logos not be sold. Preserve that credit and the creator's stated conditions when
redistributing the collection. The underlying platform marks belong to their
respective owners and are not covered by Ottercade's AGPL license.

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

Ottercade displays these marks to identify the classification recorded for a
catalog title. Credit and ownership belong to the respective rating authorities.
Their use does not imply endorsement or a rating of Ottercade itself, and the
marks are not covered by the project's AGPL license.

The links below identify the authorities. The original download URLs for these
SVG copies were not recorded; these references are not represented as their
acquisition sources or as a separate license grant.

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
terms). The root Ottercade AGPL license covers none of these fetched
components.

The [Snes9x license](https://github.com/snes9xgit/snes9x/blob/master/LICENSE)
includes non-commercial restrictions. Operators considering commercial use must
review those terms and exclude the core or obtain permission where required.
Using a CDN does not change a component's license. Preserve upstream notices and
satisfy the applicable source-distribution requirements if hosting your own copies.

## Fonts

Archivo and IBM Plex Mono are used in both the console and web interfaces
(`clients/romd_console/assets/fonts/` and `web/packages/romd-foundation/src/fonts/`).
Funnel Display is used for the outlined project wordmark; the wordmark does not
load the Funnel font at runtime.

| Typeface | Credit | Use | License text |
| --- | --- | --- | --- |
| Archivo Variable | Copyright 2020 The Archivo Project Authors | Interface typography | [Archivo OFL](web/packages/romd-foundation/src/fonts/Archivo-OFL.txt) |
| IBM Plex Mono | Copyright 2017 IBM Corp.; Reserved Font Name “Plex” | Interface metadata | [IBM Plex Mono OFL](web/packages/romd-foundation/src/fonts/IBMPlexMono-OFL.txt) |
| Funnel Display | Copyright 2025 The Funnel Project Authors | Wordmark artwork | [Funnel OFL](brand/Funnel-OFL.txt) |

All three fonts use the SIL Open Font License 1.1. Their copyright notices and
reserved-name conditions remain in force. Project branding is separately covered
by the [Trademark Policy](TRADEMARKS.md).

## Where these notices are distributed

This file is linked from the repository README and the local asset attribution
files. Deployment bundles include it alongside `LICENSE`, `TRADEMARKS.md`, the
asset attribution files, and the font license texts. Container images carry the
same notice set under `/usr/share/doc/romd/`.

When adding or updating assets, update this central record with the creator,
source, repository location, modifications, and applicable terms. Keep supplied
license texts with their components. Preserve these notices when redistributing
project assets, including screenshots and test goldens that reproduce them.
