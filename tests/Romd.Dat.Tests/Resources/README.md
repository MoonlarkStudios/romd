# Synthetic DAT fixtures

`nointro_nintendo_n64.dat` and `redump_sony_ps1.dat` are synthetic Logiqx XML
catalogs written for ROMD tests. Every title, ROM name, size and hash is
invented; the files are not No-Intro or Redump releases and contain no data
from them. `generate.py` produces both files deterministically:

```sh
python3 -I generate.py
```

The file names and headers keep the shape of the two upstream styles that the
parser must accept, so the header field names and the provider name, homepage
and URL values are realistic, while the author list, version and dates are not.

| Fixture | Structure it preserves |
| --- | --- |
| `nointro_nintendo_n64.dat` | No-Intro style: `xsi:schemaLocation` root, header `id`, `homepage` plus `url`, `clrmamepro forcenodump`; game `id`/`cloneofid`; zero, one or two `category` elements; `[BIOS]` entries; `.z64` and `.bin` ROMs with and without `sha256`, `status` (`verified`, `baddump`) and `serial`; an uppercase SHA-1 that the parser must normalize; an escaped `&amp;` |
| `redump_sony_ps1.dat` | Redump style: Logiqx `DOCTYPE`, header `date`; one `category` per game; a `.cue` plus one or more `.bin` tracks per game; disc-name cases for grouping previews: complete sets in several regions (one listed out of order), per-disc role qualifiers, missing disc numbers, discs split across region qualifiers, original and revised sets, a lone disc, an unparenthesized marker that needs review, and disc-like names without a marker |

The tests assert exact entry counts and named entries. Change the generator,
regenerate, and update those expectations together.
