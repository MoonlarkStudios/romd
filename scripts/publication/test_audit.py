#!/usr/bin/env python3
"""Real-scanner regression tests; fixtures and all findings live outside Git."""

import argparse
from contextlib import redirect_stdout
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import audit as audit_runner

SCANNER = None
SCRIPT = Path(__file__).with_name("audit.py")


def git(repo, *args):
    return subprocess.run(["git", "-C", str(repo), *args], check=True,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout.decode().strip()


class PublicationAuditTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="romd-publication-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repo = self.root / "source"
        self.repo.mkdir()
        git(self.repo, "init", "-b", "main")
        git(self.repo, "config", "user.name", "Publication test")
        git(self.repo, "config", "user.email", "publication-test@example.invalid")
        (self.repo / "README").write_text("Clean synthetic publication fixture.\n")
        self.commit()

    def commit(self):
        git(self.repo, "add", ".")
        git(self.repo, "commit", "-m", "test fixture")

    def secret(self):
        # Assemble a harmless invalid credential at runtime so test source does
        # not itself require a secret-scanner exception.
        return "ghp_" + "m7V4b8J2D9s5R1q6K3" + "n0A4t8W2x9Z5c1F7h6"

    def audit(self, evidence="evidence"):
        return subprocess.run([sys.executable, str(SCRIPT), "--source", str(self.repo),
                               "--evidence", str(self.root / evidence), "--gitleaks", str(SCANNER)],
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    def audit_in_process(self, evidence="evidence"):
        args = argparse.Namespace(source=str(self.repo), evidence=self.root / evidence,
                                  gitleaks=SCANNER, probe_access=False)
        output = io.StringIO()
        previous_umask = os.umask(0o077)
        try:
            with redirect_stdout(output):
                status = audit_runner.audit(args)
        finally:
            os.umask(previous_umask)
        return status, json.loads(output.getvalue())

    def assert_direct_secret(self, result, oid):
        self.assertEqual(result.returncode, 1, result.stderr.decode())
        summary = json.loads(result.stdout)
        self.assertTrue(summary["scan_completed"])
        self.assertFalse(summary["scan_passed"])
        self.assertGreater(summary["direct_blob_findings"], 0)
        findings = json.loads((self.root / "evidence" / "direct-blob-secrets.json").read_text())
        self.assertTrue(any(Path(item["File"]).name == oid for item in findings))
        self.assertNotIn(self.secret().encode(), result.stdout + result.stderr)
        return summary

    def test_clean_full_clone_and_private_evidence(self):
        git(self.repo, "tag", "v0.0.1")
        result = self.audit()
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        summary = json.loads(result.stdout)
        self.assertEqual(summary["refs"], 2)
        self.assertTrue(summary["scan_passed"])
        self.assertFalse(summary["publication_signoff"])
        self.assertEqual((self.root / "evidence").stat().st_mode & 0o777, 0o700)
        self.assertEqual((self.root / "evidence" / "summary.json").stat().st_mode & 0o777, 0o600)
        self.assertEqual(summary["direct_blob_scan_targets"], 1)
        self.assertTrue(summary["direct_blob_scan_completed"])

    def test_current_binary_secret_is_scanned_without_stream_truncation(self):
        # Exercise a second copy chunk and secret scanning beyond a NUL byte.
        content = b"\0binary-prefix\n" + b"\xff" * (1024 * 1024) + b"\n" + self.secret().encode() + b"\n"
        (self.repo / "opaque.dat").write_bytes(content)
        self.commit()
        oid = git(self.repo, "rev-parse", "HEAD:opaque.dat")
        result = self.audit()
        summary = self.assert_direct_secret(result, oid)
        self.assertEqual(summary["history_findings"], 0)
        stored = self.root / "evidence" / "direct-blobs" / oid
        self.assertEqual(stored.read_bytes(), content)
        self.assertEqual(stored.stat().st_mode & 0o777, 0o600)
        self.assertEqual(stored.parent.stat().st_mode & 0o777, 0o700)

    def test_deleted_binary_secret_is_scanned(self):
        (self.repo / "retired.dat").write_bytes(b"\0binary-prefix\n" + self.secret().encode() + b"\n")
        self.commit()
        oid = git(self.repo, "rev-parse", "HEAD:retired.dat")
        (self.repo / "retired.dat").unlink()
        self.commit()
        self.assert_direct_secret(self.audit(), oid)

    def test_binary_secret_historical_aliases_are_scanned_once(self):
        (self.repo / "retired.bin").write_bytes(b"\0binary-prefix\n" + self.secret().encode() + b"\n")
        self.commit()
        oid = git(self.repo, "rev-parse", "HEAD:retired.bin")
        git(self.repo, "mv", "retired.bin", "opaque.dat")
        self.commit()
        self.assert_direct_secret(self.audit(), oid)
        inventory = json.loads((self.root / "evidence" / "direct-blobs.json").read_text())
        matching = [item for item in inventory if item["object"] == oid]
        self.assertEqual(len(matching), 1)
        self.assertEqual(matching[0]["paths"], ["opaque.dat", "retired.bin"])
        self.assertEqual(matching[0]["stored_name"], oid)
        findings = json.loads((self.root / "evidence" / "direct-blob-secrets.json").read_text())
        self.assertEqual(sum(Path(item["File"]).name == oid for item in findings), 1)

    def test_merge_resolution_only_binary_secret_is_scanned_after_removal(self):
        (self.repo / "resolution.dat").write_bytes(b"\0Clean common ancestor.\n")
        self.commit()
        git(self.repo, "checkout", "-b", "clean-binary-side")
        (self.repo / "resolution.dat").write_bytes(b"\0Clean side change.\n")
        self.commit()
        git(self.repo, "checkout", "main")
        (self.repo / "resolution.dat").write_bytes(b"\0Clean main change.\n")
        self.commit()
        merge = subprocess.run(["git", "-C", str(self.repo), "merge", "--no-ff", "clean-binary-side"],
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.assertEqual(merge.returncode, 1)
        (self.repo / "resolution.dat").write_bytes(b"\0binary-prefix\n" + self.secret().encode() + b"\n")
        self.commit()
        self.assertEqual(len(git(self.repo, "rev-list", "--parents", "-n", "1", "HEAD").split()), 3)
        oid = git(self.repo, "rev-parse", "HEAD:resolution.dat")
        (self.repo / "resolution.dat").unlink()
        self.commit()
        self.assert_direct_secret(self.audit(), oid)

    def test_attributes_cannot_hide_a_text_secret_as_binary(self):
        (self.repo / ".gitattributes").write_text("*.dat binary\n")
        (self.repo / "opaque.dat").write_text(self.secret() + "\n")
        self.commit()
        oid = git(self.repo, "rev-parse", "HEAD:opaque.dat")
        # Git reads working-tree attributes here; a bare mirror may emit text.
        # Either presentation must still get an independent direct blob scan.
        self.assertIn("Binary files", git(self.repo, "show", "--format=", "HEAD"))
        self.assert_direct_secret(self.audit(), oid)

    def test_direct_blob_limit_includes_exact_limit(self):
        content = b"\0binary-prefix\n" + self.secret().encode() + b"\n"
        content += b"x" * (128 - len(content))
        (self.repo / "boundary.dat").write_bytes(content)
        self.commit()
        oid = git(self.repo, "rev-parse", "HEAD:boundary.dat")
        with patch.object(audit_runner, "DIRECT_BLOB_LIMIT", 128):
            status, summary = self.audit_in_process()
        self.assertEqual(status, 1)
        self.assertTrue(summary["scan_completed"])
        self.assertGreater(summary["direct_blob_findings"], 0)
        self.assertEqual(summary["direct_blob_scan_skipped"], 0)
        self.assertEqual((self.root / "evidence" / "direct-blobs" / oid).read_bytes(), content)

    def test_oversized_nonarchive_blob_leaves_explicit_coverage_gap(self):
        (self.repo / "oversized.dat").write_bytes(b"\0" + b"x" * 128)
        self.commit()
        oid = git(self.repo, "rev-parse", "HEAD:oversized.dat")
        with patch.object(audit_runner, "DIRECT_BLOB_LIMIT", 128):
            status, summary = self.audit_in_process()
        self.assertEqual(status, 2)
        self.assertEqual(summary["history_findings"], 0)
        self.assertEqual(summary["direct_blob_findings"], 0)
        self.assertFalse(summary["scan_completed"])
        self.assertFalse(summary["scan_passed"])
        self.assertFalse(summary["direct_blob_scan_completed"])
        self.assertEqual(summary["direct_blob_scan_skipped"], 1)
        self.assertFalse((self.root / "evidence" / "direct-blobs" / oid).exists())
        inventory = json.loads((self.root / "evidence" / "direct-blobs.json").read_text())
        skipped = next(item for item in inventory if item["object"] == oid)
        self.assertIsNone(skipped["stored_name"])
        self.assertIn("manual inspection required", skipped["skip_reason"])

    def test_direct_blob_report_contract_fails_closed_and_preserves_archives(self):
        with zipfile.ZipFile(self.repo / "fixture.zip", "w") as package:
            package.writestr("readme.txt", "Clean synthetic archive.\n")
        self.commit()
        original_run = audit_runner.run
        finding = json.dumps([{"RuleID": "synthetic", "File": "object-id", "StartLine": 1}])
        cases = [(0, None, b"", 2), (0, "[", b"", 2), (0, "{}", b"", 2),
                 (0, "[1]", b"", 2), (2, "[]", b"", 2), (0, "[]", b"ERR read failure", 2),
                 (1, "[]", b"", 1), (0, finding, b"", 1)]
        for index, (exit_code, payload, diagnostics, expected) in enumerate(cases):
            with self.subTest(exit_code=exit_code, payload=payload, diagnostics=diagnostics):
                def scanner_process(command, *, cwd=None, log=None, allowed=(0,)):
                    if "--report-path" not in command or Path(command[command.index("--report-path") + 1]).name != "direct-blob-secrets.json":
                        return original_run(command, cwd=cwd, log=log, allowed=allowed)
                    if payload is not None:
                        Path(command[command.index("--report-path") + 1]).write_text(payload)
                    if log:
                        log.write_bytes(diagnostics)
                    return subprocess.CompletedProcess(command, exit_code, b"", diagnostics)

                evidence = f"evidence-{index}"
                with patch.object(audit_runner, "run", side_effect=scanner_process):
                    status, summary = self.audit_in_process(evidence)
                self.assertEqual(status, expected)
                self.assertFalse(summary["scan_passed"])
                self.assertEqual(summary["scan_completed"], expected == 1)
                archives = json.loads((self.root / evidence / "archive-scan-results.json").read_text())
                self.assertTrue(archives[0]["scan_completed"])

    def test_current_and_deleted_historical_icons_reach_asset_inventory(self):
        # Unique harmless binary fixtures cover both icon formats, mixed-case
        # extensions, and an object absent from the current tracked tree.
        fixtures = {
            "retired.ico": b"\x00\x00\x01\x00synthetic historical icon\n",
            "current.ico": b"\x00\x00\x01\x00synthetic current icon\n",
            "current.ICNS": b"icns\x00\x00\x00\x08synthetic macOS icon\n",
        }
        for name, content in fixtures.items():
            (self.repo / name).write_bytes(content)
        self.commit()
        expected = {name: git(self.repo, "rev-parse", f"HEAD:{name}") for name in fixtures}
        (self.repo / "retired.ico").unlink()
        self.commit()
        result = self.audit()
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        summary = json.loads(result.stdout)
        self.assertTrue(summary["direct_blob_scan_completed"])
        self.assertEqual(summary["direct_blob_scan_targets"], 4)
        self.assertEqual(summary["direct_blob_findings"], 0)
        assets = json.loads((self.root / "evidence" / "assets-review.json").read_text())
        self.assertEqual({item["path"]: item["object"] for item in assets}, expected)
        self.assertEqual({item["path"]: item["bytes"] for item in assets},
                         {name: len(content) for name, content in fixtures.items()})

    def test_asset_renamed_without_extension_preserves_historical_alias(self):
        (self.repo / "retired.ico").write_bytes(b"\x00\x00\x01\x00synthetic renamed icon\n")
        self.commit()
        expected = git(self.repo, "rev-parse", "HEAD:retired.ico")
        git(self.repo, "mv", "retired.ico", "opaque.dat")
        self.commit()
        result = self.audit()
        self.assertEqual(result.returncode, 0, result.stderr.decode())
        assets = json.loads((self.root / "evidence" / "assets-review.json").read_text())
        self.assertEqual(len(assets), 1)
        self.assertEqual(assets[0]["object"], expected)
        self.assertEqual(assets[0]["paths"], ["opaque.dat", "retired.ico"])

    def test_history_secret_fails_after_current_file_is_removed(self):
        (self.repo / "token.txt").write_text(self.secret() + "\n")
        self.commit()
        git(self.repo, "tag", "contains-synthetic-secret")
        (self.repo / "token.txt").unlink()
        self.commit()
        result = self.audit()
        self.assertEqual(result.returncode, 1, result.stderr.decode())
        self.assertGreater(json.loads(result.stdout)["history_findings"], 0)
        self.assertNotIn(self.secret().encode(), result.stdout + result.stderr)

    def test_merge_resolution_only_secret_fails_after_removal(self):
        (self.repo / "resolution.txt").write_text("Clean common ancestor.\n")
        self.commit()
        git(self.repo, "checkout", "-b", "clean-side")
        (self.repo / "resolution.txt").write_text("Clean side change.\n")
        self.commit()
        git(self.repo, "checkout", "main")
        (self.repo / "resolution.txt").write_text("Clean main change.\n")
        self.commit()
        merge = subprocess.run(["git", "-C", str(self.repo), "merge", "--no-ff", "clean-side"],
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.assertEqual(merge.returncode, 1)
        (self.repo / "resolution.txt").write_text(self.secret() + "\n")
        self.commit()
        merge_commit = git(self.repo, "rev-parse", "HEAD")
        self.assertEqual(len(git(self.repo, "rev-list", "--parents", "-n", "1", "HEAD").split()), 3)
        (self.repo / "resolution.txt").unlink()
        self.commit()
        result = self.audit()
        self.assertEqual(result.returncode, 1, result.stderr.decode())
        findings = json.loads((self.root / "evidence" / "history-secrets.json").read_text())
        self.assertTrue(any(item["Commit"] == merge_commit for item in findings))
        self.assertNotIn(self.secret().encode(), result.stdout + result.stderr)

    def test_replacement_ref_cannot_hide_original_root_secret(self):
        clean_root = git(self.repo, "rev-parse", "HEAD")
        git(self.repo, "checkout", "--orphan", "original-secret-root")
        git(self.repo, "rm", "-f", "README")
        (self.repo / "token.txt").write_text(self.secret() + "\n")
        self.commit()
        original_root = git(self.repo, "rev-parse", "HEAD")
        original_blob = git(self.repo, "rev-parse", "HEAD:token.txt")
        git(self.repo, "checkout", "main")
        git(self.repo, "replace", original_root, clean_root)
        result = self.audit()
        self.assertEqual(result.returncode, 1, result.stderr.decode())
        findings = json.loads((self.root / "evidence" / "history-secrets.json").read_text())
        self.assertTrue(any(item["Commit"] == original_root for item in findings))
        inventory = json.loads((self.root / "evidence" / "historical-blobs.json").read_text())
        self.assertIn(original_blob, {item["object"] for item in inventory})
        refs = json.loads((self.root / "evidence" / "refs.json").read_text())
        self.assertIn(f"refs/replace/{original_root}", {item["ref"] for item in refs})
        self.assertNotIn(self.secret().encode(), result.stdout + result.stderr)

    def test_non_main_branch_and_inline_allow_cannot_hide_secret(self):
        git(self.repo, "checkout", "-b", "old-publication-branch")
        (self.repo / "token.txt").write_text(self.secret() + " # gitleaks:allow\n")
        # A repository-owned ignore file also must not suppress a finding.
        (self.repo / ".gitleaksignore").write_text("*\n")
        self.commit()
        git(self.repo, "checkout", "main")
        result = self.audit()
        self.assertEqual(result.returncode, 1, result.stderr.decode())
        self.assertGreater(json.loads(result.stdout)["history_findings"], 0)

    def test_compressed_secret_fails_and_member_inventory_survives(self):
        with zipfile.ZipFile(self.repo / "report.zip", "w", zipfile.ZIP_DEFLATED) as package:
            package.writestr("nested/token.txt", self.secret() + "\n")
        self.commit()
        result = self.audit()
        self.assertEqual(result.returncode, 1, result.stderr.decode())
        summary = json.loads(result.stdout)
        self.assertGreater(summary["archive_findings"], 0)
        inventory = json.loads((self.root / "evidence" / "archives-review.json").read_text())
        self.assertEqual(inventory[0]["inspection"]["members"][0]["name"], "nested/token.txt")

    def test_archive_signature_cannot_be_hidden_by_renaming(self):
        with zipfile.ZipFile(self.repo / "report.dat", "w", zipfile.ZIP_DEFLATED) as package:
            package.writestr("token.txt", self.secret() + "\n")
        self.commit()
        result = self.audit()
        self.assertEqual(result.returncode, 1, result.stderr.decode())
        self.assertGreater(json.loads(result.stdout)["archive_findings"], 0)

    def test_malformed_archive_does_not_hide_other_archive_results(self):
        # A recognizable truncated 7z signature exercises a real decoder failure.
        (self.repo / "malformed.7z").write_bytes(b"7z\xbc\xaf\x27\x1c" + bytes(32))
        with zipfile.ZipFile(self.repo / "report.zip", "w", zipfile.ZIP_DEFLATED) as package:
            package.writestr("token.txt", self.secret() + "\n")
        self.commit()
        result = self.audit()
        self.assertEqual(result.returncode, 2, result.stderr.decode())
        summary = json.loads(result.stdout)
        self.assertFalse(summary["scan_completed"])
        self.assertGreater(summary["archive_scanner_errors"], 0)
        self.assertGreater(summary["archive_findings"], 0)

    def test_reused_evidence_is_rejected_without_mutation(self):
        self.assertEqual(self.audit().returncode, 0)
        before = {str(path): hashlib.sha256(path.read_bytes()).hexdigest()
                  for path in (self.root / "evidence").rglob("*") if path.is_file()}
        self.assertEqual(self.audit().returncode, 2)
        after = {str(path): hashlib.sha256(path.read_bytes()).hexdigest()
                 for path in (self.root / "evidence").rglob("*") if path.is_file()}
        self.assertEqual(before, after)

    def test_evidence_inside_any_git_worktree_is_rejected(self):
        result = subprocess.run([sys.executable, str(SCRIPT), "--source", str(self.repo),
                                 "--evidence", str(self.repo / "findings"), "--gitleaks", str(SCANNER)],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.assertEqual(result.returncode, 2)
        self.assertFalse((self.repo / "findings").exists())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gitleaks", type=Path, required=True)
    args, remaining = parser.parse_known_args()
    SCANNER = args.gitleaks.resolve()
    unittest.main(argv=[sys.argv[0], *remaining])
