#!/usr/bin/env python3
"""Scan a new full mirror and write private publication-review evidence."""

import argparse
from collections import Counter
from contextlib import nullcontext
from datetime import datetime, timezone
import hashlib
import http.client
import json
import os
import platform
from pathlib import Path
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import zipfile
import tarfile
import zlib

ROOT = Path(__file__).resolve().parents[2]
ARCHIVE = re.compile(r"\.(?:zip|tar|tgz|gz|bz2|xz|zst|7z|rar)$", re.I)
ASSET = re.compile(r"\.(?:png|jpe?g|webp|gif|svg|ico|icns|pdf|ttf|otf|woff2?|rom|iso|chd|rvz|bin|cue|nes|gb[ac]?|sfc|smc)$", re.I)
PRIVACY_TEXT_LIMIT = 64 * 1024 * 1024
DIRECT_BLOB_LIMIT = 64 * 1024 * 1024
PRIVACY = {
    "personal-path": re.compile(rb"/(?:Users|home)/[^/\s\"'<>]+|[A-Z]:\\Users\\[^\\\s\"'<>]+"),
    "private-address": re.compile(rb"(?:https?://)?(?:10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})(?=[:/\s\"']|$)|https?://[^/\s\"']+\.(?:local|internal|lan)(?=[:/\s\"']|$)"),
}


def audit_environment():
    # Replacement refs are review inputs, never permission to hide the original
    # history. Gitleaks inherits this too for the Git processes it launches.
    return {**os.environ, "GIT_NO_REPLACE_OBJECTS": "1"}


def run(command, *, cwd=None, log=None, allowed=(0,)):
    result = subprocess.run(command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            env=audit_environment())
    if log:
        log.write_bytes(result.stdout + result.stderr)
    if allowed is not None and result.returncode not in allowed:
        # Logs may contain private URLs or findings. Never echo them.
        raise RuntimeError(f"{Path(command[0]).name} failed ({result.returncode}); inspect private evidence")
    return result


def git(repo, *args):
    return run(["git", "--git-dir", str(repo), *args]).stdout


def historical_path_aliases(repo):
    # rev-list --objects supplies only one path per object. Raw root and merge
    # diffs without rename detection retain every historical blob/path pairing,
    # including transient names absent from all ref-tip trees.
    raw = git(repo, "log", "--all", "--full-history", "--root", "-m", "--raw",
              "--no-renames", "--no-abbrev", "--no-ext-diff", "--no-textconv", "--format=", "-z")
    entries = iter(raw.split(b"\0"))
    aliases = {}
    for header in entries:
        header = header.lstrip(b"\n")
        if not header:
            continue
        fields = header.removeprefix(b":").split()
        if not header.startswith(b":") or len(fields) != 5 or fields[4][:1] in (b"R", b"C"):
            raise ValueError("Unexpected historical raw-diff record")
        path = next(entries, None)
        if path is None:
            raise ValueError("Missing historical raw-diff path")
        path = path.decode("utf-8", errors="surrogateescape")
        for oid in fields[2:4]:
            if oid.strip(b"0"):
                aliases.setdefault(oid.decode("ascii"), set()).add(path)
    return aliases


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def scanner_completed(result):
    # Gitleaks can log archive/read errors without returning an error status.
    # Such diagnostics describe coverage gaps and must not produce a clean pass.
    diagnostics = result.stdout + result.stderr
    return result.returncode in (0, 1) and re.search(rb"\b(?:ERR|FTL)\b|panic:", diagnostics) is None


def scanner_result(result, report):
    # A successful process without its expected machine-readable report cannot
    # establish coverage. Keep other scans running, but retain this gap explicitly.
    try:
        findings = json.loads(report.read_text())
    except FileNotFoundError:
        status, count = "missing", None
    except (OSError, UnicodeError, json.JSONDecodeError):
        status, count = "unreadable-or-malformed", None
    else:
        if not isinstance(findings, list) or any(not isinstance(item, dict) for item in findings):
            status, count = "invalid-shape", None
        else:
            status, count = "parsed", len(findings)
    return {"exit_code": result.returncode, "report_status": status, "findings": count,
            "scan_completed": scanner_completed(result) and status == "parsed"}


def create_evidence(path):
    path = path.resolve()
    # A parent outside the worktree prevents accidental staging of raw findings.
    if path == ROOT or ROOT in path.parents:
        raise ValueError("Evidence must be outside the source checkout")
    parent = subprocess.run(["git", "-C", str(path.parent), "rev-parse", "--show-toplevel"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=audit_environment())
    if parent.returncode == 0:
        raise ValueError("Evidence parent must be outside every Git worktree")
    path.mkdir(parents=False, exist_ok=False, mode=0o700)
    return path


def access_probe(url, expected=None):
    # No auth headers, cookies, git credential helper, or URL credentials.
    record = {"url": url, "authentication": "none"}
    try:
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme != "https" or parsed.username or parsed.password:
            return {**record, "status": "rejected-nonanonymous-https-url"}
        with urllib.request.urlopen(url, timeout=60) as response:
            digest = hashlib.sha256()
            size = 0
            while chunk := response.read(1024 * 1024):
                size += len(chunk)
                # Pinned native source archives (e.g. MAME) exceed 128 MiB.
                # Stream them through the digest without retaining their body.
                limit = 1024 * 1024 * 1024 if expected else 8 * 1024 * 1024
                if size > limit:
                    raise ValueError("access probe exceeds reviewed input size bound")
                digest.update(chunk)
            # Bounded HTTPResponse.read() can return EOF without raising when
            # Content-Length was not satisfied. A partial body is not access evidence.
            remaining = getattr(response, "length", None)
            if remaining is not None and remaining > 0:
                raise http.client.IncompleteRead(b"", remaining)
        record.update(status=response.status, sha256=digest.hexdigest(), bytes=size)
        if expected:
            record.update(expected_sha256=expected, checksum_matches=digest.hexdigest() == expected)
    except (urllib.error.URLError, http.client.HTTPException, OSError, ValueError) as error:
        # Exception text can include private local proxy details; retain only its type.
        record.update(status="failed", error_type=type(error).__name__)
    return record


def archive_members(path):
    """List without extracting paths; unsupported/encrypted archives stay pending."""
    try:
        with path.open("rb") as raw:
            header = raw.read(32)
        if header.startswith(b"7z\xbc\xaf\x27\x1c"):
            if len(header) < 32 or zlib.crc32(header[12:32]) != int.from_bytes(header[8:12], "little"):
                return {"status": "manual-malformed-7z-header"}
        if zipfile.is_zipfile(path):
            with zipfile.ZipFile(path) as package:
                members = [{"name": item.filename, "bytes": item.file_size,
                            "encrypted": bool(item.flag_bits & 1)} for item in package.infolist()]
        elif tarfile.is_tarfile(path):
            with tarfile.open(path) as package:
                members = []
                for item in package:
                    members.append({"name": item.name, "bytes": item.size,
                                    "link": item.linkname if item.issym() or item.islnk() else None})
                    if len(members) >= 100000:
                        return {"status": "manual-member-limit"}
        else:
            return {"status": "manual-format-review"}
        return {"status": "listed-not-dispositioned", "members": members}
    except (OSError, ValueError, tarfile.TarError, zipfile.BadZipFile):
        return {"status": "manual-unreadable-archive"}


def archive_suffix(content, path):
    # Gitleaks' extractor selection uses filenames. Canonicalize a recognized
    # signature so renaming an archive cannot evade its archive traversal.
    for signature, suffix in ((b"PK\x03\x04", ".zip"), (b"\x1f\x8b", ".gz"),
                              (b"BZh", ".bz2"), (b"\xfd7zXZ\x00", ".xz"),
                              (b"7z\xbc\xaf\x27\x1c", ".7z"), (b"Rar!\x1a\x07", ".rar"),
                              (b"\x28\xb5\x2f\xfd", ".zst")):
        if content.startswith(signature):
            return suffix
    if content[257:262] == b"ustar":
        return ".tar"
    return Path(path).suffix if ARCHIVE.search(path) else None


def audit(args):
    os.umask(0o077)
    evidence = create_evidence(args.evidence)
    args.created_evidence = True
    pin = json.loads(Path(__file__).with_name("scanner.json").read_text())
    scanner = args.gitleaks.resolve()
    version = run([str(scanner), "version"]).stdout.decode().strip()
    if version != pin["version"]:
        raise ValueError(f"Expected Gitleaks {pin['version']}; received another version")
    config = ROOT / ".gitleaks.toml"
    metadata = {
        "started_utc": datetime.now(timezone.utc).isoformat(),
        "command": sys.argv, "cwd": str(Path.cwd()), "environment": platform.platform(),
        "python_version": platform.python_version(),
        "git_version": run(["git", "--version"]).stdout.decode().strip(),
        "runner_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "runner_source_commit": run(["git", "rev-parse", "HEAD"], cwd=ROOT).stdout.decode().strip(),
        "scanner_version": version, "scanner_sha256": hashlib.sha256(scanner.read_bytes()).hexdigest(),
        "scanner_config_sha256": hashlib.sha256(config.read_bytes()).hexdigest(),
        "publication_signoff": False, "source": args.source,
    }
    write_json(evidence / "run.json", metadata)
    mirror = evidence / "repository.git"
    run(["git", "clone", "--mirror", "--no-local", "--", args.source, str(mirror)], log=evidence / "clone.log")
    if git(mirror, "rev-parse", "--is-shallow-repository").strip() != b"false":
        raise ValueError("Publication audit requires a full, non-shallow clone")
    refs = [line.split(" ", 1) for line in git(mirror, "for-each-ref", "--format=%(refname) %(objectname)").decode().splitlines()]
    if not refs:
        raise ValueError("No refs to scan")
    write_json(evidence / "refs.json", [{"ref": name, "object": oid,
        "commit": git(mirror, "rev-parse", f"{name}^{{commit}}").decode().strip()} for name, oid in refs])
    refnames = [name for name, _ in refs]
    # Use every acquired ref, including retained non-branch refs. No allowlist or
    # inline allow comment from the audited repository can suppress a finding.
    ignore = evidence / "empty.gitleaksignore"
    ignore.touch()
    common = ["--config", str(config), "--gitleaks-ignore-path", str(ignore),
              "--ignore-gitleaks-allow", "--redact=100", "--no-banner", "--no-color",
              "--max-decode-depth", "5", "--report-format", "json"]
    # Archive traversal belongs to the complete historical-blob pass below,
    # rather than Git's patch stream. This also isolates decoder crashes so
    # malformed media cannot prevent the remaining archives being inspected.
    # Separate merge diffs expose secrets introduced only while resolving a
    # merge; Git's default history patch stream omits those merge changes.
    history_command = [str(scanner), "git", *common, "--max-archive-depth", "0",
                       "--log-opts=--all --full-history -m --no-ext-diff --no-textconv",
                       "--report-path", str(evidence / "history-secrets.json"), str(mirror)]
    history = run(history_command, log=evidence / "history-scan.log", allowed=None)
    history_result = scanner_result(history, evidence / "history-secrets.json")
    # All unique historical objects, with the complete alias map kept privately.
    objects = git(mirror, "rev-list", "--objects", "--all", "--no-object-names").decode().splitlines()
    aliases = historical_path_aliases(mirror)
    object_ids = "\n".join(objects) + "\n"
    sizes = subprocess.run(["git", "--git-dir", str(mirror), "cat-file", "--batch-check=%(objectname) %(objecttype) %(objectsize)"],
                           input=object_ids.encode(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True,
                           env=audit_environment())
    information = {line.split()[0]: line.split()[1:] for line in sizes.stdout.decode().splitlines()}
    inventory, privacy, archives, assets, skipped, direct_blobs = [], [], [], [], [], []
    archive_dir = evidence / "archive-blobs"
    archive_dir.mkdir(mode=0o700)
    direct_dir = evidence / "direct-blobs"
    direct_dir.mkdir(mode=0o700)
    # One cat-file process avoids launching a subprocess for every historical blob.
    with subprocess.Popen(["git", "--git-dir", str(mirror), "cat-file", "--batch"],
                             stdin=subprocess.PIPE, stdout=subprocess.PIPE, env=audit_environment()) as blobs:
        for oid in objects:
            kind, size = information[oid]
            if kind != "blob":
                continue
            size = int(size)
            paths = sorted(aliases.get(oid, ()))
            if not paths:
                raise ValueError("Historical blob has no historical path inventory")
            path = paths[0]
            item = {"object": oid, "path": path, "paths": paths, "bytes": size}
            inventory.append(item)
            if any(ASSET.search(alias) for alias in paths):
                assets.append(item)
            archive_path = next((alias for alias in paths if ARCHIVE.search(alias)), path)
            blobs.stdin.write((oid + "\n").encode())
            blobs.stdin.flush()
            header = blobs.stdout.readline().split()
            if header != [oid.encode(), b"blob", str(size).encode()]:
                raise ValueError("Unexpected historical blob header")
            remaining = int(header[2])
            prefix = blobs.stdout.read(min(remaining, 512))
            remaining -= len(prefix)
            suffix = archive_suffix(prefix, archive_path)
            target = archive_dir / (oid + suffix) if suffix is not None else None
            if suffix is None:
                # Git patches omit binary content (including text forced binary by
                # attributes). Scan every unique nonarchive blob independently of
                # its historical pathname, never only the text Git emits in patches.
                target = direct_dir / oid if size <= DIRECT_BLOB_LIMIT else None
                direct_blobs.append({**item, "stored_name": target.name if target else None,
                                     "skip_reason": None if target else "direct secret scan exceeds 64 MiB; manual inspection required"})
            chunks = [prefix] if size <= PRIVACY_TEXT_LIMIT else None
            with target.open("wb") if target else nullcontext() as blob_file:
                if blob_file:
                    blob_file.write(prefix)
                while remaining:
                    chunk = blobs.stdout.read(min(remaining, 1024 * 1024))
                    if not chunk:
                        raise ValueError("Incomplete historical blob read")
                    if blob_file:
                        blob_file.write(chunk)
                    if chunks is not None:
                        chunks.append(chunk)
                    remaining -= len(chunk)
                if blobs.stdout.read(1) != b"\n":
                    raise ValueError("Incomplete historical blob framing")
            if suffix is not None:
                # Gitleaks handles supported archives recursively. The separate
                # archive inventory still requires manual format/depth/privacy review.
                archives.append({**item, "stored_name": target.name, "inspection": archive_members(target)})
            if chunks is None:
                skipped.append({**item, "reason": "privacy text review exceeds 64 MiB; manual inspection required"})
                continue
            content = b"".join(chunks)
            if b"\0" in content[:8192]:
                continue
            for category, pattern in PRIVACY.items():
                for match in pattern.finditer(content):
                    privacy.append({"category": category, "object": oid, "path": path,
                                    "line": content.count(b"\n", 0, match.start()) + 1})
        blobs.stdin.close()
        blobs.stdout.close()
        if blobs.wait() != 0:
            raise ValueError("Historical blob inventory failed")
    for name, value in (("historical-blobs", inventory), ("privacy-review", privacy),
                        ("archives-review", archives), ("assets-review", assets), ("manual-large-blobs", skipped),
                        ("direct-blobs", direct_blobs)):
        write_json(evidence / f"{name}.json", value)
    # Save the full tracked-file tree for every ref, preserving aliases and modes.
    trees = evidence / "tracked-trees"
    trees.mkdir(mode=0o700)
    for index, ref in enumerate(refnames):
        (trees / f"{index:04}.txt").write_bytes(git(mirror, "ls-tree", "-rl", "--full-tree", ref))
    direct_report = evidence / "direct-blob-secrets.json"
    direct_command = [str(scanner), "dir", *common, "--max-archive-depth", "0",
                      "--report-path", str(direct_report), str(direct_dir)]
    direct_scan = run(direct_command, log=evidence / "direct-blob-scan.log", allowed=None)
    direct_result = scanner_result(direct_scan, direct_report)
    direct_skipped = sum(item["stored_name"] is None for item in direct_blobs)
    direct_result.update(targets=len(direct_blobs) - direct_skipped, skipped_blobs=direct_skipped)
    direct_result["scan_completed"] = direct_result["scan_completed"] and not direct_skipped
    write_json(evidence / "direct-blob-scan-result.json", direct_result)
    archive_results, archive_commands = [], []
    scans = evidence / "archive-scans"
    scans.mkdir(mode=0o700)
    for index, archive in enumerate(archives):
        target = archive_dir / archive["stored_name"]
        report = scans / f"{index:04}.json"
        command = [str(scanner), "dir", *common, "--max-archive-depth", "5", "--report-path", str(report), str(target)]
        archive_commands.append(command)
        result = run(command, log=scans / f"{index:04}.log", allowed=None)
        inspection = archive["inspection"]
        unreadable = inspection["status"] in ("manual-unreadable-archive", "manual-malformed-7z-header", "manual-member-limit")
        encrypted = any(item.get("encrypted") for item in inspection.get("members", []))
        outcome = scanner_result(result, report)
        outcome["scan_completed"] = outcome["scan_completed"] and not unreadable and not encrypted
        archive_results.append({"object": archive["object"], **outcome})
    write_json(evidence / "archive-scan-results.json", archive_results)
    access = []
    # Derive build/access review inputs from the scanned branch, not the runner's
    # working copy. Check all unique Dockerfiles at the acquired ref tips.
    dockerfiles = set()
    external_inputs = set()
    urls = set()
    for ref in refnames:
        for source_path in ("Dockerfile", ".gitmodules", "native/libchdr/libchdr.json", "native/chdman/mame.json", "native/chdman/notices/runtime/SOURCE_REFERENCES.md", "web/tools/emulatorjs/pin.json"):
            result = subprocess.run(["git", "--git-dir", str(mirror), "show", f"{ref}:{source_path}"],
                                    stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=audit_environment())
            if result.returncode != 0:
                continue
            text = result.stdout.decode(errors="replace")
            if source_path == "Dockerfile":
                dockerfiles.add(text.replace("\\\n", " "))
            elif source_path == ".gitmodules":
                for url in re.findall(r"url\s*=\s*(https://\S+)", text):
                    urls.add((url.rstrip("/") + "/info/refs?service=git-upload-pack", None))
            elif source_path == "native/libchdr/libchdr.json":
                manifest = json.loads(text)
                repository = manifest.get("repository", "")
                commit = manifest.get("commit", "")
                if repository.startswith("https://github.com/") and re.fullmatch("[0-9a-f]{40}", commit):
                    urls.add((repository.rstrip("/") + "/archive/" + commit + ".tar.gz", None))
            for url in re.findall(r'https://[^\s\"<>]+', text):
                external_inputs.add((source_path, url.rstrip("'\\")))
    write_json(evidence / "external-inputs-review.json", [
        {"source_path": path, "url": url, "status": "manual-provenance-and-access-review"}
        for path, url in sorted(external_inputs)
    ])
    for dockerfile in dockerfiles:
        for checksum, url in re.findall(r"ADD\s+--checksum=sha256:([0-9a-f]{64})\s+(https://\S+)", dockerfile):
            urls.add((url, checksum))
        for url in re.findall(r"DatSubscriptions__Site=(https://\S+)", dockerfile):
            urls.add((url.rstrip("/") + "/", None))
            urls.add((url.rstrip("/") + "/metadata/timestamp.json", None))
    for url, checksum in sorted(urls):
        access.append(access_probe(url, checksum) if args.probe_access else {"url": url, "status": "not-run", "expected_sha256": checksum})
    write_json(evidence / "companion-access.json", access)
    history_count = history_result["findings"]
    archive_count = sum(item["findings"] or 0 for item in archive_results)
    scan_completed = (history_result["scan_completed"] and direct_result["scan_completed"]
                      and all(item["scan_completed"] for item in archive_results) and not skipped)
    access_unresolved = sum(item.get("status") != 200 or item.get("checksum_matches") is False for item in access)
    access_qualification_passed = not access_unresolved if args.probe_access else None
    summary = {
        "finished_utc": datetime.now(timezone.utc).isoformat(),
        "scanner_version": version, "refs": len(refs), "historical_blobs": len(inventory),
        "history_findings": history_count, "history_exit_code": history.returncode,
        "history_report_status": history_result["report_status"],
        "direct_blob_findings": direct_result["findings"],
        "direct_blob_exit_code": direct_scan.returncode,
        "direct_blob_report_status": direct_result["report_status"],
        "direct_blob_scan_completed": direct_result["scan_completed"],
        "direct_blob_scan_targets": direct_result["targets"],
        "direct_blob_scan_skipped": direct_skipped,
        "archive_findings": archive_count,
        "archive_scanner_errors": sum(not item["scan_completed"] for item in archive_results),
        "archive_blobs_for_review": len(archives), "asset_blobs_for_review": len(assets),
        "privacy_candidates": dict(Counter(item["category"] for item in privacy)),
        "large_blobs_for_manual_review": len(skipped),
        "anonymous_access_probes": len(access),
        "access_unresolved": access_unresolved,
        "access_qualification_requested": args.probe_access,
        "access_qualification_passed": access_qualification_passed,
        "scan_completed": scan_completed,
        "scan_passed": scan_completed and history.returncode == 0 and history_count == 0
                       and direct_scan.returncode == 0 and direct_result["findings"] == 0
                       and all(item["exit_code"] == 0 for item in archive_results) and archive_count == 0,
        "publication_signoff": False,
        "manual_disposition_required": ["secret findings", "personal paths and service addresses", "archive completeness and contents", "screenshots and redistributed asset/fixture provenance", "companion repository/catalog publication access and trust", "private reporting channel", "visibility/history/rotation decisions"],
    }
    write_json(evidence / "summary.json", summary)
    write_json(evidence / "commands.json", [history_command, direct_command, *archive_commands])
    # Output counts only. The evidence directory itself is supplied by the caller.
    print(json.dumps(summary, indent=2))
    # Access failures do not rewrite scanner coverage. A requested qualification
    # still fails closed, after retaining every independent probe and summary.
    if not scan_completed or access_qualification_passed is False:
        return 2
    return 0 if summary["scan_passed"] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, help="remote URL or local full repository to mirror; all acquired refs are scanned")
    parser.add_argument("--evidence", type=Path, required=True, help="new directory under an existing private parent outside Git")
    parser.add_argument("--gitleaks", type=Path, required=True, help="pinned scanner installed with install_scanner.py")
    parser.add_argument("--probe-access", action="store_true", help="perform anonymous HTTPS checks of companion build/catalog inputs; unresolved checks exit 2 after evidence is written")
    args = parser.parse_args()
    try:
        return audit(args)
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        # Raw exception details (e.g. repository URLs) stay in private evidence.
        if getattr(args, "created_evidence", False):
            (args.evidence / "failure.txt").write_text(f"{type(error).__name__}: {error}\n")
        print("Publication audit could not complete; inspect private evidence.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
