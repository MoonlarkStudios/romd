#!/usr/bin/env python3
"""Validate this repository's retained 7z fixture review, without approving publication."""

import argparse
import binascii
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import random
import re
import stat
import struct
import subprocess
import tarfile

import audit
import install_scanner


GENERATOR_OBJECT = "9dc59e8945ec96d087a2bd1a5f0e04fbb1b60736"
GENERATOR_SHA256 = "61e4b928aab12a8753a6413a9b1b81d79953e5d59e169e43794de9600fd84483"
EXPECTED_GAPS = 20
MAX_ARCHIVE_BYTES = 1024 * 1024
MAX_JSON_BYTES = 16 * 1024 * 1024
OID = re.compile(r"[0-9a-f]{40}\Z")
DIGEST = re.compile(r"[0-9a-f]{64}\Z")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(content):
    return hashlib.sha256(content).hexdigest()


def integer(value):
    return type(value) is int and value >= 0


def relative_path(value):
    require(isinstance(value, str) and value and "\\" not in value and ":" not in value
            and not any(ord(char) < 32 for char in value), "Unsafe relative evidence path")
    path = PurePosixPath(value)
    require(not path.is_absolute() and all(part not in (".", "..") for part in value.split("/")),
            "Unsafe relative evidence path")
    return value


def regular_file(path, limit=MAX_JSON_BYTES):
    require(not path.is_symlink() and path.is_file(), "Missing or unsafe evidence file")
    require(stat.S_ISREG(path.stat().st_mode) and path.stat().st_size <= limit, "Unbounded evidence file")
    return path.read_bytes()


def child(root, name):
    relative_path(name)
    path = root / name
    require(path.resolve().is_relative_to(root), "Evidence path escapes selected root")
    for parent in (path, *path.parents):
        if parent == root:
            break
        require(not parent.is_symlink(), "Evidence symlinks are not accepted")
    return path


def unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON key")
        result[key] = value
    return result


def read_json(root, name):
    return json.loads(regular_file(child(root, name)), object_pairs_hook=unique_pairs,
                      parse_constant=lambda _: require(False, "Non-finite JSON number"))


def object_json(root, name):
    value = read_json(root, name)
    require(isinstance(value, dict), "Expected a JSON object receipt")
    return value


def private_root(path):
    require(not path.is_symlink() and path.is_dir(), "Missing or unsafe private input directory")
    root = path.resolve()
    result = audit.run(["git", "-C", str(root), "rev-parse", "--show-toplevel"], allowed=None)
    require(result.returncode != 0, "Review evidence must be outside Git worktrees")
    return root


def indexed(records, key="object"):
    require(isinstance(records, list) and all(isinstance(record, dict) for record in records), "Invalid record list")
    result = {}
    for record in records:
        value = record.get(key)
        require(isinstance(value, str) and OID.fullmatch(value), "Invalid Git object identity")
        require(value not in result, "Duplicate object record")
        result[value] = record
    return result


class Source:
    def __init__(self, path, revision):
        self.directory = Path(audit.run(["git", "-C", str(path), "rev-parse", "--absolute-git-dir"]).stdout.decode().strip())
        self.commit = audit.git(self.directory, "rev-parse", "--verify", "--end-of-options", revision + "^{commit}").decode().strip()
        self.tree = audit.git(self.directory, "rev-parse", self.commit + "^{tree}").decode().strip()
        self.paths = {}
        self.objects = {}
        self.cache = {}
        for entry in audit.git(self.directory, "ls-tree", "-rz", "--full-tree", self.commit).split(b"\0"):
            if not entry:
                continue
            meta, name = entry.split(b"\t", 1)
            mode, kind, oid = meta.decode().split()
            name = name.decode("utf-8", errors="surrogateescape")
            self.paths[name] = (mode, kind, oid)
            if kind == "blob":
                self.objects.setdefault(oid, set()).add(name)

    def blob(self, oid, limit=MAX_ARCHIVE_BYTES):
        require(oid in self.objects, "Object is not a blob in the qualified tree")
        size = int(audit.git(self.directory, "cat-file", "-s", oid))
        require(size <= limit, "Source blob exceeds reviewed bounds")
        if oid not in self.cache:
            self.cache[oid] = audit.git(self.directory, "cat-file", "blob", oid)
        require(len(self.cache[oid]) == size, "Incomplete Git blob")
        return self.cache[oid]

    def at(self, path):
        relative_path(path)
        record = self.paths.get(path)
        require(record is not None and record[0] in ("100644", "100755") and record[1] == "blob",
                "Source path is not a qualified regular blob")
        return record[2]

    def archive_inventory(self):
        """Check every blob's signature, including objects an editable old ledger calls nonarchives."""
        result = {}
        with subprocess.Popen(["git", "--git-dir", str(self.directory), "cat-file", "--batch"],
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              env=audit.audit_environment()) as process:
            try:
                for oid, paths in self.objects.items():
                    process.stdin.write((oid + "\n").encode())
                    process.stdin.flush()
                    header = process.stdout.readline().split()
                    require(len(header) == 3 and header[:2] == [oid.encode(), b"blob"], "Invalid Git blob framing")
                    size = int(header[2])
                    require(size <= audit.DIRECT_BLOB_LIMIT, "Blob exceeds bounded archive inventory")
                    prefix = process.stdout.read(min(size, 512))
                    remaining = size - len(prefix)
                    while remaining:
                        chunk = process.stdout.read(min(remaining, 1024 * 1024))
                        require(chunk, "Truncated Git blob")
                        remaining -= len(chunk)
                    require(process.stdout.read(1) == b"\n", "Invalid Git blob terminator")
                    if any(audit.archive_suffix(prefix, path) is not None for path in paths):
                        result[oid] = paths
            finally:
                process.stdin.close()
                process.stdout.close()
            require(process.wait() == 0, "Archive inventory Git process failed")
        return result


def number(value):
    for index in range(8):
        if value < (1 << (7 + 7 * index)):
            return bytes([(0xff << (8 - index) & 0xff) | (value >> (8 * index))]) + value.to_bytes(8, "little")[:index]
    return b"\xff" + value.to_bytes(8, "little")


def crc(content):
    return struct.pack("<I", binascii.crc32(content))


def archive(header, packed):
    start = struct.pack("<QQ", len(packed), len(header)) + crc(header)
    return bytes.fromhex("377abcaf271c0004") + crc(start) + start + packed + header


def synthetic(names, payloads, declared=None, coder=b"\x01\x00", empty_flags=None, anti_flags=None, attributes=None):
    """Fixed bounded port of the pinned generator's pure Copy-header encoder; never extract names."""
    header = b"\x01"
    if payloads:
        header += b"\x04\x06\x00" + number(len(payloads)) + b"\x09"
        header += b"".join(number(len(payload)) for payload in payloads) + b"\x00\x07\x0b"
        header += number(len(payloads)) + b"\x00" + b"".join(b"\x01" + coder for _ in payloads)
        header += b"\x0c" + b"".join(number(declared if declared is not None else len(payload)) for payload in payloads)
        header += b"\x0a\x01" + b"".join(crc(payload) for payload in payloads) + b"\x00\x08\x00\x00"
    header += b"\x05" + number(len(names))
    if empty_flags is not None:
        header += b"\x0e\x01" + bytes([empty_flags]) + b"\x0f\x01\x80"
    if anti_flags is not None:
        header += b"\x10\x01" + bytes([anti_flags])
    if attributes is not None:
        value = b"\x01\x00" + b"".join(struct.pack("<I", item) for item in attributes)
        header += b"\x15" + number(len(value)) + value
    names_bytes = b"\x00" + b"".join(name.encode("utf-16le") + b"\x00\x00" for name in names)
    return archive(header + b"\x11" + number(len(names_bytes)) + names_bytes + b"\x00\x00", b"".join(payloads))


def replay(seeds):
    """Only reviewed in-memory transforms. No exec, imports, filesystem writes, or decoder calls."""
    outputs = {}
    for filename, name in [("traversal.7z", "../escape.rom"), ("absolute.7z", "/escape.rom"),
                           ("backslash.7z", "..\\escape.rom"), ("drive.7z", "C:\\escape.rom"),
                           ("nul.7z", "a\x00evil.rom"), ("overlong.7z", "/".join(["a" * 120] * 9) + "/a.rom")]:
        outputs[filename] = synthetic([name], [b"alpha"])
    for filename, names in [("duplicate.7z", ["a.rom", "a.rom"]), ("case-collision.7z", ["a.rom", "A.rom"]),
                            ("parent-collision.7z", ["a.rom", "A.rom/b.rom"]),
                            ("parent-case.7z", ["one/a.rom", "ONE/b.rom"]),
                            ("normalized-collision.7z", ["a.rom", "one/../a.rom"])]:
        outputs[filename] = synthetic(names, [b"alpha", b"beta"])
    outputs["implicit-parents.7z"] = synthetic(["one/two/a.rom"], [b"alpha"])
    outputs["declared-size.7z"] = synthetic(["a.rom"], [b"alpha"], declared=1 << 40)
    for filename, coder in [("oversized-dictionary.7z", "21210128"), ("oversized-lzma.7z", "23030101055d00000080"),
                            ("ppmd.7z", "23030401050600000080"), ("unsupported-coder.7z", "01ff")]:
        outputs[filename] = synthetic(["a.rom"], [b"alpha"], coder=bytes.fromhex(coder))
    outputs["symlink.7z"] = synthetic(["a.rom"], [b"alpha"], attributes=[0xa1ff8000])
    outputs["backslash-directory.7z"] = synthetic(["one\\two\\a.rom"], [b"alpha"])
    outputs["overflow-declared.7z"] = synthetic(["a.rom", "b.rom"], [b"alpha", b"beta"], declared=1 << 62)
    outputs["anti.7z"] = synthetic(["a.rom", "gone.rom"], [b"alpha"], empty_flags=0x40, anti_flags=0x80)
    outputs["truncated.7z"] = seeds["solid.7z"][:-10]
    corrupt = bytearray(seeds["large.7z"])
    require(len(corrupt) > 40, "Truncated replay seed")
    corrupt[40] ^= 0x80
    outputs["corrupt.7z"] = bytes(corrupt)
    base = seeds["solid.7z"]
    offset, size = struct.unpack_from("<QQ", base, 12)
    require(32 + offset + size == len(base), "Unaccounted replay seed bytes")
    encoded = bytearray(base[32 + offset:])
    position = encoded.index(bytes.fromhex("2303010105")) + 5
    encoded[position + 1:position + 5] = (1 << 31).to_bytes(4, "little")
    outputs["oversized-header-dictionary.7z"] = archive(encoded, base[32:32 + offset])
    require(all(len(value) <= MAX_ARCHIVE_BYTES for value in outputs.values()), "Unbounded replay result")
    return outputs


def complete_7z(content):
    require(len(content) >= 32 and content.startswith(bytes.fromhex("377abcaf271c")), "Invalid seed format")
    require(crc(content[12:32]) == content[8:12], "Invalid seed start header CRC")
    offset, size = struct.unpack_from("<QQ", content, 12)
    require(32 + offset + size == len(content), "Unaccounted seed bytes")
    require(crc(content[32 + offset:]) == content[28:32], "Invalid seed next header CRC")


def validate_inventory(source, evidence):
    archives = indexed(read_json(evidence, "archives-review.json"))
    results = indexed(read_json(evidence, "archive-scan-results.json"))
    old_blobs = indexed(read_json(evidence, "historical-blobs.json"))
    original = Source(child(evidence, "repository.git"), "HEAD")
    refs = read_json(evidence, "refs.json")
    require(isinstance(refs, list) and len(refs) == 1 and isinstance(refs[0], dict)
            and refs[0].get("ref") == "refs/heads/main"
            and refs[0].get("commit") == refs[0].get("object") == original.commit,
            "Audit source is not the bound single-root snapshot")
    require(audit.git(original.directory, "for-each-ref", "--format=%(refname) %(objectname)").strip()
            == ("refs/heads/main " + original.commit).encode(), "Audit snapshot has extra refs")
    require(audit.git(original.directory, "rev-list", "--all", "--count").strip() == b"1",
            "Audit snapshot has extra history")
    require(set(old_blobs) == set(original.objects)
            and all(set(record.get("paths", [])) == original.objects[oid] for oid, record in old_blobs.items()),
            "Historical blob inventory differs from its retained snapshot")
    require(archives and set(archives) == set(results), "Archive inventory and report sets differ")
    current = source.archive_inventory()
    expected = {oid: set(record.get("paths", [])) for oid, record in archives.items()}
    require(current == expected, "New, changed, missing, or renamed archives require a fresh archive review")
    failed = {}
    expected_artifacts = set()
    for index, (oid, record) in enumerate(archives.items()):
        require(integer(record.get("bytes")) and record["bytes"] <= MAX_ARCHIVE_BYTES, "Unbounded archive")
        require(all(relative_path(path) for path in record["paths"]), "Unsafe archive alias")
        require(all(source.paths[path] == original.paths.get(path) and source.paths[path][0] in ("100644", "100755")
                    for path in record["paths"]), "Archive path mode differs from its regular audited artifact")
        require(len(source.blob(oid)) == record["bytes"], "Archive size differs from audit")
        result = results[oid]
        require(type(result.get("scan_completed")) is bool, "Invalid original scanner completion")
        require(type(result.get("exit_code")) is int and result["exit_code"] in (0, 1, 2)
                and result.get("report_status") in ("parsed", "missing"), "Invalid original scanner report")
        require(result.get("findings") is None or integer(result["findings"]), "Invalid original finding count")
        require((result["report_status"] == "parsed") == integer(result.get("findings")),
                "Original report status and finding count disagree")
        report = child(evidence, f"archive-scans/{index:04}.json")
        expected_artifacts.add(f"{index:04}.log")
        if result["report_status"] == "parsed":
            expected_artifacts.add(f"{index:04}.json")
        if report.exists() or report.is_symlink():
            regular_file(report)
        log = regular_file(child(evidence, f"archive-scans/{index:04}.log"))
        observed = audit.scanner_result(subprocess.CompletedProcess([], result["exit_code"], log, b""), report)
        inspection = record.get("inspection")
        require(isinstance(inspection, dict), "Invalid original archive inspection")
        members = inspection.get("members", [])
        require(isinstance(members, list) and all(isinstance(member, dict) for member in members), "Invalid inspection member record")
        unreadable = inspection.get("status") in ("manual-unreadable-archive", "manual-malformed-7z-header", "manual-member-limit")
        encrypted = any(member.get("encrypted") for member in members)
        observed["scan_completed"] = observed["scan_completed"] and not unreadable and not encrypted
        require(all(observed[key] == result.get(key) for key in observed), "Original scanner record contradicts retained report or log")
        require(not result.get("findings"), "Original archive findings require separate disposition")
        if not result["scan_completed"]:
            failed[oid] = record
        else:
            require(result["exit_code"] == 0 and result["report_status"] == "parsed" and result["findings"] == 0,
                    "Non-gap original archive result is not clean")
    require({path.name for path in child(evidence, "archive-scans").iterdir()} == expected_artifacts,
            "Missing or extra retained archive report artifacts")
    require(len(failed) == EXPECTED_GAPS, "This command requires exactly the reviewed twenty archive gaps")
    return archives, failed, results


def expected_members():
    return {"solid.7z": {"a.rom": b"alpha", "b.rom": b"beta", "tree/nested.rom": b"nested", "empty.rom": b""},
            "large.7z": {"large.rom": random.Random(1234).randbytes(160000)}}


def retained_payloads(root, expected):
    found = {}
    files = list(root.rglob("*"))
    require(len(files) <= 512, "Private artifact inventory exceeds reviewed bounds")
    for path in files:
        require(not path.is_symlink() and path.resolve().is_relative_to(root), "Unsafe private artifact path")
        if path.is_file() and path.stat().st_size in {len(value) for value in expected.values()}:
            content = regular_file(path, MAX_ARCHIVE_BYTES)
            key = digest(content)
            if key in expected:
                require(content == expected[key], "Decoded artifact differs from generator literal")
                found.setdefault(key, content)
    require(set(found) == set(expected), "Missing retained decoded payload")
    return found


def validate_provenance(source, evidence, dispositions, failed):
    proof = object_json(dispositions, "archive-provenance.json")
    require(proof.get("generator_object") == GENERATOR_OBJECT and GENERATOR_OBJECT in source.objects,
            "Generator identity is not the reviewed version")
    require(all(source.paths[path][0] in ("100644", "100755") for path in source.objects[GENERATOR_OBJECT]),
            "Generator is not a qualified regular artifact")
    require(digest(source.blob(GENERATOR_OBJECT)) == GENERATOR_SHA256, "Generator pin mismatch")
    records = indexed(proof.get("failed_archive_records"))
    require(set(records) == set(failed), "Missing or extra archive provenance records")
    compressed = indexed(read_json(dispositions, "compressed-seed-disposition.json"), "source_object")
    require(len(compressed) == 2, "Missing or extra compressed seed records")
    expected = expected_members()
    seeds, decoded = {}, {}
    for oid, record in compressed.items():
        path = relative_path(record.get("source_path"))
        name = PurePosixPath(path).name
        require(name in expected and name not in seeds and source.at(path) == oid, "Unbound compressed seed")
        content = source.blob(oid)
        require(digest(content) == record.get("source_sha256"), "Compressed seed hash mismatch")
        complete_7z(content)
        members = record.get("members")
        require(isinstance(members, list) and len(members) == len(expected[name]), "Missing or extra seed payload records")
        observed = set()
        for member in members:
            require(isinstance(member, dict), "Invalid member record")
            member_path = relative_path(member.get("path"))
            require(member_path not in observed and member_path in expected[name], "Duplicate or unaccounted seed member")
            observed.add(member_path)
            payload = expected[name][member_path]
            require(integer(member.get("bytes")) and member["bytes"] == len(payload) and member.get("sha256") == digest(payload)
                    and member.get("matches_generator") is True, "Decoded member differs from pinned generator")
            decoded[digest(payload)] = payload
        require(integer(record.get("directory_count")) and record["directory_count"] == (2 if name == "solid.7z" else 0)
                and record.get("entire_physical_blob_accounted_for") is True
                and record.get("all_payloads_match_generator_literals_or_fixed_prng") is True
                and record.get("decoder_list_test_extract_passed") is True, "Incomplete seed attestation")
        seeds[name] = content
    require(set(seeds) == set(expected), "Compressed seed roles differ")
    reads = proof.get("seed_reads")
    require(isinstance(reads, list) and len(reads) == 3, "Missing or extra generator seed reads")
    for record in reads:
        require(isinstance(record, dict), "Invalid generator seed record")
        require(record.get("object") in compressed and record.get("sha256") == compressed[record["object"]]["source_sha256"]
                and PurePosixPath(relative_path(record.get("path"))).name
                == PurePosixPath(compressed[record["object"]]["source_path"]).name, "Unbound generator seed read")
    require({record["object"] for record in reads} == set(compressed), "Generator seed read set differs")
    encrypted = object_json(dispositions, "encrypted-fixture-disposition.json")
    oid = encrypted.get("source_object")
    require(oid in failed and source.at(relative_path(encrypted.get("source_path"))) == oid, "Unbound encrypted fixture")
    content = source.blob(oid)
    require(digest(content) == encrypted.get("source_sha256"), "Encrypted fixture hash mismatch")
    complete_7z(content)
    exits = encrypted.get("list_test_extract_exit_codes")
    require(integer(encrypted.get("declared_file_count")) and encrypted["declared_file_count"] == 1
            and integer(encrypted.get("declared_uncompressed_bytes")) and encrypted["declared_uncompressed_bytes"] == 5
            and encrypted.get("decoded_payload_sha256") == digest(b"alpha")
            and encrypted.get("decoded_payload_matches_reviewed_generator_literal") is True
            and encrypted.get("physical_size_matches_entire_blob") is True
            and isinstance(exits, list) and len(exits) == 3 and all(type(code) is int and code == 0 for code in exits),
            "Incomplete encrypted fixture attestation")
    decoder_name = relative_path(encrypted.get("decoder_receipt"))
    decoder_receipt = object_json(dispositions, decoder_name)
    require(decoder_receipt.get("version") == "26.03" and DIGEST.fullmatch(decoder_receipt.get("binary_sha256", ""))
            and DIGEST.fullmatch(decoder_receipt.get("expected_archive_sha256", ""))
            and decoder_receipt["expected_archive_sha256"] == decoder_receipt.get("observed_archive_sha256"), "Invalid decoder integrity receipt")
    decoder_directory = child(dispositions, decoder_name).parent
    require(any(path.is_file() and not path.is_symlink() and path.stat().st_size <= 32 * MAX_ARCHIVE_BYTES
                and digest(regular_file(path, 32 * MAX_ARCHIVE_BYTES)) == decoder_receipt["binary_sha256"]
                for path in decoder_directory.iterdir()), "Retained decoder binary hash mismatch")
    outputs = replay(seeds)
    reconstructed = 0
    for object_id, record in records.items():
        require(source.at(relative_path(record.get("source_path"))) == object_id, "Provenance path differs from qualified tree")
        raw = source.blob(object_id)
        require(integer(record.get("bytes")) and record["bytes"] == len(raw) and record.get("sha256") == digest(raw), "Raw provenance hash mismatch")
        stored = failed[object_id].get("stored_name")
        require(isinstance(stored, str) and stored.startswith(object_id + "."), "Unbound stored archive name")
        require(regular_file(child(evidence / "archive-blobs", stored), MAX_ARCHIVE_BYTES) == raw, "Retained raw archive differs from Git")
        if object_id == oid:
            require(record.get("replayed") is False, "Encrypted fixture cannot claim a pure replay")
            continue
        name = PurePosixPath(record["source_path"]).name
        require(name in outputs and outputs[name] == raw, "Pinned deterministic reconstruction differs from raw archive")
        require(record.get("replayed") is True and record.get("replay_matches") is True
                and record.get("replay") == {"object": object_id, "sha256": digest(raw), "bytes": len(raw)}, "Replay attestation does not bind its actual output")
        reconstructed += 1
    require(reconstructed == 19, "Missing or extra deterministic reconstructions")
    retained = retained_payloads(dispositions, decoded)
    return proof, compressed, encrypted, retained


def review(args):
    os.umask(0o077)
    evidence = audit.create_evidence(args.evidence)
    args.created_evidence = True
    original = private_root(args.audit_evidence)
    dispositions = private_root(args.dispositions)
    source = Source(args.source, args.revision)
    archives, failed, results = validate_inventory(source, original)
    proof, seeds, encrypted, payloads = validate_provenance(source, original, dispositions, failed)
    run = object_json(original, "run.json")
    pin = install_scanner.scanner_pin()
    scanner = args.gitleaks.resolve()
    require(not args.gitleaks.is_symlink(), "Scanner symlinks are not accepted")
    binary_hash = digest(regular_file(scanner, 64 * MAX_ARCHIVE_BYTES))
    receipt = object_json(scanner.parent, "receipt.json")
    require(receipt.get("archive_file") == install_scanner.ARCHIVE_NAME,
            "Retained pinned scanner archive is missing; reinstall with install_scanner.py")
    package_path = child(scanner.parent, install_scanner.ARCHIVE_NAME)
    require(package_path.exists(), "Retained pinned scanner archive is missing; reinstall with install_scanner.py")
    package = regular_file(package_path, install_scanner.MAX_ARCHIVE_BYTES)
    require(digest(package) in pin["archives"].values() and receipt.get("archive_sha256") == digest(package),
            "Actual scanner archive differs from checked-in pin")
    require(install_scanner.scanner_binary(package) == regular_file(scanner, install_scanner.MAX_BINARY_BYTES),
            "Scanner executable differs from pinned archive member")
    require(receipt.get("version") == pin["version"] == run.get("scanner_version")
            and receipt.get("binary_sha256") == binary_hash == run.get("scanner_sha256")
            and receipt.get("archive_sha256") in pin["archives"].values(), "Scanner pin or prior audit binding mismatch")
    require(audit.run([str(scanner), "version"]).stdout.decode().strip() == pin["version"], "Scanner version mismatch")
    config = source.blob(source.at(".gitleaks.toml"))
    require(digest(config) == run.get("scanner_config_sha256") == digest((audit.ROOT / ".gitleaks.toml").read_bytes()),
            "Scanner rules differ from the original audit or trusted command")
    config_path = evidence / "scanner.toml"
    config_path.write_bytes(config)
    targets = evidence / "independent-inputs"
    targets.mkdir(mode=0o700)
    raw_objects = set(failed) | set(seeds)
    for oid in sorted(raw_objects):
        (targets / ("raw-" + oid)).write_bytes(source.blob(oid))
    for checksum, content in payloads.items():
        (targets / ("decoded-" + checksum)).write_bytes(content)
    ignore = evidence / "empty.gitleaksignore"
    ignore.touch()
    report = evidence / "independent-secrets.json"
    command = [str(scanner), "dir", "--config", str(config_path), "--gitleaks-ignore-path", str(ignore),
               "--ignore-gitleaks-allow", "--redact=100", "--no-banner", "--no-color", "--max-decode-depth", "5",
               "--max-archive-depth", "0", "--report-format", "json", "--report-path", str(report), str(targets)]
    scanned = audit.run(command, log=evidence / "independent-scan.log", allowed=None)
    status = audit.scanner_result(scanned, report)
    audit.write_json(evidence / "independent-scan-result.json", status)
    audit.write_json(evidence / "independent-scan-command.json", command)
    technical = status["scan_completed"] and status["exit_code"] == 0 and status["findings"] == 0
    input_hashes = {}
    for root, names in [(original, ["run.json", "archives-review.json", "archive-scan-results.json", "historical-blobs.json"]),
                        (dispositions, ["archive-provenance.json", "compressed-seed-disposition.json", "encrypted-fixture-disposition.json", encrypted["decoder_receipt"]])]:
        for name in names:
            input_hashes[str(root / name)] = digest(regular_file(child(root, name)))
    for path in (original / "archive-scans").iterdir():
        if path.suffix in (".json", ".log"):
            input_hashes[str(path)] = digest(regular_file(child(original, str(path.relative_to(original)))))
    audit.write_json(evidence / "input-receipt-hashes.json", input_hashes)
    audit.write_json(evidence / "original-archive-scan-results.json", list(results.values()))
    audit.write_json(evidence / "verified-archive-bindings.json", [
        {"object": oid, "sha256": digest(source.blob(oid)), "bytes": len(source.blob(oid)),
         "paths": sorted(source.objects[oid]), "deterministic_reconstruction": oid != encrypted["source_object"]}
        for oid in sorted(failed)])
    summary = {"schema_version": 1, "qualified_commit": source.commit, "qualified_tree": source.tree,
               "scope": "Unchanged archive inventory and the twenty bound decoder gaps; not a fresh whole-tree secret scan.",
               "archive_inventory_objects": len(archives), "archive_gap_objects": len(failed),
               "raw_git_and_retained_archive_integrity_verified": True, "pinned_generator_object": GENERATOR_OBJECT,
               "pinned_generator_sha256": GENERATOR_SHA256, "deterministic_reconstructions_verified": 19,
               "compressed_seed_sources_verified": 2, "decoded_payload_records_verified": 6,
               "unique_retained_decoded_payloads_verified": len(payloads), "decoded_record_bytes": 160020,
               "independent_raw_blob_targets": len(raw_objects), "independent_decoded_targets": len(payloads),
               "independent_archive_recursion": False, "new_decoder_execution_or_extraction": False,
               "independent_scan_completed": status["scan_completed"], "independent_findings": status["findings"],
               "independent_technical_checks_passed": technical,
               "review_command_sha256": digest(Path(__file__).read_bytes()),
               "scanner_installer_sha256": digest(Path(install_scanner.__file__).read_bytes()),
               "audit_helpers_sha256": digest(Path(audit.__file__).read_bytes()),
               "scanner_pin_sha256": digest(Path(__file__).with_name("scanner.json").read_bytes()),
               "python_version": platform.python_version(),
               "scanner_archive_sha256": digest(package), "scanner_binary_sha256": binary_hash,
               "scanner_rules_sha256": digest(config),
               "decoded_completeness_basis": "External reviewer-attested decoder lists/tests/extractions; integrity is verified, authority and completeness are not inferred from hashes.",
               "reviewer_attestation_required": True, "technical_manual_coverage_complete": False,
               "approval_needs": ["Review the exact compressed-seed and encrypted-fixture decoder completeness attestations and this receipt.",
                                  "Bind any external approval to this qualified tree and the input receipt hashes."],
               "original_archive_scanner_completed": False, "original_archive_scanner_gaps": len(failed),
               "original_scanner_reports_modified": False, "scan_completed": False, "scan_passed": False,
               "publication_signoff": False}
    audit.write_json(evidence / "summary.json", summary)
    print(json.dumps(summary, indent=2))
    return 0 if technical else (1 if status["scan_completed"] else 2)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--audit-evidence", type=Path, required=True)
    parser.add_argument("--dispositions", type=Path, required=True)
    parser.add_argument("--gitleaks", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    try:
        return review(args)
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError, KeyError, TypeError, struct.error, tarfile.TarError) as error:
        if getattr(args, "created_evidence", False):
            (args.evidence / "failure.txt").write_text(f"{type(error).__name__}: {error}\n")
        print("Archive review could not complete; inspect private evidence.")
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
