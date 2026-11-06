#!/usr/bin/env python3
"""Install only the checksummed pinned scanner into a caller-owned directory."""

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import tarfile
import urllib.request


ARCHIVE_NAME = "scanner.tar.gz"
MAX_ARCHIVE_BYTES = 32 * 1024 * 1024
MAX_BINARY_BYTES = 64 * 1024 * 1024


def scanner_pin():
    return json.loads(Path(__file__).with_name("scanner.json").read_text())


def scanner_binary(archive):
    """Read one bounded regular member in memory; never extract upstream paths or links."""
    with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as package:
        members = []
        total_bytes = 0
        for index, member in enumerate(package):
            if index >= 128:
                raise ValueError("Scanner archive member limit exceeded")
            total_bytes += member.size
            if total_bytes > MAX_BINARY_BYTES:
                raise ValueError("Scanner archive exceeds reviewed uncompressed size bound")
            if member.name == "gitleaks":
                if not member.isfile():
                    raise ValueError("Scanner member must be a regular file")
                members.append(member)
        if len(members) != 1 or not members[0].isfile():
            raise ValueError("Scanner archive requires one regular gitleaks member")
        member = members[0]
        if member.size > MAX_BINARY_BYTES:
            raise ValueError("Scanner member exceeds reviewed size bound")
        binary = package.extractfile(member).read(MAX_BINARY_BYTES + 1)
        if len(binary) != member.size:
            raise ValueError("Incomplete scanner archive member")
        return binary


def install(destination: Path) -> Path:
    os.umask(0o077)
    pin = scanner_pin()
    machine = {"x86_64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(platform.machine())
    target = f"{platform.system().lower()}_{machine}"
    if target not in pin["archives"]:
        raise ValueError(f"No reviewed scanner archive for {target}")
    destination.mkdir(parents=True, exist_ok=False, mode=0o700)
    name = f"gitleaks_{pin['version']}_{target}.tar.gz"
    url = f"https://github.com/gitleaks/gitleaks/releases/download/v{pin['version']}/{name}"
    with urllib.request.urlopen(url, timeout=60) as response:
        archive = response.read(MAX_ARCHIVE_BYTES + 1)
    digest = hashlib.sha256(archive).hexdigest()
    if digest != pin["archives"][target]:
        raise ValueError("Scanner archive checksum differs from reviewed pin")
    binary = scanner_binary(archive)
    (destination / ARCHIVE_NAME).write_bytes(archive)
    executable = destination / "gitleaks"
    executable.write_bytes(binary)
    executable.chmod(0o700)
    (destination / "receipt.json").write_text(json.dumps({
        "url": url, "archive_sha256": digest,
        "archive_file": ARCHIVE_NAME,
        "binary_sha256": hashlib.sha256(binary).hexdigest(), "version": pin["version"],
    }, indent=2) + "\n")
    return executable


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path, help="new private directory outside the repository")
    args = parser.parse_args()
    print(install(args.destination.resolve()))
