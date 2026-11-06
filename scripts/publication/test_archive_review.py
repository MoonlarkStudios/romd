#!/usr/bin/env python3
"""Synthetic private evidence contracts, using the committed generated corpus as golden bytes."""

import argparse
from contextlib import redirect_stdout
import io
import json
import os
from pathlib import Path, PurePosixPath
import random
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import archive_review as review
import audit
import install_scanner
from test_audit import git


REPLAY_NAMES = ["absolute.7z", "anti.7z", "backslash-directory.7z", "backslash.7z", "case-collision.7z",
                "corrupt.7z", "declared-size.7z", "drive.7z", "duplicate.7z", "implicit-parents.7z",
                "normalized-collision.7z", "nul.7z", "overlong.7z", "overflow-declared.7z",
                "oversized-dictionary.7z", "oversized-header-dictionary.7z", "oversized-lzma.7z",
                "parent-case.7z", "parent-collision.7z"]


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def scanner_archive(binary, *, name="gitleaks", kind=tarfile.REGTYPE, duplicate=False):
    result = io.BytesIO()
    with tarfile.open(fileobj=result, mode="w:gz") as package:
        member = tarfile.TarInfo(name)
        member.type = kind
        member.size = len(binary) if kind == tarfile.REGTYPE else 0
        member.linkname = "outside"
        package.addfile(member, io.BytesIO(binary) if kind == tarfile.REGTYPE else None)
        if duplicate:
            package.addfile(member, io.BytesIO(binary))
    return result.getvalue()


class ArchiveReviewContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # These are existing synthetic test fixtures, not any private review artifacts.
        source = review.Source(audit.ROOT, "HEAD")
        generator_path = next(iter(source.objects[review.GENERATOR_OBJECT]))
        directory = PurePosixPath(generator_path).parent
        cls.generator = source.blob(review.GENERATOR_OBJECT)
        cls.config = source.blob(source.at(".gitleaks.toml"))
        names = REPLAY_NAMES + ["encrypted-headers.7z", "solid.7z", "large.7z"]
        cls.corpus = {name: source.blob(source.at(str(directory / name))) for name in names}

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="romd-archive-review-contract-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.repo = self.root / "source"
        self.repo.mkdir()
        git(self.repo, "init", "-b", "main")
        git(self.repo, "config", "user.name", "Synthetic archive review")
        git(self.repo, "config", "user.email", "archive-test@example.invalid")
        (self.repo / "fixtures").mkdir()
        (self.repo / "fixtures/generate.py").write_bytes(self.generator)
        (self.repo / ".gitleaks.toml").write_bytes(self.config)
        for name, content in self.corpus.items():
            (self.repo / "fixtures" / name).write_bytes(content)
        self.commit()
        self.audit = self.root / "audit"
        self.dispositions = self.root / "dispositions"
        self.scanner = self.root / "scanner/gitleaks"
        self.scanner.parent.mkdir()
        self.scanner.write_bytes(b"Synthetic scanner process, mocked and never executed.\n")
        self.version = json.loads(Path(audit.__file__).with_name("scanner.json").read_text())["version"]
        package = scanner_archive(self.scanner.read_bytes())
        self.package = self.scanner.with_name(install_scanner.ARCHIVE_NAME)
        self.package.write_bytes(package)
        self.pin = {"version": self.version, "archives": {"synthetic": review.digest(package)}}
        write_json(self.scanner.with_name("receipt.json"), {
            "version": self.version, "archive_file": install_scanner.ARCHIVE_NAME, "archive_sha256": review.digest(package),
            "binary_sha256": review.digest(self.scanner.read_bytes())})
        self.bundle()
        self.run_number = 0
        old_umask = os.umask(0o077)
        self.addCleanup(os.umask, old_umask)

    def commit(self):
        git(self.repo, "add", ".")
        git(self.repo, "commit", "-m", "synthetic archive review fixture")

    def bundle(self):
        for directory in (self.audit, self.dispositions):
            if directory.exists():
                shutil.rmtree(directory)
            directory.mkdir(mode=0o700)
        audit.run(["git", "clone", "--mirror", "--no-local", str(self.repo), str(self.audit / "repository.git")])
        source = review.Source(self.repo, "HEAD")
        # Rebuilt synthetic evidence remains one root even in the forgery test.
        if int(audit.git(self.audit / "repository.git", "rev-list", "--all", "--count")) != 1:
            tree = git(self.repo, "rev-parse", "HEAD^{tree}")
            commit = audit.run(["git", "--git-dir", str(self.audit / "repository.git"), "-c", "user.name=Synthetic review",
                                "-c", "user.email=archive-test@example.invalid", "commit-tree", tree, "-m", "synthetic root"]).stdout.decode().strip()
            audit.run(["git", "--git-dir", str(self.audit / "repository.git"), "update-ref", "refs/heads/main", commit])
        original = review.Source(self.audit / "repository.git", "HEAD")
        write_json(self.audit / "refs.json", [{"ref": "refs/heads/main", "object": original.commit, "commit": original.commit}])
        historical = [{"object": oid, "path": sorted(paths)[0], "paths": sorted(paths), "bytes": len(source.blob(oid))}
                      for oid, paths in source.objects.items()]
        write_json(self.audit / "historical-blobs.json", historical)
        raw_dir = self.audit / "archive-blobs"
        raw_dir.mkdir()
        scans = self.audit / "archive-scans"
        scans.mkdir()
        archives, results, provenance = [], [], []
        for index, name in enumerate(self.corpus):
            path = "fixtures/" + name
            oid = source.at(path)
            content = source.blob(oid)
            failed = name in REPLAY_NAMES or name == "encrypted-headers.7z"
            stored = oid + ".7z"
            archives.append({"object": oid, "path": path, "paths": [path], "bytes": len(content), "stored_name": stored,
                             "inspection": {"status": "manual-format-review"}})
            (raw_dir / stored).write_bytes(content)
            results.append({"object": oid, "scan_completed": not failed, "exit_code": 2 if name in REPLAY_NAMES else 0,
                            "report_status": "missing" if name in REPLAY_NAMES else "parsed",
                            "findings": None if name in REPLAY_NAMES else 0})
            (scans / f"{index:04}.log").write_bytes(b"panic: synthetic decoder failure\n" if name in REPLAY_NAMES
                                                    else b"ERR synthetic unsupported decoder\n" if failed
                                                    else b"INF synthetic clean\n")
            if name not in REPLAY_NAMES:
                write_json(scans / f"{index:04}.json", [])
            if failed:
                provenance.append({"object": oid, "source_path": path, "bytes": len(content), "sha256": review.digest(content),
                                   "replayed": name in REPLAY_NAMES, "replay_matches": name in REPLAY_NAMES,
                                   "replay": {"object": oid, "bytes": len(content), "sha256": review.digest(content)}
                                   if name in REPLAY_NAMES else None})
        write_json(self.audit / "archives-review.json", archives)
        write_json(self.audit / "archive-scan-results.json", results)
        write_json(self.audit / "run.json", {"scanner_version": self.version,
                                             "scanner_sha256": review.digest(self.scanner.read_bytes()),
                                             "scanner_config_sha256": review.digest(self.config)})
        payloads = {"solid.7z": {"a.rom": b"alpha", "b.rom": b"beta", "tree/nested.rom": b"nested", "empty.rom": b""},
                    "large.7z": {"large.rom": random.Random(1234).randbytes(160000)}}
        compressed = []
        members_dir = self.dispositions / "members"
        members_dir.mkdir()
        for name, members in payloads.items():
            content = source.blob(source.at("fixtures/" + name))
            compressed.append({"source_object": source.at("fixtures/" + name), "source_path": "fixtures/" + name,
                               "source_sha256": review.digest(content), "directory_count": 2 if name == "solid.7z" else 0,
                               "members": [{"path": path, "bytes": len(payload), "sha256": review.digest(payload), "matches_generator": True}
                                           for path, payload in members.items()],
                               "entire_physical_blob_accounted_for": True,
                               "all_payloads_match_generator_literals_or_fixed_prng": True,
                               "decoder_list_test_extract_passed": True})
            for payload in members.values():
                (members_dir / (review.digest(payload) + ".bin")).write_bytes(payload)
        write_json(self.dispositions / "compressed-seed-disposition.json", compressed)
        reads = [{"object": source.at("fixtures/" + name), "path": name,
                  "sha256": review.digest(source.blob(source.at("fixtures/" + name)))}
                 for name in ("solid.7z", "large.7z", "solid.7z")]
        write_json(self.dispositions / "archive-provenance.json", {
            "generator_object": review.GENERATOR_OBJECT, "failed_archive_records": provenance, "seed_reads": reads})
        decoder = self.dispositions / "decoder"
        decoder.mkdir()
        binary = decoder / "7zz"
        binary.write_bytes(b"Synthetic retained decoder; never executed.")
        write_json(decoder / "receipt.json", {"version": "26.03", "binary_sha256": review.digest(binary.read_bytes()),
                                             "expected_archive_sha256": "1" * 64, "observed_archive_sha256": "1" * 64})
        oid = source.at("fixtures/encrypted-headers.7z")
        write_json(self.dispositions / "encrypted-fixture-disposition.json", {
            "source_object": oid, "source_path": "fixtures/encrypted-headers.7z", "source_sha256": review.digest(source.blob(oid)),
            "declared_file_count": 1, "declared_uncompressed_bytes": 5, "decoded_payload_sha256": review.digest(b"alpha"),
            "decoded_payload_matches_reviewed_generator_literal": True, "physical_size_matches_entire_blob": True,
            "list_test_extract_exit_codes": [0, 0, 0], "decoder_receipt": "decoder/receipt.json"})

    def mutate_json(self, root, name, mutation):
        path = root / name
        value = json.loads(path.read_text())
        mutation(value)
        write_json(path, value)

    def run_review(self, *, exit_code=0, payload="[]", diagnostics=b"", evidence=None):
        self.run_number += 1
        args = argparse.Namespace(source=self.repo, revision="HEAD", audit_evidence=self.audit,
                                  dispositions=self.dispositions, gitleaks=self.scanner,
                                  evidence=evidence or self.root / f"review-{self.run_number}")
        original_run = audit.run

        def scanner_process(command, *, cwd=None, log=None, allowed=(0,)):
            if command[0] != str(self.scanner):
                return original_run(command, cwd=cwd, log=log, allowed=allowed)
            if command[1] == "version":
                return subprocess.CompletedProcess(command, 0, (self.version + "\n").encode(), b"")
            if payload is not None:
                Path(command[command.index("--report-path") + 1]).write_text(payload)
            if log:
                log.write_bytes(diagnostics)
            self.assertEqual(command[command.index("--max-archive-depth") + 1], "0")
            self.assertIn("--ignore-gitleaks-allow", command)
            return subprocess.CompletedProcess(command, exit_code, diagnostics, b"")

        output = io.StringIO()
        with patch.object(audit, "run", side_effect=scanner_process), \
                patch.object(install_scanner, "scanner_pin", return_value=self.pin), redirect_stdout(output):
            status = review.review(args)
        return status, json.loads((args.evidence / "summary.json").read_text()), args.evidence, output.getvalue()

    def test_valid_bindings_scan_bytes_without_approving_attestations_or_changing_original_reports(self):
        original = {path: review.digest(path.read_bytes()) for path in self.audit.rglob("*.json")}

        status, summary, evidence, _ = self.run_review()

        self.assertEqual(status, 0)
        self.assertTrue(summary["independent_technical_checks_passed"])
        self.assertEqual(summary["deterministic_reconstructions_verified"], 19)
        self.assertEqual(summary["independent_raw_blob_targets"], 22)
        self.assertEqual(summary["unique_retained_decoded_payloads_verified"], 5)
        self.assertTrue(summary["reviewer_attestation_required"])
        for key in ("technical_manual_coverage_complete", "scan_completed", "scan_passed", "publication_signoff"):
            self.assertFalse(summary[key])
        self.assertEqual(original, {path: review.digest(path.read_bytes()) for path in self.audit.rglob("*.json")})
        for path in evidence.rglob("*"):
            self.assertEqual(path.stat().st_mode & 0o777, 0o700 if path.is_dir() else 0o600)

    def test_self_consistent_forged_hashes_cannot_replace_actual_deterministic_reconstruction(self):
        path = self.repo / "fixtures/absolute.7z"
        content = bytearray(path.read_bytes())
        content[-1] ^= 1
        path.write_bytes(content)
        self.commit()
        self.bundle()  # All source, raw, provenance, and replay hashes now agree with the forgery.

        with self.assertRaisesRegex(ValueError, "deterministic reconstruction"):
            self.run_review()

    def test_changed_generator_is_not_executed_or_accepted(self):
        (self.repo / "fixtures/generate.py").write_text("raise RuntimeError('must never execute repository code')\n")
        self.commit()
        with self.assertRaisesRegex(ValueError, "Generator identity"):
            self.run_review()

    def test_new_opaque_archive_is_rejected_by_signature(self):
        (self.repo / "opaque.dat").write_bytes(self.corpus["solid.7z"] + b"unaccounted")
        self.commit()
        with self.assertRaisesRegex(ValueError, "New, changed, missing, or renamed"):
            self.run_review()

    def test_renamed_archive_alias_is_rejected_even_when_bytes_are_identical(self):
        (self.repo / "fixtures/solid.7z").rename(self.repo / "renamed.dat")
        self.commit()
        with self.assertRaisesRegex(ValueError, "New, changed, missing, or renamed"):
            self.run_review()

    def test_missing_extra_or_duplicate_provenance_records_are_rejected(self):
        for mutation in (lambda rows: rows.pop(), lambda rows: rows.append(dict(rows[0])),
                         lambda rows: rows.append({**rows[0], "object": "f" * 40})):
            with self.subTest(mutation=mutation):
                original = (self.dispositions / "archive-provenance.json").read_bytes()
                self.mutate_json(self.dispositions, "archive-provenance.json", lambda value: mutation(value["failed_archive_records"]))
                with self.assertRaises(ValueError):
                    self.run_review()
                (self.dispositions / "archive-provenance.json").write_bytes(original)

    def test_missing_extra_unsafe_or_wrong_payload_records_are_rejected(self):
        for mutation in (lambda members: members.pop(), lambda members: members.append(dict(members[0])),
                         lambda members: members[0].update(path="../escape"),
                         lambda members: members[0].update(sha256="f" * 64),
                         lambda members: members[0].update(bytes=True)):
            with self.subTest(mutation=mutation):
                original = (self.dispositions / "compressed-seed-disposition.json").read_bytes()
                self.mutate_json(self.dispositions, "compressed-seed-disposition.json", lambda value: mutation(value[0]["members"]))
                with self.assertRaises(ValueError):
                    self.run_review()
                (self.dispositions / "compressed-seed-disposition.json").write_bytes(original)

    def test_missing_retained_decoded_payload_is_rejected(self):
        (self.dispositions / "members" / (review.digest(b"nested") + ".bin")).unlink()
        with self.assertRaisesRegex(ValueError, "Missing retained decoded"):
            self.run_review()

    def test_symlinked_payload_and_escaping_decoder_receipt_are_rejected(self):
        payload = self.dispositions / "members" / (review.digest(b"nested") + ".bin")
        payload.unlink()
        outside = self.root / "outside.bin"
        outside.write_bytes(b"nested")
        payload.symlink_to(outside)
        with self.assertRaisesRegex(ValueError, "Unsafe private artifact"):
            self.run_review()
        payload.unlink()
        payload.write_bytes(b"nested")
        self.mutate_json(self.dispositions, "encrypted-fixture-disposition.json",
                         lambda value: value.update(decoder_receipt="../outside.json"))
        with self.assertRaisesRegex(ValueError, "Unsafe relative"):
            self.run_review()

    def test_raw_archive_tampering_is_rejected(self):
        source = review.Source(self.repo, "HEAD")
        object_id = source.at("fixtures/" + REPLAY_NAMES[0])
        path = self.audit / "archive-blobs" / (object_id + ".7z")
        path.write_bytes(path.read_bytes() + b"tampered")
        with self.assertRaisesRegex(ValueError, "Retained raw archive differs"):
            self.run_review()

    def test_unbound_historical_objects_and_original_findings_are_rejected(self):
        path = self.audit / "historical-blobs.json"
        original = path.read_bytes()
        self.mutate_json(self.audit, path.name, lambda value: value.append({"object": "f" * 40, "paths": ["opaque.dat"]}))
        with self.assertRaisesRegex(ValueError, "Historical blob inventory"):
            self.run_review()
        path.write_bytes(original)
        self.mutate_json(self.audit, "archive-scan-results.json", lambda value: value[0].update(findings=1, report_status="parsed"))
        write_json(self.audit / "archive-scans/0000.json", [{"RuleID": "synthetic"}])
        with self.assertRaisesRegex(ValueError, "Original archive findings"):
            self.run_review()

    def test_scanner_missing_malformed_failed_or_finding_reports_never_pass(self):
        marker = "harmless-" + "private-report-" + "marker"
        finding = json.dumps([{"RuleID": "synthetic", "Secret": marker}])
        for code, payload, diagnostics, expected in [(0, None, b"", 2), (0, "{", b"", 2), (0, "{}", b"", 2),
                                                      (0, "[1]", b"", 2), (2, "[]", b"", 2),
                                                      (0, "[]", b"ERR synthetic failure", 2), (1, "[]", b"", 1),
                                                      (0, finding, b"", 1), (1, finding, b"", 1)]:
            with self.subTest(code=code, payload=payload):
                status, summary, _, output = self.run_review(exit_code=code, payload=payload, diagnostics=diagnostics)
                self.assertEqual(status, expected)
                self.assertFalse(summary["independent_technical_checks_passed"])
                self.assertFalse(summary["technical_manual_coverage_complete"])
                self.assertFalse(summary["scan_passed"])
                self.assertNotIn("harmless-private-report-marker", output)

    def test_scanner_binary_and_config_drift_are_rejected(self):
        original = self.scanner.read_bytes()
        self.scanner.write_bytes(b"changed scanner")
        with self.assertRaisesRegex(ValueError, "Scanner executable differs"):
            self.run_review()
        self.scanner.write_bytes(original)
        (self.repo / ".gitleaks.toml").write_text("[extend]\nuseDefault = false\n")
        self.commit()
        with self.assertRaisesRegex(ValueError, "Scanner rules differ"):
            self.run_review()

    def test_matching_editable_hash_receipts_cannot_authenticate_a_fake_executable(self):
        self.scanner.write_bytes(b"Fake scanner claiming the same version and empty reports")
        forged = review.digest(self.scanner.read_bytes())
        self.mutate_json(self.scanner.parent, "receipt.json", lambda value: value.update(binary_sha256=forged))
        self.mutate_json(self.audit, "run.json", lambda value: value.update(scanner_sha256=forged))
        with self.assertRaisesRegex(ValueError, "Scanner executable differs"):
            self.run_review()

    def test_actual_archive_bytes_must_match_the_trusted_pin(self):
        self.package.write_bytes(scanner_archive(b"Fake replacement"))
        self.mutate_json(self.scanner.parent, "receipt.json", lambda value: value.update(archive_sha256=review.digest(self.package.read_bytes())))
        with self.assertRaisesRegex(ValueError, "Actual scanner archive differs"):
            self.run_review()

    def test_wrong_link_or_duplicate_scanner_member_is_rejected_without_extraction(self):
        for options in ({"name": "elsewhere/gitleaks"}, {"kind": tarfile.SYMTYPE}, {"kind": tarfile.LNKTYPE}, {"duplicate": True}):
            with self.subTest(options=options):
                package = scanner_archive(self.scanner.read_bytes(), **options)
                self.package.write_bytes(package)
                self.pin["archives"]["synthetic"] = review.digest(package)
                self.mutate_json(self.scanner.parent, "receipt.json", lambda value: value.update(archive_sha256=review.digest(package)))
                with self.assertRaises(ValueError):
                    self.run_review()
                self.assertFalse((self.root / "outside").exists())

    def test_original_statuses_are_recomputed_from_raw_reports_and_logs(self):
        results_path = self.audit / "archive-scan-results.json"
        original = results_path.read_bytes()
        complete_index = len(REPLAY_NAMES) + 1
        self.mutate_json(self.audit, results_path.name, lambda value: value[complete_index].update(
            exit_code=2, report_status="missing", findings=None))
        with self.assertRaisesRegex(ValueError, "contradicts retained"):
            self.run_review()
        results_path.write_bytes(original)
        write_json(self.audit / f"archive-scans/{complete_index:04}.json", [{"RuleID": "synthetic"}])
        with self.assertRaisesRegex(ValueError, "contradicts retained"):
            self.run_review()

    def test_extra_snapshot_ref_and_unindexed_report_artifacts_are_rejected(self):
        audit.run(["git", "--git-dir", str(self.audit / "repository.git"), "tag", "unexpected"])
        with self.assertRaisesRegex(ValueError, "extra refs"):
            self.run_review()
        audit.run(["git", "--git-dir", str(self.audit / "repository.git"), "tag", "--delete", "unexpected"])
        for name in ("9999.json", "9999.log"):
            with self.subTest(name=name):
                path = self.audit / "archive-scans" / name
                path.write_text('[{"RuleID":"synthetic-unindexed-finding"}]')
                with self.assertRaisesRegex(ValueError, "extra retained archive report"):
                    self.run_review()
                path.unlink()

    def test_non_gap_original_nonzero_exit_is_rejected_even_with_an_empty_report(self):
        complete_index = len(REPLAY_NAMES) + 1
        self.mutate_json(self.audit, "archive-scan-results.json", lambda value: value[complete_index].update(exit_code=1))
        with self.assertRaisesRegex(ValueError, "Non-gap original archive result is not clean"):
            self.run_review()

    def test_preexisting_magic_only_archive_cannot_be_hidden_by_a_forged_archive_ledger(self):
        opaque = self.repo / "opaque.dat"
        opaque.write_bytes(self.corpus["solid.7z"] + b"hidden")
        self.commit()
        # Rebuild the historical snapshot/ledger to include this blob as a nonarchive.
        self.bundle()
        with self.assertRaisesRegex(ValueError, "New, changed, missing, or renamed"):
            self.run_review()

    def test_bad_object_receipt_shapes_and_nested_records_are_rejected(self):
        for root, name in [(self.dispositions, "archive-provenance.json"),
                           (self.dispositions, "encrypted-fixture-disposition.json"),
                           (self.dispositions / "decoder", "receipt.json"),
                           (self.audit, "run.json"), (self.scanner.parent, "receipt.json")]:
            original = (root / name).read_bytes()
            for value in ([], None, 1):
                with self.subTest(name=name, value=value):
                    write_json(root / name, value)
                    with self.assertRaisesRegex(ValueError, "JSON object"):
                        self.run_review()
            (root / name).write_bytes(original)
        for root, name, mutation in [
                (self.audit, "refs.json", lambda value: value.__setitem__(0, None)),
                (self.dispositions, "compressed-seed-disposition.json", lambda value: value[0]["members"].__setitem__(0, None)),
                (self.dispositions, "archive-provenance.json", lambda value: value["seed_reads"].__setitem__(0, None))]:
            original = (root / name).read_bytes()
            self.mutate_json(root, name, mutation)
            with self.assertRaises(ValueError):
                self.run_review()
            (root / name).write_bytes(original)

    def test_nonregular_source_mode_is_rejected(self):
        path = "fixtures/absolute.7z"
        oid = git(self.repo, "rev-parse", "HEAD:" + path)
        git(self.repo, "update-index", "--cacheinfo", "120000," + oid + "," + path)
        git(self.repo, "commit", "-m", "synthetic mode change")
        with self.assertRaisesRegex(ValueError, "Archive path mode"):
            self.run_review()

    def test_invalid_receipt_shape_exits_sanitized_incomplete_without_traceback(self):
        write_json(self.dispositions / "archive-provenance.json", [])
        evidence = self.root / "cli-evidence"
        args = ["archive_review.py", "--source", str(self.repo), "--revision", "HEAD",
                "--audit-evidence", str(self.audit), "--dispositions", str(self.dispositions),
                "--gitleaks", str(self.scanner), "--evidence", str(evidence)]
        output = io.StringIO()
        with patch.object(sys, "argv", args), redirect_stdout(output):
            self.assertEqual(review.main(), 2)
        self.assertNotIn("Traceback", output.getvalue())
        self.assertNotIn(str(self.root), output.getvalue())
        self.assertTrue((evidence / "failure.txt").exists())

    def test_old_installation_requires_reinstall_instead_of_receipt_fabrication(self):
        self.package.unlink()
        self.mutate_json(self.scanner.parent, "receipt.json", lambda value: value.pop("archive_file"))
        with self.assertRaisesRegex(ValueError, "reinstall with install_scanner.py"):
            self.run_review()

    def test_installer_retains_verified_archive_and_binary_with_private_modes(self):
        package = self.package.read_bytes()
        pin = {"version": self.version, "archives": {"darwin_arm64": review.digest(package)}}
        target = self.root / "installed"
        with patch.object(install_scanner, "scanner_pin", return_value=pin), \
                patch.object(install_scanner.platform, "system", return_value="Darwin"), \
                patch.object(install_scanner.platform, "machine", return_value="arm64"), \
                patch.object(install_scanner.urllib.request, "urlopen", return_value=io.BytesIO(package)):
            binary = install_scanner.install(target)
        self.assertEqual(binary.read_bytes(), self.scanner.read_bytes())
        self.assertEqual((target / install_scanner.ARCHIVE_NAME).read_bytes(), package)
        self.assertEqual((target / install_scanner.ARCHIVE_NAME).stat().st_mode & 0o777, 0o600)
        self.assertEqual(binary.stat().st_mode & 0o777, 0o700)

    def test_installer_rejects_wrong_checksum_before_retaining_or_installing_bytes(self):
        target = self.root / "rejected-install"
        with patch.object(install_scanner, "scanner_pin", return_value={"version": self.version, "archives": {"darwin_arm64": "0" * 64}}), \
                patch.object(install_scanner.platform, "system", return_value="Darwin"), \
                patch.object(install_scanner.platform, "machine", return_value="arm64"), \
                patch.object(install_scanner.urllib.request, "urlopen", return_value=io.BytesIO(self.package.read_bytes())):
            with self.assertRaisesRegex(ValueError, "checksum differs"):
                install_scanner.install(target)
        self.assertFalse((target / install_scanner.ARCHIVE_NAME).exists())
        self.assertFalse((target / "gitleaks").exists())

    def test_output_inside_git_is_rejected_before_creation(self):
        output = self.repo / "private-output"
        with self.assertRaisesRegex(ValueError, "outside every Git"):
            self.run_review(evidence=output)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
