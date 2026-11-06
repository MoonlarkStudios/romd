#!/usr/bin/env python3
"""Fail-closed audit integration checks with mocked scanner processes."""

import argparse
from contextlib import redirect_stdout
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import audit
from test_audit import git


def http_response(body=b"synthetic catalog", *, declared_length=None, chunked=False, status=200):
    """Exercise real stdlib framing/EOF behavior without a network listener."""
    headers = (b"Transfer-Encoding: chunked" if chunked else
               f"Content-Length: {len(body) if declared_length is None else declared_length}".encode())
    wire = f"HTTP/1.1 {status} Test\r\n".encode() + headers + b"\r\n\r\n" + body

    class Socket:
        def makefile(self, *args):
            return io.BytesIO(wire)

    response = http.client.HTTPResponse(Socket())
    response.begin()
    return response


class AccessProbeTests(unittest.TestCase):
    url = "https://companion.example.invalid/catalog/"

    def test_transport_and_protocol_failures_record_only_their_type(self):
        for failure in (http.client.RemoteDisconnected("PROXY_DETAIL_MUST_NOT_LEAK"),
                        http.client.HTTPException("PROXY_DETAIL_MUST_NOT_LEAK"),
                        ConnectionResetError("PROXY_DETAIL_MUST_NOT_LEAK"),
                        TimeoutError("PROXY_DETAIL_MUST_NOT_LEAK"),
                        audit.urllib.error.URLError("PROXY_DETAIL_MUST_NOT_LEAK")):
            with self.subTest(failure=type(failure).__name__):
                with patch.object(audit.urllib.request, "urlopen", side_effect=failure) as opened:
                    result = audit.access_probe(self.url)
                self.assertEqual(result["status"], "failed")
                self.assertEqual(result["error_type"], type(failure).__name__)
                self.assertNotIn("PROXY_DETAIL_MUST_NOT_LEAK", json.dumps(result))
                opened.assert_called_once_with(self.url, timeout=60)

    def test_read_failure_closes_response_and_never_reports_partial_hash_as_success(self):
        for failure in (http.client.IncompleteRead(b"PARTIAL_BODY_MUST_NOT_LEAK", 100),
                        ConnectionResetError("PROXY_DETAIL_MUST_NOT_LEAK"),
                        http.client.HTTPException("PROXY_DETAIL_MUST_NOT_LEAK")):
            with self.subTest(failure=type(failure).__name__):
                class Response:
                    status = 200
                    closed = False
                    reads = 0

                    def __enter__(self):
                        return self

                    def __exit__(self, *args):
                        self.closed = True

                    def read(self, size):
                        self.reads += 1
                        if self.reads == 1:
                            return b"PARTIAL_BODY_MUST_NOT_LEAK"
                        raise failure

                response = Response()
                with patch.object(audit.urllib.request, "urlopen", return_value=response):
                    result = audit.access_probe(self.url, "0" * 64)
                self.assertEqual(result["status"], "failed")
                self.assertEqual(result["error_type"], type(failure).__name__)
                self.assertTrue(response.closed)
                self.assertNotIn("sha256", result)
                self.assertNotIn("checksum_matches", result)
                self.assertNotIn("PROXY_DETAIL_MUST_NOT_LEAK", json.dumps(result))
                self.assertNotIn("PARTIAL_BODY_MUST_NOT_LEAK", json.dumps(result))

    def test_declared_length_truncated_eof_cannot_qualify_as_success(self):
        response = http_response(b"partial", declared_length=100)
        self.addCleanup(response.close)
        with patch.object(audit.urllib.request, "urlopen", return_value=response):
            result = audit.access_probe(self.url)
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["error_type"], "IncompleteRead")
        self.assertNotIn("sha256", result)
        self.assertTrue(response.closed)

    def test_incomplete_chunked_body_records_protocol_failure(self):
        response = http_response(b"4\r\nabc", chunked=True)
        self.addCleanup(response.close)
        with patch.object(audit.urllib.request, "urlopen", return_value=response):
            result = audit.access_probe(self.url)
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["error_type"], "IncompleteRead")
        self.assertNotIn("sha256", result)
        self.assertTrue(response.closed)

    def test_complete_stream_preserves_verified_hash_and_size(self):
        body = b"x" * (1024 * 1024 + 17)
        expected = hashlib.sha256(body).hexdigest()
        response = http_response(body)
        self.addCleanup(response.close)
        with patch.object(audit.urllib.request, "urlopen", return_value=response):
            result = audit.access_probe(self.url, expected)
        self.assertEqual(result["status"], 200)
        self.assertEqual(result["bytes"], len(body))
        self.assertEqual(result["sha256"], expected)
        self.assertTrue(result["checksum_matches"])
        self.assertTrue(response.closed)

    def test_response_close_failure_does_not_leave_a_successful_checksum(self):
        response = http_response()
        self.addCleanup(response.close)
        with patch.object(response, "close", side_effect=ConnectionResetError("PROXY_DETAIL_MUST_NOT_LEAK")):
            with patch.object(audit.urllib.request, "urlopen", return_value=response):
                result = audit.access_probe(self.url, hashlib.sha256(b"synthetic catalog").hexdigest())
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["error_type"], "ConnectionResetError")
        self.assertNotIn("sha256", result)
        self.assertNotIn("checksum_matches", result)
        self.assertNotIn("PROXY_DETAIL_MUST_NOT_LEAK", json.dumps(result))

    def test_malformed_url_is_a_sanitized_probe_failure(self):
        with patch.object(audit.urllib.request, "urlopen") as opened:
            result = audit.access_probe("https://[")
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["error_type"], "ValueError")
        opened.assert_not_called()


class ScannerReportContractTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="romd-publication-contract-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repo = self.root / "source"
        self.repo.mkdir()
        git(self.repo, "init", "-b", "main")
        git(self.repo, "config", "user.name", "Publication contract test")
        git(self.repo, "config", "user.email", "publication-test@example.invalid")
        (self.repo / "README").write_text("Clean synthetic report contract fixture.\n")
        with zipfile.ZipFile(self.repo / "fixture.zip", "w") as package:
            package.writestr("readme.txt", "Clean synthetic archive.\n")
        git(self.repo, "add", ".")
        git(self.repo, "commit", "-m", "test fixture")
        self.scanner = (self.root / "mock-scanner").resolve()
        self.scanner.write_text("Mock scanner processes only; never executed.\n")
        self.version = json.loads(Path(audit.__file__).with_name("scanner.json").read_text())["version"]
        self.run_number = 0
        old_umask = os.umask(0o077)
        self.addCleanup(os.umask, old_umask)

    def run_audit(self, *, history=(0, "[]"), archive=(0, "[]"), probe_access=False):
        self.run_number += 1
        evidence = self.root / f"evidence-{self.run_number}"
        original_run = audit.run

        def scanner_process(command, *, cwd=None, log=None, allowed=(0,)):
            if command[0] != str(self.scanner):
                return original_run(command, cwd=cwd, log=log, allowed=allowed)
            if command[1] == "version":
                return subprocess.CompletedProcess(command, 0, (self.version + "\n").encode(), b"")
            exit_code, payload = history if command[1] == "git" else archive
            if payload is not None:
                Path(command[command.index("--report-path") + 1]).write_text(payload)
            if log:
                log.write_bytes(b"Synthetic scanner process.\n")
            return subprocess.CompletedProcess(command, exit_code, b"", b"")

        args = argparse.Namespace(source=str(self.repo), evidence=evidence,
                                  gitleaks=self.scanner, probe_access=probe_access)
        output = io.StringIO()
        with patch.object(audit, "run", side_effect=scanner_process), redirect_stdout(output):
            status = audit.audit(args)
        self.output = output.getvalue()
        return status, json.loads((evidence / "summary.json").read_text()), evidence

    def add_access_inputs(self, *, expected=None):
        dockerfile = "ENV DatSubscriptions__Site=https://companion.example.invalid/catalog\n"
        if expected is not None:
            dockerfile += f"ADD --checksum=sha256:{expected} https://companion.example.invalid/pinned.dat /input\n"
        (self.repo / "Dockerfile").write_text(dockerfile)
        git(self.repo, "add", ".")
        git(self.repo, "commit", "-m", "add synthetic companion inputs")

    def test_disconnected_access_probe_keeps_scanner_results_and_remaining_evidence(self):
        self.add_access_inputs()
        response = http_response()
        self.addCleanup(response.close)
        with patch.object(audit.urllib.request, "urlopen", side_effect=[
                http.client.RemoteDisconnected("PROXY_DETAIL_MUST_NOT_LEAK"), response]) as opened:
            status, summary, evidence = self.run_audit(probe_access=True)

        self.assertEqual(status, 2)
        self.assertTrue(summary["scan_completed"])
        self.assertTrue(summary["scan_passed"])
        self.assertTrue(summary["access_qualification_requested"])
        self.assertFalse(summary["access_qualification_passed"])
        self.assertFalse(summary["publication_signoff"])
        self.assertEqual(summary["access_unresolved"], 1)
        self.assertEqual(opened.call_count, 2)
        access = json.loads((evidence / "companion-access.json").read_text())
        self.assertEqual(access[0]["error_type"], "RemoteDisconnected")
        self.assertEqual(access[1]["status"], 200)
        self.assertTrue((evidence / "commands.json").is_file())
        self.assertNotIn("PROXY_DETAIL_MUST_NOT_LEAK", (evidence / "companion-access.json").read_text())
        self.assertNotIn("PROXY_DETAIL_MUST_NOT_LEAK", self.output)

    def test_access_failure_keeps_incomplete_scanner_coverage_and_independent_archive_results(self):
        self.add_access_inputs()
        response = http_response()
        self.addCleanup(response.close)
        with patch.object(audit.urllib.request, "urlopen", side_effect=[ConnectionResetError("PROXY_DETAIL_MUST_NOT_LEAK"), response]):
            status, summary, evidence = self.run_audit(history=(0, None), probe_access=True)
        self.assertEqual(status, 2)
        self.assertFalse(summary["scan_completed"])
        self.assertFalse(summary["scan_passed"])
        self.assertEqual(summary["history_report_status"], "missing")
        self.assertFalse(summary["access_qualification_passed"])
        self.assertEqual(summary["access_unresolved"], 1)
        access = json.loads((evidence / "companion-access.json").read_text())
        self.assertEqual(access[0]["error_type"], "ConnectionResetError")
        self.assertEqual(access[1]["status"], 200)
        archives = json.loads((evidence / "archive-scan-results.json").read_text())
        self.assertTrue(archives[0]["scan_completed"])
        self.assertTrue((evidence / "commands.json").is_file())
        self.assertFalse(summary["publication_signoff"])

    def test_truncated_probe_keeps_later_probe_and_complete_summary(self):
        self.add_access_inputs()
        responses = [http_response(b"partial", declared_length=100), http_response()]
        for response in responses:
            self.addCleanup(response.close)
        with patch.object(audit.urllib.request, "urlopen", side_effect=responses) as opened:
            status, summary, evidence = self.run_audit(probe_access=True)
        self.assertEqual(status, 2)
        self.assertTrue(summary["scan_completed"])
        self.assertTrue(summary["scan_passed"])
        self.assertFalse(summary["access_qualification_passed"])
        self.assertEqual(opened.call_count, 2)
        access = json.loads((evidence / "companion-access.json").read_text())
        self.assertEqual(access[0]["error_type"], "IncompleteRead")
        self.assertEqual(access[1]["status"], 200)
        self.assertTrue((evidence / "commands.json").is_file())

    def test_requested_checksum_mismatch_fails_qualification_without_rewriting_scanner_flags(self):
        self.add_access_inputs(expected=hashlib.sha256(b"expected content").hexdigest())
        responses = [http_response(), http_response(), http_response(b"different content")]
        for response in responses:
            self.addCleanup(response.close)
        with patch.object(audit.urllib.request, "urlopen", side_effect=responses):
            status, summary, evidence = self.run_audit(probe_access=True)
        self.assertEqual(status, 2)
        self.assertTrue(summary["scan_completed"])
        self.assertTrue(summary["scan_passed"])
        self.assertEqual(summary["access_unresolved"], 1)
        self.assertFalse(summary["access_qualification_passed"])
        access = json.loads((evidence / "companion-access.json").read_text())
        self.assertEqual(access[-1]["status"], 200)
        self.assertFalse(access[-1]["checksum_matches"])
        self.assertFalse(summary["publication_signoff"])

    def test_complete_requested_access_qualifies_without_granting_signoff(self):
        self.add_access_inputs()
        responses = [http_response(), http_response()]
        for response in responses:
            self.addCleanup(response.close)
        with patch.object(audit.urllib.request, "urlopen", side_effect=responses):
            status, summary, _ = self.run_audit(probe_access=True)
        self.assertEqual(status, 0)
        self.assertTrue(summary["scan_passed"])
        self.assertTrue(summary["access_qualification_passed"])
        self.assertEqual(summary["access_unresolved"], 0)
        self.assertFalse(summary["publication_signoff"])

    def test_requested_non200_response_keeps_status_and_fails_access_qualification(self):
        self.add_access_inputs()
        responses = [http_response(status=201), http_response()]
        for response in responses:
            self.addCleanup(response.close)
        with patch.object(audit.urllib.request, "urlopen", side_effect=responses):
            status, summary, evidence = self.run_audit(probe_access=True)
        self.assertEqual(status, 2)
        self.assertTrue(summary["scan_completed"])
        self.assertTrue(summary["scan_passed"])
        self.assertEqual(summary["access_unresolved"], 1)
        self.assertFalse(summary["access_qualification_passed"])
        access = json.loads((evidence / "companion-access.json").read_text())
        self.assertEqual([item["status"] for item in access], [201, 200])
        self.assertFalse(summary["publication_signoff"])

    def test_access_failure_does_not_hide_completed_scanner_findings(self):
        self.add_access_inputs()
        response = http_response()
        self.addCleanup(response.close)
        finding = json.dumps([{"RuleID": "synthetic", "File": "readme.txt", "StartLine": 1}])
        with patch.object(audit.urllib.request, "urlopen", side_effect=[http.client.RemoteDisconnected("PROXY_DETAIL_MUST_NOT_LEAK"), response]):
            status, summary, evidence = self.run_audit(history=(1, finding), probe_access=True)
        self.assertEqual(status, 2)
        self.assertTrue(summary["scan_completed"])
        self.assertFalse(summary["scan_passed"])
        self.assertEqual(summary["history_exit_code"], 1)
        self.assertEqual(summary["history_findings"], 1)
        self.assertFalse(summary["access_qualification_passed"])
        self.assertTrue((evidence / "commands.json").is_file())
        self.assertFalse(summary["publication_signoff"])

    def test_unrequested_access_stays_not_run_and_does_not_change_scanner_exit(self):
        self.add_access_inputs()
        with patch.object(audit.urllib.request, "urlopen") as opened:
            status, summary, evidence = self.run_audit()
        self.assertEqual(status, 0)
        self.assertTrue(summary["scan_passed"])
        self.assertFalse(summary["access_qualification_requested"])
        self.assertIsNone(summary["access_qualification_passed"])
        self.assertEqual(summary["access_unresolved"], 2)
        self.assertTrue(all(item["status"] == "not-run" for item in json.loads((evidence / "companion-access.json").read_text())))
        self.assertFalse(summary["publication_signoff"])
        opened.assert_not_called()

    def test_clean_reports_and_zero_exits_pass(self):
        status, summary, _ = self.run_audit()
        self.assertEqual(status, 0)
        self.assertTrue(summary["scan_completed"])
        self.assertTrue(summary["scan_passed"])

    def test_renamed_large_archive_is_detected_and_privacy_gap_blocks(self):
        with zipfile.ZipFile(self.repo / "large-opaque.dat", "w") as package:
            package.writestr("payload.txt", "Synthetic payload.\n" * 128)
        git(self.repo, "add", ".")
        git(self.repo, "commit", "-m", "add synthetic large archive")
        with patch.object(audit, "PRIVACY_TEXT_LIMIT", 256):
            status, summary, evidence = self.run_audit()
        self.assertEqual(status, 2)
        self.assertFalse(summary["scan_completed"])
        self.assertFalse(summary["scan_passed"])
        self.assertEqual(summary["archive_blobs_for_review"], 2)
        self.assertEqual(summary["large_blobs_for_manual_review"], 1)
        archives = json.loads((evidence / "archives-review.json").read_text())
        renamed = next(item for item in archives if item["path"] == "large-opaque.dat")
        self.assertTrue(renamed["stored_name"].endswith(".zip"))
        self.assertEqual(renamed["inspection"]["members"][0]["name"], "payload.txt")

    def test_missing_or_malformed_history_reports_are_incomplete(self):
        for payload, report_status in ((None, "missing"), ("[", "unreadable-or-malformed"),
                                       ("{}", "invalid-shape"), ("[1]", "invalid-shape")):
            with self.subTest(payload=payload):
                status, summary, evidence = self.run_audit(history=(0, payload))
                self.assertEqual(status, 2)
                self.assertFalse(summary["scan_completed"])
                self.assertFalse(summary["scan_passed"])
                self.assertEqual(summary["history_report_status"], report_status)
                self.assertIsNone(summary["history_findings"])
                # A failed history report does not suppress independent archive evidence.
                archives = json.loads((evidence / "archive-scan-results.json").read_text())
                self.assertTrue(archives[0]["scan_completed"])

    def test_nonzero_archive_exit_cannot_pass_with_empty_report(self):
        status, summary, evidence = self.run_audit(archive=(1, "[]"))
        self.assertEqual(status, 1)
        self.assertTrue(summary["scan_completed"])
        self.assertFalse(summary["scan_passed"])
        self.assertEqual(summary["archive_findings"], 0)
        archives = json.loads((evidence / "archive-scan-results.json").read_text())
        self.assertEqual(archives[0]["exit_code"], 1)

    def test_missing_or_malformed_archive_reports_are_incomplete(self):
        for exit_code, payload in ((1, None), (0, None), (0, "["), (0, "null")):
            with self.subTest(exit_code=exit_code, payload=payload):
                status, summary, evidence = self.run_audit(archive=(exit_code, payload))
                self.assertEqual(status, 2)
                self.assertFalse(summary["scan_completed"])
                self.assertFalse(summary["scan_passed"])
                self.assertEqual(summary["archive_scanner_errors"], 1)
                archives = json.loads((evidence / "archive-scan-results.json").read_text())
                self.assertIsNone(archives[0]["findings"])

    def test_nonzero_history_exit_cannot_pass_with_empty_report(self):
        status, summary, _ = self.run_audit(history=(1, "[]"))
        self.assertEqual(status, 1)
        self.assertTrue(summary["scan_completed"])
        self.assertFalse(summary["scan_passed"])

    def test_scanner_error_exit_remains_incomplete_with_valid_report(self):
        status, summary, _ = self.run_audit(archive=(2, "[]"))
        self.assertEqual(status, 2)
        self.assertFalse(summary["scan_completed"])
        self.assertFalse(summary["scan_passed"])

    def test_reported_findings_block_even_when_process_returns_zero(self):
        finding = json.dumps([{"RuleID": "synthetic", "File": "readme.txt", "StartLine": 1}])
        for surface in ("history", "archive"):
            with self.subTest(surface=surface):
                status, summary, _ = self.run_audit(**{surface: (0, finding)})
                self.assertEqual(status, 1)
                self.assertTrue(summary["scan_completed"])
                self.assertFalse(summary["scan_passed"])
                self.assertEqual(summary[f"{surface}_findings"], 1)

    def test_findings_exit_and_count_are_preserved(self):
        finding = json.dumps([{"RuleID": "synthetic", "File": "readme.txt", "StartLine": 1}])
        for surface in ("history", "archive"):
            with self.subTest(surface=surface):
                status, summary, _ = self.run_audit(**{surface: (1, finding)})
                self.assertEqual(status, 1)
                self.assertTrue(summary["scan_completed"])
                self.assertFalse(summary["scan_passed"])
                self.assertEqual(summary[f"{surface}_findings"], 1)


if __name__ == "__main__":
    unittest.main()
