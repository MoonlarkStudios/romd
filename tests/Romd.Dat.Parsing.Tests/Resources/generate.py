"""Generate the synthetic Logiqx DAT fixtures for Romd.Dat.Parsing.Tests.

Usage: python3 generate.py [output-directory]   (default: this directory)

Titles are invented. Hashes are slices of SHA-512/SHA-256 digests of a fixed
salt plus the ROM name, so they are stable and well-formed but identify no real
dump. Regenerating changes nothing unless this script changes; update the
expected counts in the tests deliberately when it does.
"""
import hashlib
import sys
from pathlib import Path
from xml.sax.saxutils import escape, quoteattr

SALT = "romd-synthetic-dat-fixture:"


def hashes(name: str):
    d = hashlib.sha512((SALT + name).encode()).hexdigest() + hashlib.sha256((SALT + "x" + name).encode()).hexdigest()
    return d[:8], d[8:40], d[40:80], d[80:144]


def rom(name, size, *, sha256=False, status=None, serial=None, upper_sha1=False):
    crc, md5, sha1, s256 = hashes(name)
    if upper_sha1:
        sha1 = sha1.upper()
    attrs = [("name", name), ("size", str(size)), ("crc", crc), ("md5", md5), ("sha1", sha1)]
    if sha256:
        attrs.append(("sha256", s256))
    if status:
        attrs.append(("status", status))
    if serial:
        attrs.append(("serial", serial))
    return "\t\t<rom " + " ".join(f"{k}={quoteattr(v)}" for k, v in attrs) + "/>"


NOTE = ("<!-- Synthetic ROMD test fixture. Titles, sizes and hashes are invented; "
        "this is not an upstream DAT release. See README.md. -->")


def n64() -> str:
    games = []

    def game(name, roms, *, gid, clone=None, categories=()):
        attrs = f'name={quoteattr(name)} id="{gid}"' + (f' cloneofid="{clone}"' if clone else "")
        lines = [f"\t<game {attrs}>"]
        lines += [f"\t\t<category>{c}</category>" for c in categories]
        lines.append(f"\t\t<description>{escape(name)}</description>")
        lines += roms
        lines.append("\t</game>")
        games.append("\n".join(lines))

    bios = "[BIOS] Synthetic 64 - Boot ROM"
    game(f"{bios} (Brazil)", [rom(f"{bios} (Brazil).bin", 1984, sha256=True, status="verified")], gid="9003", clone="9001")
    game(f"{bios} (Europe)", [rom(f"{bios} (Europe).bin", 1984, sha256=True, status="verified")], gid="9001")
    game(f"{bios} (Japan, USA)", [rom(f"{bios} (Japan, USA).bin", 1984, sha256=True, status="verified")], gid="9002", clone="9001")

    game("Fixture Golf Open (Europe) (En,Fr,De,Es,It)",
         [rom("Fixture Golf Open (Europe) (En,Fr,De,Es,It).z64", 16777216, sha256=True, status="verified", serial="NFGP")],
         gid="0201", clone="0200")
    game("Fixture Golf Open (USA)",
         [rom("Fixture Golf Open (USA).z64", 16777216, sha256=True, status="verified", serial="NFGE")], gid="0200")
    game("Fixture Racer Turbo (USA)",
         [rom("Fixture Racer Turbo (USA).z64", 12582912, sha256=True, serial="NFRE")], gid="0300")
    game("Fixture Racer Turbo (USA) (Rev 1)",
         [rom("Fixture Racer Turbo (USA) (Rev 1).z64", 12582912, sha256=True, status="verified", serial="NFRE")],
         gid="0301", clone="0300")
    game("Example Raiders & Rogues (Europe)",
         [rom("Example Raiders & Rogues (Europe).z64", 8388608, serial="NERP")], gid="0400")
    game("Mock Mansion Mystery (USA) (Proto)",
         [rom("Mock Mansion Mystery (USA) (Proto).z64", 33554432)], gid="0500", categories=("Games", "Preproduction"))
    game("Placeholder Kart League (Japan) (Beta) [b]",
         [rom("Placeholder Kart League (Japan) (Beta) [b].z64", 33554432, sha256=True, status="baddump")],
         gid="0601", clone="0600", categories=("Games", "Preproduction"))
    game("Placeholder Kart League (Japan) (Beta) (2000-07-10) [b]",
         [rom("Placeholder Kart League (Japan) (Beta) (2000-07-10) [b].z64", 33554432, status="baddump", serial="NPKJ")],
         gid="0602", clone="0600", categories=("Games", "Preproduction"))
    game("Placeholder Kart League (Japan)",
         [rom("Placeholder Kart League (Japan).z64", 33554432, sha256=True, status="verified", serial="NPKJ")], gid="0600")
    game("Sample Cheat Cartridge (Europe) (v3.3) (Unl)",
         [rom("Sample Cheat Cartridge (Europe) (v3.3) (Unl).bin", 262144, sha256=True)],
         gid="0701", clone="0700", categories=("Applications",))
    game("Sample Cheat Cartridge (USA) (v2.0) (Unl)",
         [rom("Sample Cheat Cartridge (USA) (v2.0) (Unl).bin", 262144)], gid="0700", categories=("Applications",))
    game("Synthetic Puzzle Garden (Japan)",
         [rom("Synthetic Puzzle Garden (Japan).z64", 4194304, sha256=True, status="verified")], gid="0800")
    game("Synthetic Puzzle Garden (USA) (Kiosk)",
         [rom("Synthetic Puzzle Garden (USA) (Kiosk).z64", 4194304)], gid="0801", clone="0800", categories=("Games",))
    game("Synthetic Star Quest 64 (Europe) (En,Fr,De)",
         [rom("Synthetic Star Quest 64 (Europe) (En,Fr,De).z64", 8388608, sha256=True, status="verified", serial="NSQP")],
         gid="0102", clone="0100")
    game("Synthetic Star Quest 64 (Japan)",
         [rom("Synthetic Star Quest 64 (Japan).z64", 8388608, sha256=True, status="verified", serial="NSQJ",
              upper_sha1=True)],
         gid="0100")
    game("Synthetic Star Quest 64 (USA)",
         [rom("Synthetic Star Quest 64 (USA).z64", 8388608, sha256=True, status="verified", serial="NSQE")],
         gid="0101", clone="0100")
    game("Synthetic Star Quest 64 (USA) (Kiosk)",
         [rom("Synthetic Star Quest 64 (USA) (Kiosk).z64", 8388608)], gid="0103", clone="0100", categories=("Games",))
    game("Test Pattern Sampler (World) (Demo)",
         [rom("Test Pattern Sampler (World) (Demo).z64", 1048576, serial="NTPA")], gid="0900", categories=("Demos",))

    header = """\t<header>
\t\t<id>9999</id>
\t\t<name>Nintendo - Nintendo 64 (BigEndian)</name>
\t\t<description>Nintendo - Nintendo 64 (BigEndian)</description>
\t\t<version>20260101-000000</version>
\t\t<author>Fixture Curator, Synthetic Dumper, Example Verifier</author>
\t\t<homepage>No-Intro</homepage>
\t\t<url>https://www.no-intro.org</url>
\t\t<clrmamepro forcenodump="required"/>
\t</header>"""
    return ('<?xml version="1.0"?>\n'
            '<datafile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
            'xsi:schemaLocation="https://datomatic.no-intro.org/stuff '
            'https://datomatic.no-intro.org/stuff/schema_nointro_datfile_v3.xsd">\n'
            f"\t{NOTE}\n{header}\n" + "\n".join(games) + "\n</datafile>\n")


def ps1() -> str:
    games = []

    def game(name, category="Games", tracks=1):
        lines = [f"\t<game name={quoteattr(name)}>", f"\t\t<category>{category}</category>",
                 f"\t\t<description>{escape(name)}</description>"]
        lines.append(rom(f"{name}.cue", 80 + 70 * tracks))
        if tracks == 1:
            lines.append(rom(f"{name}.bin", 2352 * (150000 + len(name) * 97)))
        else:
            for t in range(1, tracks + 1):
                lines.append(rom(f"{name} (Track {t}).bin", 2352 * (20000 + t * 1531 + len(name))))
        lines.append("\t</game>")
        games.append("\n".join(lines))

    # Single-disc entries without a disc marker, including names that merely look disc-like.
    game("Example Tactics & Tanks (USA)")
    game("Fixture Horror House (Japan) (Rev 1)")
    game("Fixture Horror House (USA)", tracks=2)
    game("Mock Coverdisc Sampler 12 (Europe)", category="Coverdiscs")
    game("Placeholder Music Studio (Europe) (En,Fr,De,Es,It)", category="Applications", tracks=3)
    game("Sample Making-Of Extras (USA)", category="Bonus Discs")
    game("Synthetic Collector's CD Vol. 1 (USA)", category="Coverdiscs")
    game("Synthetic Demo Disc - Shock Sampler (USA) (SLUS-00000)", category="Demos")
    game("Synthetic Skyline Racer (Europe) (En,Fr,De,Es,It)", tracks=4)
    game("Synthetic Skyline Racer (USA)", tracks=4)
    # An unparenthesized disc marker is retained for review rather than grouped.
    game("Synthetic Demo Disc 02 (Europe) (En,Fr,De,Es,It)", category="Demos")

    # Complete three-disc sets in four regions; Europe is listed out of order.
    for region, order in (("USA", (1, 2, 3)), ("Europe", (1, 3, 2)), ("Japan", (1, 2, 3)), ("Germany", (1, 2, 3))):
        for disc in order:
            game(f"Synthetic Saga VII ({region}) (Disc {disc})")
    # Per-disc role qualifiers make each disc its own edition.
    game("Fixture Grand Prix 2 (Europe) (En,Fr,De,Es,It) (Disc 1) (Arcade Mode)")
    game("Fixture Grand Prix 2 (Europe) (En,Fr,De,Es,It) (Disc 2) (Career Mode)")
    # Only discs 2 and 4 are present.
    game("Example Starfighter III - Comet Run (Europe) (Disc 2)")
    game("Example Starfighter III - Comet Run (Europe) (Disc 4)")
    # A complete set, then discs split across different region qualifiers.
    game("Mock Infiltrator 2 (USA) (Disc 1)")
    game("Mock Infiltrator 2 (USA) (Disc 2)")
    game("Mock Infiltrator 2 (Europe, Australia) (Disc 1)")
    game("Mock Infiltrator 2 (Europe) (Disc 2)")
    # Original and revised sets stay separate editions.
    game("Placeholder Stealth Ops (USA) (Disc 1)")
    game("Placeholder Stealth Ops (USA) (Disc 2)")
    game("Placeholder Stealth Ops (USA) (Disc 1) (Rev 1)")
    game("Placeholder Stealth Ops (USA) (Disc 2) (Rev 1)")
    # Mixed qualifiers within one set, an out-of-order pair, an XML entity and a lone disc.
    game("Synthetic Quartet (Japan, Asia) (En,Ja) (Disc 1)")
    game("Synthetic Quartet (Japan, Asia) (En,Ja) (Disc 2)")
    game("Synthetic Quartet (Japan, Asia) (Disc 3) (VR-Disc)")
    game("Sample Survivor 2 (Germany) (Disc 2)")
    game("Sample Survivor 2 (Germany) (Disc 1)")
    game("Sample Survivor 2 (Europe) (Disc 1)")
    game("Example Tactics & Tanks (France) (Disc 1) (Blue)")
    game("Example Tactics & Tanks (France) (Disc 2) (Red)")
    game("Fixture Mist Valley (Europe) (Disc 1)", tracks=2)
    game("Fixture Mist Valley (Europe) (Disc 2)")
    game("Fixture Mist Valley (Europe) (Disc 3)")
    game("Fixture Mist Valley (Europe) (Disc 4)")
    game("Fixture Mist Valley (Europe) (Disc 5)")

    count = len(games)
    header = f"""\t<header>
\t\t<name>Sony - PlayStation</name>
\t\t<description>Sony - PlayStation - Discs ({count}) (2026-01-01 00-00-00)</description>
\t\t<version>2026-01-01 00-00-00</version>
\t\t<date>2026-01-01 00-00-00</date>
\t\t<author>redump.org</author>
\t\t<homepage>redump.org</homepage>
\t\t<url>http://redump.org/</url>
\t</header>"""
    return ('<?xml version="1.0"?>\n'
            '<!DOCTYPE datafile PUBLIC "-//Logiqx//DTD ROM Management Datafile//EN" '
            '"http://www.logiqx.com/Dats/datafile.dtd">\n'
            f"<datafile>\n\t{NOTE}\n{header}\n" + "\n".join(games) + "\n</datafile>\n")


if __name__ == "__main__":
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent
    (out / "nointro_nintendo_n64.dat").write_text(n64())
    (out / "redump_sony_ps1.dat").write_text(ps1())
