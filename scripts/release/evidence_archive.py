#!/usr/bin/env python3
"""Transport complete public verification evidence without changing its signatures.

The archive checksum and member hashes detect corruption. Authentication still
requires the existing metadata/provenance verifier with an independently chosen
source revision, tag and trusted signer workflow.
"""
import argparse
import gzip
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import tarfile

from preflight import valid_tag
from suite import REPORTS


MAX_FILE = 64 * 1024 * 1024
MAX_TOTAL = 256 * 1024 * 1024
MAX_FILES = 1024
MAX_MANIFEST = 256 * 1024
MAX_TAR = MAX_TOTAL + MAX_MANIFEST + (MAX_FILES + 1) * 1024 + tarfile.RECORDSIZE + 1
MANIFEST = 'manifest.json'
FIXED = {'attestations.jsonl', 'publisher/images.env', 'publisher/candidate.json',
         'metadata/release-metadata.json', 'packaged/suite.json',
         *('packaged/' + name for name in REPORTS)}


def archive_name(tag):
    if not valid_tag(tag):
        raise ValueError('Invalid verification archive version')
    return f'romd-{tag}-verification.tar.gz'


def safe_member(name):
    if not isinstance(name, str):
        raise ValueError('Unsafe verification archive member')
    path = PurePosixPath(name)
    if (len(name) > 240 or path.is_absolute()
            or str(path) != name or any(part in ('.', '..') for part in path.parts)
            or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._/-]*', name)):
        raise ValueError('Unsafe verification archive member')
    return name


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def regular_bytes(path, limit=MAX_FILE):
    if path.is_symlink() or not path.is_file() or not 0 < path.stat().st_size <= limit:
        raise ValueError('Verification input must be a bounded regular file')
    with path.open('rb') as stream:
        data = stream.read(limit + 1)
    if not 0 < len(data) <= limit:
        raise ValueError('Verification input exceeded its size bound')
    return data


def source_bytes(root, name):
    safe_member(name)
    path = root / name
    if (root.is_symlink() or not path.resolve().is_relative_to(root.resolve())
            or any(parent.is_symlink() for parent in path.parents if parent != root.parent)):
        raise ValueError('Verification input may not traverse a symbolic link')
    return regular_bytes(path)


def identity(revision, tag):
    if not re.fullmatch('[0-9a-f]{40}', revision) or not valid_tag(tag):
        raise ValueError('Verification archive requires a full source commit and strict version')


def validate_payload(files, revision, tag):
    """Check transport inventory/bindings; full runtime and crypto gates stay separate."""
    identity(revision, tag)
    if not FIXED <= files.keys():
        raise ValueError('Verification archive is missing required evidence')
    index = json.loads(files['metadata/release-metadata.json'])
    suite = json.loads(files['packaged/suite.json'])
    state = json.loads(files['publisher/candidate.json'])
    if not all(isinstance(item, dict) for item in (index, suite, state)):
        raise ValueError('Invalid verification evidence report')
    for report in (index, suite):
        if (report.get('schemaVersion') != 1 or report.get('result') != 'passed'
                or report.get('evidenceType') != 'disposable-release'
                or report.get('sourceCommit') != revision or report.get('releaseTag') != tag):
            raise ValueError('Verification evidence result/source/version/type mismatch')
    inventory = index.get('files')
    if not isinstance(inventory, dict) or not inventory:
        raise ValueError('Verification metadata inventory is missing')
    expected = FIXED | {'metadata/' + safe_member(name) for name in inventory}
    if set(files) != expected:
        raise ValueError('Verification archive has missing or unexpected files')
    for name, digest in inventory.items():
        if '/' in name or not re.fullmatch('[0-9a-f]{64}', str(digest)):
            raise ValueError('Invalid verification metadata file identity')
        if sha256(files['metadata/' + name]) != digest:
            raise ValueError('Verification metadata file checksum mismatch')
    if set(suite.get('files', {})) != set(REPORTS):
        raise ValueError('Verification runtime report inventory mismatch')
    for name, digest in suite['files'].items():
        if sha256(files['packaged/' + name]) != digest:
            raise ValueError('Verification runtime report checksum mismatch')
    # Retain the original role/digest map. Runtime qualification still uses the
    # independently retained publisher artifact, not these public copies.
    maps = {}
    for line in files['publisher/images.env'].decode().splitlines():
        key, value = line.split('=', 1)
        if key in maps:
            raise ValueError('Duplicate publisher image role')
        maps[key] = value
    subjects = index.get('subjects', [])
    if not isinstance(subjects, list) or len(subjects) != 6 or not all(isinstance(item, dict) for item in subjects):
        raise ValueError('Invalid verification subject inventory')
    images = {f'ROMD_{item["role"].upper()}_IMAGE': item['name']
              for item in subjects if item.get('kind') == 'image'}
    bundles = [item for item in subjects if item.get('kind') == 'bundle']
    if (set(maps) != {'ROMD_' + role + '_IMAGE' for role in ('ADMIN', 'CONSUMER', 'WORKER', 'PLAYER', 'POSTGRES')}
            or any(not re.fullmatch(r'.+@sha256:[0-9a-f]{64}', value) for value in maps.values())
            or maps != images or maps != state.get('images') or maps != suite.get('images')
            or state.get('sourceCommit') != revision or state.get('releaseTag') != tag
            or len(bundles) != 1 or state.get('bundleSha256') != bundles[0].get('sha256')
            or suite.get('bundleSha256') != state.get('bundleSha256')):
        raise ValueError('Verification publisher/security/runtime bindings mismatch')


def create_archive(metadata, attestations, images, state, packaged, output, revision, tag):
    identity(revision, tag)
    if output.name != archive_name(tag):
        raise ValueError('Verification archive filename differs from its version')
    checksum = Path(str(output) + '.sha256')
    if output.exists() or checksum.exists() or output.is_symlink() or checksum.is_symlink():
        raise ValueError('Verification archive output already exists')
    index = json.loads(source_bytes(metadata, 'release-metadata.json'))
    if not isinstance(index, dict):
        raise ValueError('Invalid verification metadata index')
    inventory = index.get('files')
    if not isinstance(inventory, dict) or not 0 < len(inventory) < MAX_FILES - len(FIXED):
        raise ValueError('Invalid verification metadata inventory')
    files = {'metadata/release-metadata.json': source_bytes(metadata, 'release-metadata.json'),
             'attestations.jsonl': regular_bytes(attestations),
             'publisher/images.env': regular_bytes(images),
             'publisher/candidate.json': regular_bytes(state),
             'packaged/suite.json': source_bytes(packaged, 'suite.json')}
    for name in inventory:
        if safe_member(name) != Path(name).name or name.casefold() == 'release-metadata.json':
            raise ValueError('Unsafe or reserved metadata filename')
        files['metadata/' + name] = source_bytes(metadata, name)
    for name in REPORTS:
        files['packaged/' + name] = source_bytes(packaged, name)
    validate_payload(files, revision, tag)
    if (len({name.casefold() for name in files}) != len(files)
            or sum(map(len, files.values())) > MAX_TOTAL):
        raise ValueError('Duplicate or oversized verification evidence')
    manifest = dict(schemaVersion=1, evidenceType='public-release-verification',
                    sourceCommit=revision, releaseTag=tag,
                    files={name: dict(size=len(data), sha256=sha256(data)) for name, data in sorted(files.items())})
    manifest_bytes = (json.dumps(manifest, sort_keys=True, indent=2) + '\n').encode()
    with output.open('xb') as raw, gzip.GzipFile(fileobj=raw, mode='wb', filename='', mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode='w', format=tarfile.USTAR_FORMAT) as archive:
            for name, data in [(MANIFEST, manifest_bytes), *sorted(files.items())]:
                member = tarfile.TarInfo(name)
                member.size, member.mode = len(data), 0o644
                archive.addfile(member, io.BytesIO(data))
    with output.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    with checksum.open('x') as stream:
        stream.write(f'{digest}  {output.name}\n')
    return manifest


def regular_header(read):
    """Inspect raw fixed-size headers before any extension processor sees them."""
    header = read(tarfile.BLOCKSIZE)
    if header == b'\0' * tarfile.BLOCKSIZE:
        trailer = read(tarfile.RECORDSIZE + 1)
        if not tarfile.BLOCKSIZE <= len(trailer) <= tarfile.RECORDSIZE or any(trailer):
            raise ValueError('Invalid or oversized verification archive trailer')
        return None
    # tarfile.open/next processes PAX/GNU bodies before returning a member.
    # This closed transport emits only regular USTAR records, so refuse every
    # extension/link/special type before its declared body can be read.
    if len(header) != tarfile.BLOCKSIZE or header[156:157] not in (tarfile.REGTYPE, tarfile.AREGTYPE):
        raise ValueError('Unsupported verification archive extension or member type')
    member = tarfile.TarInfo.frombuf(header, 'utf-8', 'strict')
    if not member.isfile() or member.linkname:
        raise ValueError('Unsupported verification archive member type or alias')
    return member


def regular_body(read, member, limit):
    if not 0 < member.size <= limit:
        raise ValueError('Oversized verification archive member')
    data = read(member.size)
    padding_size = -member.size % tarfile.BLOCKSIZE
    padding = read(padding_size)
    if len(data) != member.size or len(padding) != padding_size or any(padding):
        raise ValueError('Truncated verification archive member or invalid padding')
    return data


def read_archive(archive, checksum, revision, tag):
    identity(revision, tag)
    if (archive.name != archive_name(tag) or archive.is_symlink() or not archive.is_file()
            or not 0 < archive.stat().st_size <= MAX_TOTAL + 1024 * 1024):
        raise ValueError('Invalid verification archive file')
    fields = regular_bytes(checksum, 512).decode().strip().split()
    with archive.open('rb') as stream:
        actual = hashlib.file_digest(stream, 'sha256').hexdigest()
    if len(fields) != 2 or fields != [actual, archive.name]:
        raise ValueError('Verification archive checksum mismatch')
    with gzip.open(archive, 'rb') as compressed:
        remaining = MAX_TAR
        def read(count):
            nonlocal remaining
            if not 0 <= count <= remaining:
                raise ValueError('Verification archive decompressed budget exceeded')
            data = compressed.read(count)
            remaining -= len(data)
            return data
        first = regular_header(read)
        if first is None or first.name != MANIFEST:
            raise ValueError('Verification archive manifest is missing or oversized')
        manifest = json.loads(regular_body(read, first, MAX_MANIFEST))
        if not isinstance(manifest, dict):
            raise ValueError('Invalid verification archive manifest')
        inventory = manifest.get('files')
        if (manifest.get('schemaVersion') != 1 or manifest.get('evidenceType') != 'public-release-verification'
                or manifest.get('sourceCommit') != revision or manifest.get('releaseTag') != tag
                or not isinstance(inventory, dict) or not 0 < len(inventory) <= MAX_FILES):
            raise ValueError('Verification archive identity or inventory mismatch')
        for name, record in inventory.items():
            safe_member(name)
            if (name == MANIFEST or not isinstance(record, dict)
                    or type(record.get('size')) is not int or not 0 < record['size'] <= MAX_FILE
                    or not re.fullmatch('[0-9a-f]{64}', str(record.get('sha256')))):
                raise ValueError('Invalid verification archive member inventory')
        if (len({name.casefold() for name in inventory}) != len(inventory)
                or sum(record['size'] for record in inventory.values()) > MAX_TOTAL):
            raise ValueError('Duplicate or oversized verification archive inventory')
        files = {}
        # No streaming tar parser or extract/extractall. The verified inventory
        # bounds every body, member count, header, padding and decompressed byte.
        while (member := regular_header(read)) is not None:
            name = safe_member(member.name)
            if (name not in inventory or name in files or not member.isfile() or member.issparse()
                    or member.size != inventory[name]['size']):
                raise ValueError('Unexpected verification archive member/type/size')
            data = regular_body(read, member, MAX_FILE)
            if len(data) != member.size or sha256(data) != inventory[name]['sha256']:
                raise ValueError('Verification archive member checksum mismatch')
            files[name] = data
    if set(files) != set(inventory):
        raise ValueError('Verification archive members are incomplete')
    validate_payload(files, revision, tag)
    return manifest, files


def unpack_archive(archive, checksum, output, revision, tag):
    manifest, files = read_archive(archive, checksum, revision, tag)
    output.mkdir(parents=True, exist_ok=False, mode=0o700)
    for name, data in files.items():
        path = output / name
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open('xb') as stream:
            stream.write(data)
    (output / MANIFEST).write_text(json.dumps(manifest, sort_keys=True, indent=2) + '\n')
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    create = commands.add_parser('create')
    for name in ('metadata-dir', 'attestations', 'images', 'publisher-state', 'packaged-evidence', 'output'):
        create.add_argument('--' + name, type=Path, required=True)
    unpack = commands.add_parser('unpack')
    for name in ('archive', 'checksum', 'output'):
        unpack.add_argument('--' + name, type=Path, required=True)
    for command in (create, unpack):
        command.add_argument('--revision', required=True)
        command.add_argument('--tag', required=True)
    args = parser.parse_args()
    try:
        if args.command == 'create':
            create_archive(args.metadata_dir, args.attestations, args.images, args.publisher_state,
                           args.packaged_evidence, args.output, args.revision, args.tag)
        else:
            unpack_archive(args.archive, args.checksum, args.output, args.revision, args.tag)
    except (ValueError, OSError, KeyError, TypeError, tarfile.TarError):
        parser.exit(1, 'Verification archive failed validation; existing outputs were not replaced\n')
    print('Verification archive: ' + ('created' if args.command == 'create' else 'unpacked; cryptographic verification required'))


if __name__ == '__main__':
    main()
