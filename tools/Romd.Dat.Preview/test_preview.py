"""CLI contract checks against an already built preview tool; inputs are scratch DATs."""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


TOOL = Path(__file__).parent / "bin/Debug/net10.0/Romd.Dat.Preview.dll"


def xml_dat(*names):
    games = "".join(
        f'<game name="{name}"><rom name="track.bin" size="1" sha1="{i:040x}" /></game>'
        for i, name in enumerate(names, 1)
    )
    return f'<datafile><header><name>Scratch PS1</name></header>{games}</datafile>'


class PreviewCliTests(unittest.TestCase):
    def run_tool(self, *paths):
        return subprocess.run(["dotnet", str(TOOL), *map(str, paths)],
                              capture_output=True, text=True, check=False)

    def test_usage_and_help_do_not_emit_inventory_json(self):
        for args, expected in (([], 2), (["--help"], 0)):
            result = self.run_tool(*args)
            self.assertEqual(result.returncode, expected, result.stderr)
            self.assertEqual(result.stdout, "")
            self.assertIn("Usage:", result.stderr)

    def test_file_size_limit_rejects_before_reading_a_sparse_input(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "too-large.dat"
            with path.open("wb") as stream:
                stream.truncate(256 * 1024 * 1024 + 1)
            result = self.run_tool(path)
            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertEqual(result.stdout, "")
            self.assertIn("256 MiB", result.stderr)

    def test_each_input_has_its_own_scope_and_unchanged_digest(self):
        with tempfile.TemporaryDirectory() as folder:
            paths = [Path(folder) / name for name in ("first.dat", "second.dat")]
            for i, path in enumerate(paths, 1):
                path.write_text(xml_dat(f"Game (USA) (Disc {i})"))
            digests = [hashlib.sha256(p.read_bytes()).hexdigest() for p in paths]
            result = self.run_tool(*paths)
            self.assertEqual(result.returncode, 0, result.stderr)
            sources = json.loads(result.stdout)["Sources"]
            self.assertEqual([s["Sha256"] for s in sources], digests)
            self.assertEqual([hashlib.sha256(p.read_bytes()).hexdigest() for p in paths], digests)
            for source in sources:
                self.assertEqual(source["Preview"]["EntriesExamined"], 1)
                self.assertFalse(source["Preview"]["Groups"][0]["IsNameCandidate"])

    def test_no_partial_json_after_malformed_second_input(self):
        with tempfile.TemporaryDirectory() as folder:
            good, bad = Path(folder) / "good.dat", Path(folder) / "bad.dat"
            good.write_text(xml_dat("Game (Disc 1)", "Game (Disc 2)"))
            bad.write_text('<datafile><header><name>Broken</name></header><game name="Game">')
            result = self.run_tool(good, bad)
            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertEqual(result.stdout, "")
            self.assertIn("Inventory failed", result.stderr)

    def test_empty_unknown_and_missing_input_fail_without_json(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "catalog.dat"
            for text in (xml_dat(), "not a DAT"):
                path.write_text(text)
                result = self.run_tool(path)
                self.assertEqual(result.returncode, 1, result.stderr)
                self.assertEqual(result.stdout, "")
            path.unlink()
            result = self.run_tool(path)
            self.assertEqual(result.returncode, 1)
            self.assertEqual(result.stdout, "")

    def test_reader_warning_prevents_silent_omission_of_a_source_entry(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "catalog.dat"
            path.write_text(xml_dat("Game (Disc 1)", "Game (Disc 2)")
                            .replace("</datafile>", '<game><rom name="a.bin" /></game></datafile>'))
            result = self.run_tool(path)
            self.assertEqual(result.returncode, 1, result.stderr)
            self.assertEqual(result.stdout, "")
            self.assertIn("Reader skipped source data", result.stderr)

    def test_clrmamepro_uses_same_parser_and_number_order(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "catalog.dat"
            path.write_text('clrmamepro ( name "Scratch" description "Scratch" )\n'
                            'game ( name "Game (Disc 2 of 2)" rom ( name "a.bin" size 1 ) )\n'
                            'game ( name "Game (Disc 1 of 2)" rom ( name "b.bin" size 1 ) )\n')
            result = self.run_tool(path)
            self.assertEqual(result.returncode, 0, result.stderr)
            group = json.loads(result.stdout)["Sources"][0]["Preview"]["Groups"][0]
            self.assertEqual([m["DiscNumber"] for m in group["Members"]], [1, 2])
            self.assertTrue(group["HasAllDeclaredDiscNames"])

    def test_duplicate_number_and_unsupported_name_are_serialized_as_review(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "catalog.dat"
            path.write_text(xml_dat("Game (Disc 1)", "game (Disc 1)", "Other (Disk 2)"))
            result = self.run_tool(path)
            self.assertEqual(result.returncode, 0, result.stderr)
            preview = json.loads(result.stdout)["Sources"][0]["Preview"]
            self.assertEqual(preview["ReviewEntries"][0]["Issue"], "UnsupportedDiscMarker")
            self.assertIn("DuplicateDiscNumber", preview["Groups"][0]["ReviewReasons"])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--tool", type=Path, default=TOOL)
    args, remaining = parser.parse_known_args()
    TOOL = args.tool
    unittest.main(argv=[sys.argv[0], *remaining])
