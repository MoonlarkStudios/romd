#!/usr/bin/env python3
"""Install exact official scanner archives; never execute a downloaded install script."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import tarfile
import urllib.request

PINS = Path(__file__).with_name('tools.json')


def binary_from_archive(archive, name, expected):
    data = Path(archive).read_bytes()
    if hashlib.sha256(data).hexdigest() != expected:
        raise ValueError('Scanner archive checksum mismatch')
    with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as bundle:
        matches = [member for member in bundle if member.name == name]
        if len(matches) != 1 or not matches[0].isfile() or matches[0].size > 300 * 1024 * 1024:
            raise ValueError('Scanner archive must contain one regular executable')
        executable = bundle.extractfile(matches[0]).read()
    if executable[:4] != b'\x7fELF' or executable[18:20] != b'\x3e\x00':
        raise ValueError('Scanner executable is not Linux amd64 ELF')
    return executable


def verify_installed(directory):
    pins = json.loads(PINS.read_text())
    for name in ('syft', 'trivy'):
        binary = binary_from_archive(directory / f'{name}.tar.gz', name, pins[name]['sha256'])
        if (directory / name).read_bytes() != binary:
            raise ValueError('Installed scanner differs from the pinned archive')
    return pins


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True, help='New directory, outside public release metadata')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False, mode=0o700)
    pins = json.loads(PINS.read_text())
    for name in ('syft', 'trivy'):
        pin = pins[name]
        with urllib.request.urlopen(pin['url'], timeout=120) as source:
            data = source.read(300 * 1024 * 1024 + 1)
        if len(data) > 300 * 1024 * 1024:
            raise ValueError('Scanner archive exceeds download limit')
        archive = args.output / f'{name}.tar.gz'
        archive.write_bytes(data)
        binary = binary_from_archive(archive, name, pin['sha256'])
        (args.output / name).write_bytes(binary)
        (args.output / name).chmod(0o755)
    verify_installed(args.output)
    (args.output / 'pins.json').write_text(json.dumps(pins, indent=2) + '\n')
    print('Verified pinned Syft and Trivy Linux amd64 executables')


if __name__ == '__main__':
    main()
