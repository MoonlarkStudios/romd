#!/usr/bin/env python3
"""Disposable PS2 DVD CHD end-to-end harness through the normal ROMD hosts.

Real mode (`mise run e2e:chd`) uploads the owner's fixture discs one at a time; it needs
ROMD_FIXTURES and ROMD_E2E_ROOT and fails closed without them. Synthetic mode
(`mise run e2e:chd:synthetic`) generates every input. Both run PostgreSQL and the stock admin,
consumer and worker images behind the HTTPS ingress, activate CHD through a generated overlay in
the run folder, kill the worker at the selected convergence checkpoint and destroy only
the Compose project and run folder they created. Never reads .env. See scripts/e2e/README.md.
"""
from __future__ import annotations

import argparse
import base64
import contextlib
import dataclasses
import datetime as dt
import hashlib
import html
import ipaddress
import itertools
import json
import os
from pathlib import Path, PurePosixPath
import platform
import queue
import random
import re
import secrets
import shutil
import signal
import struct
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from urllib.parse import parse_qs, urlencode, urlsplit
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape, quoteattr
import zipfile
import zlib

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts' / 'deployment'))
from test_contract import test_environment  # noqa: E402  (shared disposable-stack secrets and hostnames)

CHECKPOINT = 'publishing'
CHECKPOINT_DESCRIPTION = ('convergence job phase Publishing, not terminal, outcome null, a growing file under '
                          'data/content/.staging/ and no run-* records in data/temp/media-runs-v1/')
ENCODING_DESCRIPTION = ('convergence job phase Encoding, a permitted encode-chd child of namespace PID 1, '
                        'a live chdman and a growing reserved output, followed by worker container recreation')


def decoder_identity(manifest, architecture, upstream, recipe_version):
    """Bind a qualification receipt to the decoder checked by each host at startup.

    Startup owns native binary/manifest verification. This checks the recorded
    identity against the source being qualified; it does not grant attestation.
    """
    rid = {'amd64': 'linux-x64', 'arm64': 'linux-arm64'}.get(architecture)
    require(rid is not None, 'unsupported CHD image architecture')
    require(isinstance(manifest, dict) and manifest.get('schemaVersion') == 3
            and manifest.get('product') == 'moonlark_chdr', 'invalid native build record')
    require(manifest.get('rid') == rid and manifest.get('upstreamCommit') == upstream
            and type(manifest.get('recipeVersion')) is int and manifest['recipeVersion'] == recipe_version,
            'image decoder tuple differs from the current source or image architecture')
    build_info = manifest.get('buildInfo')
    require(isinstance(build_info, dict), 'native build record lacks build information')
    build_id = build_info.get('buildId')
    digest = manifest.get('sha256')
    require(all(isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value) for value in (build_id, digest)),
            'native build record lacks a binary digest or build identity')
    return {'rid': rid, 'upstreamCommit': upstream, 'recipeVersion': recipe_version,
            'binarySha256': digest, 'buildId': build_id}


def current_decoder_source():
    pin = json.loads((ROOT / 'native/libchdr/libchdr.json').read_text())['commit']
    policy = (ROOT / 'tools/Moonlark.Native.Engineering/Native/NativePolicy.cs').read_text()
    version = re.search(r'internal const int BuildRecipeVersion = ([1-9][0-9]*);', policy)
    require(version is not None, 'cannot read the current native build recipe version')
    return pin, int(version[1])


def checkpoint_description(name):
    return ENCODING_DESCRIPTION if name == 'encoding' else CHECKPOINT_DESCRIPTION


ADMIN_HOST, CONSUMER_HOST = 'admin.example.test', 'console.example.test'
CLIENT_IDS = {ADMIN_HOST: 'romd-admin-spa', CONSUMER_HOST: 'romd-consumer-spa'}
DATA_TARGET = '/var/lib/romd'
LIBCHDR_KEY, LIBCHDR_DIRECTORY = 'Romd__Native__LibchdrDirectory', '/app/native/libchdr'
CHDMAN_KEY, CHDMAN_DIRECTORY = 'Romd__Native__ChdmanDirectory', '/app/native/chdman'
HOSTS = ('romd-admin', 'romd-consumer', 'romd-worker')
READING_HOSTS = ('romd-admin', 'romd-consumer')
IMAGE_SERVICES = ('worker', 'admin', 'consumer', 'player')
GIB = 1 << 30
# Binary units throughout: an assumed 30 GiB peak until a passed run measured one, plus 10 GiB of headroom.
DEFAULT_PEAK_BYTES, PEAK_HEADROOM_BYTES = 30 * GIB, 10 * GIB
RECIPE_SOURCE = ROOT / 'src/Romd.Media/Chd/ChdDvdCanonicalRecipe.cs'
# Artifacts of the 2026-10-13 real-disc recipe measurements (DVD-5 and DVD-9 PS2 discs; see docs/self-hosting/media-processing.md).
RECIPE_MEASUREMENTS = {'chd-dvd-canonical/v2': {
    'dvd5': ('90e8f71573edaf2bb3dd6b305afeb1d20622b5641ba226c6be57ab3d32726edf', 4_113_594_919),
    'dvd9': ('10915cee5a07de66c215ed107e3ab3dd4616e676048954aa1da90c08c3177363', 4_657_418_312)}}
RUN_ID = re.compile(r'chd-(?:ffx|gt4|synthetic|ps1)-\d{8}t\d{6}-[0-9a-f]{6}')
TEMPORARY_ROOTS = ('/tmp', '/private/tmp', '/var/tmp', '/private/var/tmp', '/var/folders', '/private/var/folders')
# Outside Docker's default 172.17-31/192.168 pools, so the unpinned backend network cannot claim it first; not k3s 10.42/10.43.
SUBNET_POOL = ipaddress.ip_network('10.213.0.0/16')
POISON_NAME, SIBLING_NAME, MALFORMED_ARCHIVE = 'romd-e2e-poison.chd', 'romd-e2e-sibling.bin', 'romd-e2e-malformed.zip'
# MediaInspectionErrors.Failed(MediaWorkerFailure.Malformed) in src/Romd.Infrastructure/Storage/RvzVerifier.cs. The
# poison's random map makes libchdr refuse the open (CHDERR_INVALID_FILE); ChdInspectionService reports Malformed.
MALFORMED_MEDIA_ERROR = 'The media is malformed or its complete output could not be verified.'
SYNTHETIC_REDUMP_ID, SYNTHETIC_SEED = 4242, 20261014
SYNTHETIC_GAME, SYNTHETIC_ISO = 'ROMD E2E Synthetic PS2 (USA)', 'romd-e2e-synthetic-ps2.iso'
INGRESS_DEVIATION = ('deploy/examples/https + generated template with server-level proxy_buffering off; '
                     'committed VFS streaming is preserved, but normal-route response buffering is not exercised')
WORKER_READ_LIMITATION = ('The worker reads stored CHDs only for export jobs; no export runs here, so worker '
                          'serving admission is not exercised.')
IDENTITY_KEYS = ('size', 'sha256', 'sha1', 'md5', 'crc32')
HEX_LENGTHS = {'sha256': 64, 'sha1': 40, 'md5': 32, 'crc32': 8}


@dataclasses.dataclass(frozen=True)
class Disc:
    key: str
    role: str
    redump_id: int
    media: str


DISCS = {'ffx': Disc('ffx', 'dvd5', 210, 'Dvd5'), 'gt4': Disc('gt4', 'dvd9', 955, 'Dvd9')}


class HarnessError(RuntimeError):
    """A failed precondition or assertion. Messages never contain secrets or fixture bytes."""


class CheckpointMissed(HarnessError):
    """The named checkpoint was not held when the worker was killed."""


def require(condition, message):
    if not condition:
        raise HarnessError(message)


def utc_now():
    return dt.datetime.now(dt.timezone.utc)


def iso(moment):
    return moment.isoformat(timespec='milliseconds').replace('+00:00', 'Z')


def tail(text, limit=2000):
    text = text.decode('utf-8', 'replace') if isinstance(text, bytes) else (text or '')
    return text[-limit:].strip()


# ---------------------------------------------------------------- paths and fail-closed configuration

def within(path, parent):
    return path == parent or parent in path.parents


def canonical_directory(environ, name):
    raw = environ.get(name, '')
    require(raw.strip(), f'{name} is unset; export the absolute directory explicitly (see scripts/e2e/README.md).')
    require(not any(character in raw for character in '\0\r\n,:'),
            f'{name} must not contain control characters, commas or colons (Docker mount syntax).')
    path = Path(raw)
    require(path.is_absolute(), f'{name} must be an absolute path, not {raw!r}.')
    require(path.is_dir(), f'{name} does not name an existing directory: {raw}')
    resolved = path.resolve(strict=True)
    require(resolved == path, f'{name} must be its resolved path {resolved}; Docker bind sources need resolved host paths.')
    return resolved


def default_temporary_roots():
    return tuple(Path(root) for root in TEMPORARY_ROOTS) + (Path(tempfile.gettempdir()).resolve(),)


def real_paths(environ, *, repository=ROOT, temporary_roots=None):
    """Real-disc runs never write under scratch roots, inside the checkout, or beside the fixtures."""
    temporary_roots = default_temporary_roots() if temporary_roots is None else temporary_roots
    fixtures = canonical_directory(environ, 'ROMD_FIXTURES')
    root = canonical_directory(environ, 'ROMD_E2E_ROOT')
    for name, path in (('ROMD_FIXTURES', fixtures), ('ROMD_E2E_ROOT', root)):
        require(not any(within(path, temporary) for temporary in temporary_roots),
                f'{name} must not be under a temporary directory ({path}); real runs need durable host space.')
        require(not within(path, repository) and not within(repository, path),
                f'{name} must be outside the repository checkout ({path}).')
    require(not within(fixtures, root) and not within(root, fixtures),
            'ROMD_FIXTURES and ROMD_E2E_ROOT must not contain each other; cleanup deletes run folders.')
    require((fixtures / 'manifest.json').is_file(), f'{fixtures} has no manifest.json.')
    return fixtures, root


def new_run_id(label, now=None, token=None):
    now = now or utc_now()
    run_id = f'chd-{label}-{now:%Y%m%dt%H%M%S}-{token or secrets.token_hex(3)}'
    require(RUN_ID.fullmatch(run_id), f'invalid run id {run_id!r}')
    return run_id


def run_folder(root, run_id):
    require(RUN_ID.fullmatch(run_id), f'invalid run id {run_id!r}')
    return root / 'runs' / run_id


def runs_directory(root):
    """ROOT/runs as a real directory: neither it nor an ancestor below ROOT may be a symlink."""
    runs = root / 'runs'
    require(root.resolve() == root, f'{root} must be its resolved path')
    require(not runs.is_symlink() and (not runs.exists() or runs.is_dir()), f'{runs} must be a real directory, not a symlink')
    runs.mkdir(exist_ok=True)
    require(runs.resolve() == runs, f'{runs} resolves elsewhere ({runs.resolve()})')
    return runs


def require_disposable(root, folder):
    """Only a run-id-named, non-symlink directory directly under a real ROOT/runs may ever be deleted."""
    runs = root / 'runs'
    require(not runs.is_symlink() and runs.resolve() == runs, f'refusing to delete under {runs}: it is a symlink')
    resolved = folder.resolve()
    require(not folder.is_symlink() and resolved.parent == runs and RUN_ID.fullmatch(resolved.name) is not None,
            f'refusing to delete {folder}: it is not a run folder directly under {runs}')
    return resolved


def resolve_evidence(raw, root, invocation_id, fixtures=None, repository=ROOT):
    if raw is None:
        if root is None:
            return None
        path = root / 'evidence' / f'{invocation_id}.json'
    else:
        path = Path(raw)
        require(path.is_absolute(), f'--evidence must be an absolute path, not {raw!r}.')
    resolved = path.parent.resolve() / path.name
    require(not within(resolved, repository), 'the evidence path must be outside the repository checkout.')
    if root is not None:
        require(not within(resolved, (root / 'runs').resolve()), 'the evidence path must be outside the run folders.')
    if fixtures is not None:
        require(not within(resolved, fixtures), 'the evidence path must be outside ROMD_FIXTURES.')
    return resolved


def prior_peak(evidence_directory, label):
    """The largest peak a passed run recorded for this disc, or None. Failed or aborted runs can stop early with a
    low peak, so they never count as a measurement."""
    peaks = []
    for path in sorted(evidence_directory.glob('*.json')) if evidence_directory.is_dir() else []:
        try:
            document = json.loads(path.read_text(encoding='utf-8'))
        except (OSError, ValueError):
            continue
        runs = document.get('runs', []) if isinstance(document, dict) else []
        for run in runs if isinstance(runs, list) else []:
            peak = (run.get('disk') or {}).get('peakBytes') if isinstance(run, dict) else None
            if (isinstance(run, dict) and run.get('disc') == label and run.get('status') == 'passed'
                    and isinstance(peak, int) and peak > 0):
                peaks.append(peak)
    return max(peaks, default=None)


def required_free_bytes(measured_peak):
    """A measurement can raise the requirement but never lower it below the assumed 30 GiB peak."""
    return max(DEFAULT_PEAK_BYTES, measured_peak or 0) + PEAK_HEADROOM_BYTES


def synthetic_required_bytes(iso_bytes):
    # Request buffer, upload workspace, CAS original, convergence source, CHD output and CAS CHD, plus slack.
    return 5 * iso_bytes + 2 * GIB


# ---------------------------------------------------------------- identities

class StreamDigest:
    """Size, SHA-256, SHA-1, MD5 and CRC32 in one pass; bytes are never retained."""

    def __init__(self):
        self.size, self._crc = 0, 0
        self._digests = {name: hashlib.new(name) for name in ('sha256', 'sha1', 'md5')}

    def update(self, chunk):
        self.size += len(chunk)
        self._crc = zlib.crc32(chunk, self._crc)
        for digest in self._digests.values():
            digest.update(chunk)

    def identity(self):
        return {'size': self.size, **{name: digest.hexdigest() for name, digest in self._digests.items()},
                'crc32': f'{self._crc:08x}'}


def digest_stream(stream, chunk_bytes=1 << 20):
    digest = StreamDigest()
    while chunk := stream.read(chunk_bytes):
        digest.update(chunk)
    return digest.identity()


def digest_file(path):
    with open(path, 'rb') as stream:
        return digest_stream(stream)


def normalize_identity(values):
    normalized = {}
    for key in IDENTITY_KEYS:
        value = values.get(key)
        if value is None:
            continue
        normalized[key] = int(value) if key == 'size' else str(value).strip().lower()
    return normalized


def identity_mismatches(label, expected, actual, keys=IDENTITY_KEYS):
    """Fails closed: a value absent from either side is a mismatch."""
    wanted, observed = normalize_identity(expected), normalize_identity(actual)
    return [f'{label} {key}: expected {wanted.get(key)}, observed {observed.get(key)}'
            for key in keys if wanted.get(key) is None or wanted.get(key) != observed.get(key)]


def valid_identity(entry, keys=IDENTITY_KEYS):
    try:
        values = normalize_identity(entry)
    except (TypeError, ValueError):
        return False
    return all(key in values for key in keys) and values.get('size', 0) > 0 and all(
        re.fullmatch(f'[0-9a-f]{{{HEX_LENGTHS[key]}}}', values[key]) for key in keys if key != 'size')


def load_manifest(fixtures):
    document = json.loads((fixtures / 'manifest.json').read_text(encoding='utf-8'))
    require(isinstance(document, dict) and document.get('schema') == 1, 'manifest.json must be schema 1.')
    return document


def manifest_entry(manifest, collection, role):
    entries = [entry for entry in manifest.get(collection) or [] if isinstance(entry, dict) and entry.get('role') == role]
    require(len(entries) == 1, f'manifest.json must list exactly one {collection} entry with role {role!r}.')
    return entries[0]


def fixture_file(fixtures, relative):
    """A regular file inside the fixture root, plus the path the read-only client mount exposes."""
    require(isinstance(relative, str) and relative and not relative.startswith('/') and
            not any(character in relative for character in '"\\\0\r\n'), f'unsafe fixture path {relative!r}')
    resolved = (fixtures / relative).resolve(strict=True)
    require(within(resolved, fixtures) and resolved.is_file(), f'fixture {relative!r} is not a regular file under ROMD_FIXTURES')
    return resolved, str(PurePosixPath('/e2e/fixtures') / PurePosixPath(*resolved.relative_to(fixtures).parts))


def dat_rom(source, game_name):
    """The single ROM entry of one named DAT game (Redump DATs carry no SHA-256)."""
    games = [game for game in ET.parse(source).getroot().iter('game') if game.get('name') == game_name]
    require(len(games) == 1, f'the DAT must contain exactly one game named {game_name!r}')
    roms = games[0].findall('rom')
    require(len(roms) == 1, f'DAT game {game_name!r} must have exactly one rom entry')
    rom = roms[0]
    return {key: value for key, value in {'name': rom.get('name'), 'size': rom.get('size'), 'crc32': rom.get('crc'),
            'md5': rom.get('md5'), 'sha1': rom.get('sha1'), 'sha256': rom.get('sha256')}.items() if value is not None}


def parse_reference(stdout):
    results = []
    for line in stdout.splitlines():
        line = line.strip()
        if not line.startswith('{'):
            continue
        try:
            value = json.loads(line)
        except ValueError:
            continue
        if isinstance(value, dict) and 'recordId' in value:
            results.append(value)
    require(len(results) == 1, f'the reference import printed {len(results)} JSON results; expected exactly one')
    return results[0]


def reference_mismatches(reference, expected, media, redump_id):
    observed = {'size': reference.get('logicalBytes'), 'sha1': reference.get('sha1'),
                'md5': reference.get('md5'), 'crc32': reference.get('crc32')}
    problems = identity_mismatches('reference', expected, observed, ('size', 'sha1', 'md5', 'crc32'))
    for key, value in (('system', 'PlayStation2'), ('media', media), ('fileCount', 1),
                       ('source', f'https://redump.info/disc/{redump_id}')):
        if reference.get(key) != value:
            problems.append(f'reference {key}: expected {value!r}, observed {reference.get(key)!r}')
    return problems


def expected_recipe(source_text):
    match = re.search(r'public const string Id = "([^"]+)";', source_text)
    require(match, f'cannot read the canonical recipe ID from {RECIPE_SOURCE.relative_to(ROOT)}')
    return match.group(1)


def recipe_measurement(recipe, role, sha256, size, synthetic=False):
    if synthetic:
        return {'applicable': False, 'reason': 'generated inputs have no recorded recipe measurement'}
    known = RECIPE_MEASUREMENTS.get(recipe, {}).get(role)
    if known is None:
        return {'applicable': False,
                'reason': f'recipe-measurement artifacts exist only for {", ".join(RECIPE_MEASUREMENTS)} real discs'}
    return {'applicable': True, 'expectedSha256': known[0], 'expectedSizeBytes': known[1],
            'matches': sha256 == known[0] and size == known[1]}


# ---------------------------------------------------------------- generated inputs

SECTOR = 2048
PS2_HEADER_BLOCKS, PRIMARY_BLOCK, ROOT_BLOCK, CNF_BLOCK, BOOT_BLOCK = 64, 16, 24, 25, 26
ROOT_OFFSET = ROOT_BLOCK * SECTOR
CNF_RECORD, BOOT_RECORD = ROOT_OFFSET + 68, ROOT_OFFSET + 68 + 46
BOOT_BYTES, ENTRY_POINT = 512, 0x00100000
BOOT_CONFIGURATION = 'BOOT2 = cdrom0:\\SLUS_203.12;1\r\nVER = 1.00\r\nVMODE = NTSC\r\n'


def _both16(buffer, offset, value):
    struct.pack_into('<H', buffer, offset, value)
    struct.pack_into('>H', buffer, offset + 2, value)


def _both32(buffer, offset, value):
    struct.pack_into('<I', buffer, offset, value)
    struct.pack_into('>I', buffer, offset + 4, value)


def _iso_record(buffer, offset, name, block, count, flags):
    buffer[offset] = 33 + len(name) + (1 if len(name) % 2 == 0 else 0)
    _both32(buffer, offset + 2, block)
    _both32(buffer, offset + 10, count)
    buffer[offset + 25] = flags
    _both16(buffer, offset + 28, 1)
    buffer[offset + 32] = len(name)
    buffer[offset + 33:offset + 33 + len(name)] = name


def ps2_iso_header(volume_blocks):
    """Port of tests/Romd.Media.TestSupport/SyntheticPs2Iso.cs with a caller-chosen volume size."""
    buffer = bytearray(PS2_HEADER_BLOCKS * SECTOR)
    for block, kind in ((PRIMARY_BLOCK, 1), (PRIMARY_BLOCK + 1, 255)):
        buffer[block * SECTOR] = kind
        buffer[block * SECTOR + 1:block * SECTOR + 6] = b'CD001'
        buffer[block * SECTOR + 6] = 1
    pvd = PRIMARY_BLOCK * SECTOR
    _both32(buffer, pvd + 80, volume_blocks)
    for offset, value in ((120, 1), (124, 1), (128, SECTOR)):
        _both16(buffer, pvd + offset, value)
    _both32(buffer, pvd + 132, 10)
    struct.pack_into('<I', buffer, pvd + 140, 20)
    struct.pack_into('>I', buffer, pvd + 148, 21)
    buffer[pvd + 881] = 1
    buffer[20 * SECTOR] = 1
    struct.pack_into('<IH', buffer, 20 * SECTOR + 2, ROOT_BLOCK, 1)
    buffer[21 * SECTOR] = 1
    struct.pack_into('>IH', buffer, 21 * SECTOR + 2, ROOT_BLOCK, 1)
    _iso_record(buffer, pvd + 156, b'\0', ROOT_BLOCK, SECTOR, 2)
    _iso_record(buffer, ROOT_OFFSET, b'\0', ROOT_BLOCK, SECTOR, 2)
    _iso_record(buffer, ROOT_OFFSET + 34, b'\1', ROOT_BLOCK, SECTOR, 2)
    configuration = BOOT_CONFIGURATION.encode('ascii')
    _iso_record(buffer, CNF_RECORD, b'SYSTEM.CNF;1', CNF_BLOCK, len(configuration), 0)
    _iso_record(buffer, BOOT_RECORD, b'SLUS_203.12;1', BOOT_BLOCK, BOOT_BYTES, 0)
    buffer[CNF_BLOCK * SECTOR:CNF_BLOCK * SECTOR + len(configuration)] = configuration
    elf = BOOT_BLOCK * SECTOR
    buffer[elf:elf + 7] = b'\x7fELF\x01\x01\x01'
    struct.pack_into('<HHIIIIIHHH', buffer, elf + 16, 2, 8, 1, ENTRY_POINT, 52, 0, 0, 52, 32, 1)
    struct.pack_into('<IIIIIIII', buffer, elf + 52, 1, 256, ENTRY_POINT, 0, 128, 256, 5, 16)
    return buffer


def write_synthetic_iso(path, total_bytes, seed):
    """A recognized PS2 boot layout padded with seeded incompressible bytes, so the CHD stays large."""
    require(total_bytes % SECTOR == 0 and total_bytes >= PS2_HEADER_BLOCKS * SECTOR,
            'the synthetic ISO must be whole 2048-byte sectors and contain the boot layout')
    header = ps2_iso_header(total_bytes // SECTOR)
    digest, generator = StreamDigest(), random.Random(seed)
    with open(path, 'wb') as output:
        output.write(header)
        digest.update(header)
        remaining = total_bytes - len(header)
        while remaining:
            chunk = generator.randbytes(min(remaining, 1 << 20))
            output.write(chunk)
            digest.update(chunk)
            remaining -= len(chunk)
    return digest.identity()


def poison_chd(seed, logical_bytes=1 << 20, body_bytes=64 << 10):
    """A v5 header that passes ChdHeaderPreflight (zlib, 16 KiB hunks, 2 KiB units, no parent) over a random map."""
    generator = random.Random(seed)
    header = bytearray(124)
    header[0:8] = b'MComprHD'
    struct.pack_into('>II', header, 8, 124, 5)
    struct.pack_into('>IIII', header, 16, 0x7A6C6962, 0, 0, 0)
    struct.pack_into('>QQQII', header, 32, logical_bytes, 124, 0, 16384, 2048)
    header[64:104] = generator.randbytes(40)  # raw and combined SHA-1 claims that describe nothing
    return bytes(header) + generator.randbytes(body_bytes)


def write_malformed_archive(path, seed):
    with zipfile.ZipFile(path, 'w', compression=zipfile.ZIP_STORED) as archive:
        for name, data in ((POISON_NAME, poison_chd(seed)), (SIBLING_NAME, random.Random(seed + 1).randbytes(64 << 10))):
            archive.writestr(zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0)), data)


def synthetic_dat(game, filename, identity):
    return ('<?xml version="1.0"?>\n<datafile><header><name>ROMD E2E Synthetic PS2</name>'
            '<description>Disposable generated fixture</description><author>ROMD</author></header>'
            f'<game name={quoteattr(game)}><description>{escape(game)}</description>'
            f'<rom name={quoteattr(filename)} size="{identity["size"]}" crc="{identity["crc32"]}" '
            f'md5="{identity["md5"]}" sha1="{identity["sha1"]}"/></game></datafile>\n')


def redump_page(filename, identity, media='DVD-5'):
    """The layout of RedumpDiscReferenceImporterTests.Page(), the format the unchanged importer parses."""
    cells = (filename, str(identity['size']), identity['crc32'], identity['md5'], identity['sha1'])
    return ("<!DOCTYPE html><html><body><table class='disc-info-table'><tr><td>System</td><td>Sony PlayStation 2</td></tr>"
            f"<tr><td>Media</td><td>{html.escape(media)}</td></tr></table><table class='files-table'><thead><tr>"
            "<th>Filename</th><th>Size</th><th>CRC-32</th><th>MD5</th><th>SHA-1</th></tr></thead><tbody><tr>"
            + ''.join(f'<td>{html.escape(cell)}</td>' for cell in cells) + '</tr></tbody></table></body></html>')


def e2e_ingress_template(text):
    """The working tree's HTTPS example with upstream response buffering off, so multi-GB downloads stream through
    the ingress instead of spooling up to 1 GiB per response into its container layer. Committed files stay unchanged."""
    anchor = '    proxy_request_buffering off;\n'
    require(text.count(anchor) == 1, 'the HTTPS ingress template no longer has exactly one proxy_request_buffering line')
    buffering = re.findall(r'^\s*proxy_buffering\s+([^;]+);', text, re.M)
    require(all(value.strip() == 'off' for value in buffering),
            'the HTTPS ingress template has an unsupported proxy_buffering setting')
    # A location's off directive does not stream API downloads handled by location /.
    if '    proxy_buffering off;' in text.splitlines():
        return text
    return text.replace(anchor, anchor + '    proxy_buffering off;\n')


def mock_redump_conf(disc_id):
    return f'''server {{
    listen 443 ssl;
    server_name redump.info;
    ssl_certificate /etc/e2e-redump/tls/leaf.pem;
    ssl_certificate_key /etc/e2e-redump/tls/leaf.key;
    gzip off;
    location = /disc/{disc_id} {{
        types {{ }}
        default_type "text/html; charset=utf-8";
        alias /srv/redump/{disc_id}.html;
    }}
    location / {{ return 404; }}
}}
'''


def run_command(arguments, label, *, env=None, timeout=None, capture=True):
    """Runs a tool with stdin closed. Failures never echo the command line, which can carry bearer tokens,
    login forms or connection strings."""
    try:
        return subprocess.run([str(argument) for argument in arguments], env=env, stdin=subprocess.DEVNULL,
                              capture_output=capture, timeout=timeout)
    except subprocess.TimeoutExpired:
        raise HarnessError(f'{label} timed out after {timeout}s') from None
    except OSError as error:
        raise HarnessError(f'{label} could not start ({error.strerror})') from None


def openssl(*args):
    result = run_command(['openssl', *args], f'openssl {args[0]}', timeout=120)
    require(result.returncode == 0, f'openssl {args[0]} failed: {tail(result.stderr)}')


def readable_tree(directory):
    """Disposable test keys only: container users other than the host user must read the mounts."""
    directory.chmod(0o755)
    for path in directory.iterdir():
        path.chmod(0o644)


def ingress_certificate(directory):
    directory.mkdir()
    openssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', directory / 'privkey.pem',
            '-out', directory / 'fullchain.pem', '-days', '2', '-subj', '/CN=ROMD CHD end-to-end',
            '-addext', 'subjectAltName=DNS:admin.example.test,DNS:console.example.test,DNS:play.example.test,DNS:unknown.example.test')
    readable_tree(directory)


def redump_certificates(directory):
    directory.mkdir()
    openssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', directory / 'ca.key', '-out', directory / 'ca.pem',
            '-days', '2', '-subj', '/CN=ROMD e2e mock redump CA', '-addext', 'basicConstraints=critical,CA:TRUE',
            '-addext', 'keyUsage=critical,keyCertSign,cRLSign')
    openssl('req', '-newkey', 'rsa:2048', '-nodes', '-keyout', directory / 'leaf.key', '-out', directory / 'leaf.csr',
            '-subj', '/CN=redump.info')
    (directory / 'leaf.ext').write_text('subjectAltName=DNS:redump.info\nbasicConstraints=critical,CA:FALSE\n'
                                        'keyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n')
    openssl('x509', '-req', '-in', directory / 'leaf.csr', '-CA', directory / 'ca.pem', '-CAkey', directory / 'ca.key',
            '-set_serial', '1', '-days', '2', '-extfile', directory / 'leaf.ext', '-out', directory / 'leaf.pem')
    readable_tree(directory)
    return directory / 'ca.pem'


# ---------------------------------------------------------------- generated Compose overlay

@dataclasses.dataclass(frozen=True)
class MockRedump:
    image: str
    subnet: str
    address: str
    conf: Path
    tls: Path
    pages: Path
    ca: Path


def free_subnets(existing, count, pool=SUBNET_POOL):
    taken = [ipaddress.ip_network(value, strict=False) for value in existing if value and ':' not in value]
    chosen = []
    for candidate in pool.subnets(new_prefix=24):
        if any(candidate.overlaps(network) for network in taken + chosen):
            continue
        chosen.append(candidate)
        if len(chosen) == count:
            return [str(network) for network in chosen]
    raise HarnessError(f'no {count} free /24 networks remain in {pool}')


def edge_environment(subnet):
    base = ipaddress.ip_network(subnet).network_address
    return {'ROMD_EDGE_SUBNET': subnet, 'ROMD_EDGE_DYNAMIC_RANGE': f'{base + 128}/25',
            'ROMD_TRUSTED_PROXY': str(base + 2), 'ROMD_ADMIN_EDGE_IP': str(base + 3),
            'ROMD_CONSUMER_EDGE_IP': str(base + 4), 'ROMD_PLAYER_EDGE_IP': str(base + 5)}


def bind(source, target, read_only=False):
    return {'type': 'bind', 'source': str(source), 'target': target, 'read_only': read_only}


def build_overlay(*, data, tls, inputs, client_image, ingress_templates, fixtures=None, mock=None, image_defaults=False):
    """Disposable activation; JSON is valid Compose YAML. Never written outside the run folder."""
    def host(environment):
        # Same target as compose.yaml's romd-data volume, so Compose replaces it with the run folder.
        return {'environment': {} if image_defaults else environment, 'volumes': [bind(data, DATA_TARGET)]}
    services = {
        'romd-admin': host({LIBCHDR_KEY: LIBCHDR_DIRECTORY}),
        'romd-consumer': host({LIBCHDR_KEY: LIBCHDR_DIRECTORY}),
        'romd-worker': host({LIBCHDR_KEY: LIBCHDR_DIRECTORY, CHDMAN_KEY: CHDMAN_DIRECTORY}),
        # A test client only: the admin image supplies curl. It never runs a ROMD host.
        'e2e-client': {
            'image': client_image, 'entrypoint': ['sleep', 'infinity'], 'init': True, 'restart': 'no',
            'networks': ['edge'],
            'volumes': [bind(tls, '/e2e/tls', True), bind(inputs, '/e2e/inputs', True)]
                       + ([bind(fixtures, '/e2e/fixtures', True)] if fixtures else []),
            'logging': {'driver': 'json-file', 'options': {'max-size': '1m', 'max-file': '1'}}},
        # Same target as the example's template mount: the generated copy disables response buffering.
        'ingress': {'volumes': [bind(ingress_templates, '/etc/nginx/templates', True)]},
    }
    overlay = {'services': services}
    if mock is not None:
        worker = services['romd-worker']
        worker['environment']['SSL_CERT_FILE'] = '/etc/romd-e2e/redump-ca.pem'
        worker['extra_hosts'] = [f'redump.info:{mock.address}']
        worker['networks'] = {'backend': {}, 'e2e-redump': {}}
        worker['volumes'].append(bind(mock.ca, '/etc/romd-e2e/redump-ca.pem', True))
        services['e2e-redump'] = {
            'image': mock.image, 'restart': 'no', 'networks': {'e2e-redump': {'ipv4_address': mock.address}},
            'volumes': [bind(mock.conf, '/etc/nginx/conf.d/default.conf', True), bind(mock.tls, '/etc/e2e-redump/tls', True),
                        bind(mock.pages, '/srv/redump', True)],
            'logging': {'driver': 'json-file', 'options': {'max-size': '1m', 'max-file': '1'}}}
        overlay['networks'] = {'e2e-redump': {'internal': True, 'ipam': {'config': [{'subnet': mock.subnet}]}}}
    return overlay


# ---------------------------------------------------------------- checkpoint detection and custody

@dataclasses.dataclass(frozen=True)
class DataSample:
    staging: tuple  # ((name, size), ...) under data/content/.staging
    run_records: tuple  # run-* names under data/temp/media-runs-v1


def _entries(directory):
    try:
        with os.scandir(directory) as iterator:
            return sorted(iterator, key=lambda entry: entry.name)
    except FileNotFoundError:
        return []


def scan_data(data):
    staging = []
    for entry in _entries(data / 'content' / '.staging'):
        try:
            if entry.is_file(follow_symlinks=False):
                staging.append((entry.name, entry.stat(follow_symlinks=False).st_size))
        except FileNotFoundError:
            continue  # published or cleaned between listing and stat
    records = tuple(entry.name for entry in _entries(data / 'temp' / 'media-runs-v1') if entry.name.startswith('run-'))
    return DataSample(tuple(staging), records)


class PublishingCheckpoint:
    """Arms once the convergence job encodes, verifies or publishes; fires on the same staging file growing
    across two consecutive samples with no media run record. A terminal job before that is a miss."""

    ARMING_PHASES = frozenset({'Encoding', 'Verifying', 'Publishing'})

    def __init__(self):
        self.armed, self.missed, self.growth = False, None, None
        self._previous = None

    def observe_job(self, job):
        if self.missed:
            return
        if job.get('isTerminal'):
            self.missed = (f"MISSED checkpoint {CHECKPOINT}: the convergence job became terminal (phase {job.get('phase')}, "
                           f"outcome {job.get('outcome')}, failure {job.get('failureCode')}, errors {job.get('errors')}) "
                           f"before a growing CAS staging file was observed")
        elif job.get('phase') in self.ARMING_PHASES:
            self.armed = True

    def observe_data(self, sample):
        if not self.armed or self.missed:
            self._previous = None
            return False
        previous, self._previous = self._previous, sample
        if previous is None or sample.run_records:
            return False
        before = dict(previous.staging)
        grown = [(name, before[name], size) for name, size in sample.staging if name in before and size > before[name]]
        if grown:
            self.growth = grown[0]
            return True
        return False


def checkpoint_confirmation(job, grown_file, staged_after_kill):
    """None when the killed job still held the checkpoint; otherwise why the kill missed it.
    The file that grew must still be staged: the copy was interrupted, not finished and published."""
    if job.get('isTerminal'):
        return (f"MISSED checkpoint {CHECKPOINT}: the job was already terminal (phase {job.get('phase')}, "
                f"outcome {job.get('outcome')}) when the worker was killed")
    if job.get('phase') != 'Publishing' or job.get('outcome') is not None:
        return (f"MISSED checkpoint {CHECKPOINT}: the killed job reported phase {job.get('phase')}, "
                f"outcome {job.get('outcome')}")
    if grown_file not in staged_after_kill:
        return f'MISSED checkpoint {CHECKPOINT}: the growing staging file {grown_file} was gone when the worker was killed'
    return None


def active_encoding(data, job_id, operation="encode-chd"):
    if operation not in ("encode-chd", "encode-chd-cd"):
        return None
    maximum_record_bytes = (1 << 20) if operation == "encode-chd-cd" else 16384
    """Read only this job's bounded, atomically published custody record and its reserved output."""
    for entry in _entries(data / 'temp' / 'media-runs-v1'):
        if not re.fullmatch(r'run-\d+\.json', entry.name):
            continue
        try:
            if entry.stat(follow_symlinks=False).st_size > maximum_record_bytes or not entry.is_file(follow_symlinks=False):
                continue
            record = json.loads(Path(entry.path).read_bytes())
            if (not isinstance(record, dict) or record.get('Version') != 'romd-media-run/v2' or
                    record.get('JobId') != job_id or record.get('Operation') != operation or
                    record.get('Phase') != 'permitted' or not isinstance(record.get('Parent'), dict) or
                    record['Parent'].get('ProcessId') != 1 or not isinstance(record.get('Identity'), dict) or
                    not isinstance(record.get('Admission'), dict)):
                continue
            outputs = [binding for binding in record['Staging'] if binding['Role'] == 'output']
            if len(outputs) != 1:
                continue
            relative = PurePosixPath(outputs[0]['Path']).relative_to(DATA_TARGET)
            if '..' in relative.parts:
                continue
            output = data.joinpath(*relative.parts)
            if output.is_symlink() or not output.is_file():
                continue
            return {'record': entry.name, 'runId': record['RunId'], 'parent': record['Parent'],
                    'child': record['Identity'], 'admission': record['Admission'],
                    'output': str(relative), 'bytes': output.stat().st_size}
        except (OSError, ValueError, KeyError, TypeError):
            continue  # A transition or incomplete record never arms the checkpoint.
    return None


def encoding_confirmation(job, before, after):
    if (job.get('isTerminal') or job.get('phase') != 'Encoding' or job.get('outcome') is not None or
            after is None or any(before[key] != after[key] for key in ('record', 'runId', 'parent', 'child', 'admission', 'output')) or
            after['bytes'] < before['bytes']):
        return 'MISSED checkpoint encoding: the killed job did not retain the same active encode custody and output'
    return None


def media_child_pids(stack, parent):
    require(type(parent) is int and parent > 1, 'invalid active media child PID')
    # Linux reports children per spawning thread. The CLR may launch chdman from a pool thread.
    children = stack.exec('romd-worker', 'sh', '-c', 'cat /proc/"$1"/task/*/children',
                          'media-children', str(parent)).stdout.split()
    require(all(value.isdigit() and int(value) > 1 for value in children), 'invalid native tool child PID')
    return sorted({int(value) for value in children})


SEALED_CHDMAN_EXECUTABLE = '/memfd:chdman (deleted)'


def pinned_tool_image(stack, pid, directory=CHDMAN_DIRECTORY):
    """The worker executes chdman from a sealed memfd copy, so the running image has no path to compare. Its bytes
    must be the pinned build: the size and digest the build record names, which the configured binary also holds."""
    require(type(pid) is int and pid > 1, 'invalid native tool PID')
    executable = stack.exec('romd-worker', 'readlink', f'/proc/{pid}/exe').stdout.decode().strip()
    require(executable == SEALED_CHDMAN_EXECUTABLE, 'active child is not the sealed chdman image')

    def measure(path):
        size, digest = stack.exec('romd-worker', 'sh', '-c', 'stat -L -c %s "$1" && sha256sum < "$1"', 'tool-image',
                                  path).stdout.decode().split()[:2]
        require(size.isdigit() and re.fullmatch('[0-9a-f]{64}', digest), 'unreadable chdman image measurement')
        return int(size), digest

    image = measure(f'/proc/{pid}/exe')
    record = json.loads(stack.exec('romd-worker', 'cat', directory + '/build-manifest.json').stdout)
    binary = record.get('binary') if isinstance(record, dict) else None
    pinned = (binary.get('bytes'), binary.get('sha256')) if isinstance(binary, dict) else None
    require(pinned is not None and image == pinned, 'active child is not the pinned chdman')
    require(measure(directory + '/native/chdman') == pinned, 'configured chdman binary differs from its build record')
    return {'executable': executable, 'bytes': image[0], 'sha256': image[1]}


def fresh_encoding(previous, observed):
    """A persisted Encoding phase survives the crash; only a new permitted run with output proves re-execution."""
    return observed is not None and observed['runId'] != previous['runId'] and observed['bytes'] > 0


def recovery_reexecuted(timeline):
    """Recovery repeated the encode: a later attempt was observed in Encoding, or in Verifying, which this executor
    reaches only after encoding in the same attempt. A journal replay goes straight to Completed."""
    return any(entry['attempt'] > 1 and entry['phase'] in ('Encoding', 'Verifying') for entry in timeline)


def encode_wall_seconds(timeline):
    """Per attempt, first observed Encoding until the first later observed phase (poll-granular)."""
    result = {}
    for attempt in sorted({entry['attempt'] for entry in timeline}):
        entries = [entry for entry in timeline if entry['attempt'] == attempt]
        starts = [index for index, entry in enumerate(entries) if entry['phase'] == 'Encoding' and
                  index + 1 < len(entries) and entries[index + 1]['phase'] != 'Inspecting']
        if starts and starts[0] + 1 < len(entries):
            result[str(attempt)] = round(entries[starts[0] + 1]['elapsedSeconds'] - entries[starts[0]]['elapsedSeconds'], 1)
    return result


def custody_residue(data):
    """Holds that must be released after recovery; persistent *.lease, capacity and accounting files are not holds."""
    problems = [f'media run record {entry.name}' for entry in _entries(data / 'temp' / 'media-runs-v1')
                if entry.name.startswith('run-')]
    problems += [f'CAS staging entry {entry.name}' for entry in _entries(data / 'content' / '.staging')]
    pool = data / 'temp' / 'staging-v1'
    problems += [f'staging payload {entry.name}' for entry in _entries(pool) if re.fullmatch(r'slot-\d+\.data', entry.name)]
    accounting = pool / 'reservations.json'
    if accounting.is_file():
        try:
            reservations = json.loads(accounting.read_text(encoding='utf-8')).get('Reservations') or {}
        except (OSError, ValueError, AttributeError):
            problems.append('unreadable staging accounting temp/staging-v1/reservations.json')
        else:
            problems += [f'staging reservation slot {slot} ({size} bytes)' for slot, size in sorted(reservations.items())]
    for directory, _, files in os.walk(data / 'temp'):
        problems += [f'quarantine marker {Path(directory, name).relative_to(data)}' for name in sorted(files)
                     if name.endswith('.quarantine')]
    return problems


def malformed_isolation_failures(items):
    by_name = {}
    for item in items:
        if item.get('kind') == 'rom':
            by_name.setdefault(PurePosixPath(str(item.get('fileName', '')).replace('\\', '/')).name, []).append(item)
    problems = []
    poison, sibling = by_name.get(POISON_NAME, []), by_name.get(SIBLING_NAME, [])
    if len(poison) != 1 or poison[0].get('outcome') != 'failed' or poison[0].get('error') != MALFORMED_MEDIA_ERROR:
        problems.append(f'the poison CHD must be exactly one failed item reporting {MALFORMED_MEDIA_ERROR!r}; observed {poison}')
    if len(sibling) != 1 or sibling[0].get('outcome') != 'ingested':
        problems.append(f'the valid sibling must be exactly one ingested item; observed {sibling}')
    if extra := sorted(set(by_name) - {POISON_NAME, SIBLING_NAME}):
        problems.append(f'unexpected archive items {extra}')
    return problems


def guest_mountpoint(df_output):
    lines = [line for line in df_output.splitlines() if line.strip()]
    require(len(lines) >= 2 and lines[0].split()[-1] == 'on', f'unexpected df output: {tail(df_output, 300)}')
    mountpoint = lines[-1].split()[-1]
    require(mountpoint.startswith('/'), f'unexpected guest mountpoint {mountpoint!r}')
    return mountpoint


def parse_http_response(raw):
    headers, _, body = raw.partition(b'\r\n\r\n')
    status = int(headers.split(b'\r\n', 1)[0].split()[1])
    # Multipart uploads can emit an interim 100 Continue before the final response.
    while 100 <= status < 200 and body:
        headers, _, body = body.partition(b'\r\n\r\n')
        status = int(headers.split(b'\r\n', 1)[0].split()[1])
    fields = {}
    for line in headers.decode('iso-8859-1').split('\r\n')[1:]:
        name, separator, value = line.partition(':')
        if separator:
            fields[name.strip().lower()] = value.strip()
    return status, fields, body


# ---------------------------------------------------------------- Docker and HTTP plumbing

INTERRUPT_SIGNALS = (signal.SIGINT, signal.SIGTERM, signal.SIGHUP)


def interrupt(signum, frame):
    # SIGTERM and SIGHUP take the Ctrl-C path, so teardown and evidence still run.
    raise KeyboardInterrupt(signal.Signals(signum).name)


def install_interrupt_handlers():
    # An inherited ignore (nohup) stays in place, so the run and its docker/curl children keep that immunity.
    for signum in (signal.SIGTERM, signal.SIGHUP):
        if signal.getsignal(signum) is not signal.SIG_IGN:
            signal.signal(signum, interrupt)


@contextlib.contextmanager
def interrupts_ignored():
    """A second interrupt, hangup or termination must not abandon cleanup."""
    previous = {signum: signal.signal(signum, signal.SIG_IGN) for signum in INTERRUPT_SIGNALS}
    try:
        yield
    finally:
        for signum, handler in previous.items():
            signal.signal(signum, handler)


def docker(*args, check=True, timeout=None):
    result = run_command(['docker', *args], f'docker {args[0]}', timeout=timeout)
    require(not check or result.returncode == 0, f'docker {args[0]} failed ({result.returncode}): {tail(result.stderr)}')
    return result


def docker_json(*args):
    return json.loads(docker(*args).stdout)


def docker_environment():
    info = json.loads(docker('info', '--format', '{{json .}}', timeout=60).stdout)
    return {'host': {'system': platform.system(), 'release': platform.release(), 'machine': platform.machine()},
            'dockerServer': {key: info.get(key) for key in
                             ('ServerVersion', 'OperatingSystem', 'KernelVersion', 'Architecture', 'NCPU', 'MemTotal')}}


def network_subnets():
    ids = docker('network', 'ls', '-q').stdout.decode().split()
    networks = docker_json('network', 'inspect', *ids) if ids else []
    return [config.get('Subnet') for network in networks for config in (network.get('IPAM') or {}).get('Config') or []]


def image_refs(tag):
    return {name: f'romd-{name}:{tag}' for name in IMAGE_SERVICES}


def image_environment(refs):
    return {f'ROMD_{name.upper()}_IMAGE': ref for name, ref in refs.items()}


def build_images(refs, project, built):
    """Serial target builds: four concurrent builds exhaust the shared 6 GB Rancher VM (known issue).
    Each tag is appended to `built` as soon as it exists, so a partial build is still removed."""
    env = test_environment() | image_environment(refs)
    env['ROMD_ENV_FILE'] = os.devnull
    for name in IMAGE_SERVICES:
        print(f'Building romd-{name} as {refs[name]}', flush=True)
        result = run_command([ROOT / 'scripts/deployment/compose.sh', '-p', project, '-f', ROOT / 'compose.yaml',
                              '-f', ROOT / 'compose.build.yaml', 'build', f'romd-{name}'],
                             f'building romd-{name}', env=env, timeout=7200, capture=False)
        if image_id(refs[name]):
            built.append(refs[name])
        require(result.returncode == 0, f'building romd-{name} failed ({result.returncode})')


def image_id(ref):
    result = docker('image', 'inspect', '--format', '{{.Id}}', ref, check=False)
    return result.stdout.decode().strip() if result.returncode == 0 else None


def source_revision():
    head = run_command(['git', '-C', ROOT, 'rev-parse', 'HEAD'], 'git rev-parse', timeout=60)
    status = run_command(['git', '-C', ROOT, 'status', '--porcelain'], 'git status', timeout=120)
    require(head.returncode == 0 and status.returncode == 0, 'cannot read the source revision')
    return {'revision': head.stdout.decode().strip(), 'dirty': bool(status.stdout.strip())}


class Stack:
    def __init__(self, project, env, files):
        self.project, self.env, self.files = project, env, files
        self.started = False
        self._containers = {}

    def compose(self, *args, capture=False, check=True, timeout=None):
        command = [str(ROOT / 'scripts/deployment/compose.sh'), '-p', self.project]
        for path in self.files:
            command += ['-f', str(path)]
        result = run_command(command + list(args), f'docker compose {args[0]}', env=self.env, timeout=timeout, capture=capture)
        require(not check or result.returncode == 0,
                f'docker compose {args[0]} failed ({result.returncode}){": " + tail(result.stderr) if capture else ""}')
        return result

    def container(self, service):
        if service not in self._containers:
            ids = self.compose('ps', '--all', '-q', service, capture=True).stdout.decode().split()
            require(len(ids) == 1, f'expected one {service} container, found {len(ids)}')
            self._containers[service] = ids[0]
        return self._containers[service]

    def forget(self, service):
        self._containers.pop(service, None)

    def inspect(self, service):
        return docker_json('inspect', self.container(service))[0]

    def exec(self, service, *args, check=True, timeout=None):
        return docker('exec', self.container(service), *args, check=check, timeout=timeout)


def safe_path(path):
    """Capability addresses must never appear in errors or persisted harness evidence."""
    resource = urlsplit(path).path
    if resource.startswith('/delivery/content/'):
        return '/delivery/content/<grant>'
    if resource.startswith('/api/download-tickets/'):
        return '/api/download-tickets/<ticket>'
    return resource


class Api:
    """HTTPS through the real ingress from the disposable client container, trusting only the run's certificate."""

    def __init__(self, stack, proxy, email, password):
        self.stack, self.proxy, self.email, self.password = stack, proxy, email, password
        self._tokens, self._lock, self._sequence = {}, threading.Lock(), itertools.count()

    def _curl(self, host, path, max_time, *arguments):
        return ['curl', '-sS', '--noproxy', '*', '--cacert', '/e2e/tls/fullchain.pem', '--max-time', str(max_time),
                '--resolve', f'{host}:443:{self.proxy}', *arguments, f'https://{host}{path}']

    def raw(self, host, path, *, method=None, form=None, payload=None, upload=None, headers=(), max_time=60, cookies=False):
        arguments = ['-i']
        if cookies:
            arguments += ['-b', '/tmp/e2e-cookies', '-c', '/tmp/e2e-cookies']
        if form is not None:
            arguments += ['--data', urlencode(form)]
        if payload is not None:
            arguments += ['-H', 'Content-Type: application/json', '--data', json.dumps(payload)]
        if upload is not None:
            require(not any(character in upload for character in '"\\\0\r\n'), f'unsafe upload path {upload!r}')
            arguments += ['-F', f'file=@"{upload}"']
        if method:
            arguments += ['-X', method]
        for header in headers:
            arguments += ['-H', header]
        result = self.stack.exec('e2e-client', *self._curl(host, path, max_time, *arguments), timeout=max_time + 60)
        return parse_http_response(result.stdout)

    def sign_in(self, host):
        """Authorization code + PKCE through the HTTPS ingress, as the SPAs do."""
        verifier = secrets.token_urlsafe(32)
        challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).decode().rstrip('=')
        redirect = f'https://{host}/auth/callback'
        authorize = '/connect/authorize?' + urlencode(dict(
            client_id=CLIENT_IDS[host], response_type='code', scope='openid profile email roles offline_access',
            redirect_uri=redirect, code_challenge=challenge, code_challenge_method='S256', state='e2e'))
        status, _, body = self.raw(host, '/connect/login', cookies=True)
        require(status == 200, f'{host} login form failed ({status})')
        token = re.search(r'name="__RequestVerificationToken" value="([^"]+)"', body.decode())
        require(token is not None, f'{host} login form had no antiforgery token')
        status, _, _ = self.raw(host, '/connect/login', cookies=True,
                                form=dict(login=self.email, password=self.password, returnUrl=authorize,
                                          __RequestVerificationToken=html.unescape(token[1])))
        require(status == 302, f'{host} login failed ({status})')
        status, headers, _ = self.raw(host, authorize, cookies=True)
        require(status == 302 and 'location' in headers, f'{host} authorization failed ({status})')
        code = parse_qs(urlsplit(headers['location']).query)['code'][0]
        status, _, body = self.raw(host, '/connect/token', cookies=True, form=dict(
            grant_type='authorization_code', client_id=CLIENT_IDS[host], redirect_uri=redirect, code=code,
            code_verifier=verifier))
        require(status == 200, f'{host} token exchange failed ({status})')
        return json.loads(body)['access_token']

    def token(self, host):
        with self._lock:
            if host not in self._tokens:
                self._tokens[host] = self.sign_in(host)
            return self._tokens[host]

    def forget(self, host):
        with self._lock:
            self._tokens.pop(host, None)

    def call(self, host, path, *, expect=(200,), headers=(), **options):
        for attempt in (1, 2):
            status, fields, body = self.raw(host, path, headers=[f'Authorization: Bearer {self.token(host)}', *headers], **options)
            if status != 401 or attempt == 2:
                break
            self.forget(host)  # expired, or revoked by a library assignment
        resource = urlsplit(path).path
        shown_body = ('<download ticket response redacted>' if resource == '/api/download-tickets'
                      or resource.startswith('/api/download-tickets/') else tail(body, 400))
        require(expect is None or status in expect,
                f"{options.get('method') or 'GET'} https://{host}{safe_path(path)} returned {status}: {shown_body}")
        return status, fields, body

    def json(self, host, path, **options):
        return json.loads(self.call(host, path, **options)[2])

    def download(self, host, path, *, authorized=True, max_time=7200):
        """Stream a full object through curl's stdout and hash it as it arrives; nothing is saved."""
        shown = safe_path(path)
        for _ in range(12):
            header_file = f'/tmp/e2e-headers-{next(self._sequence)}'
            headers = ['-H', f'Authorization: Bearer {self.token(host)}'] if authorized else []
            command = ['docker', 'exec', self.stack.container('e2e-client'),
                       *self._curl(host, path, max_time, '--dump-header', header_file, *headers)]
            digest, started = StreamDigest(), time.monotonic()
            with subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE) as process:
                while chunk := process.stdout.read(1 << 20):
                    digest.update(chunk)
                exit_code = process.wait()
            require(exit_code == 0, f'streamed download of {shown} failed (curl exit {exit_code})')
            status, fields, _ = parse_http_response(self.stack.exec('e2e-client', 'cat', header_file).stdout)
            if status == 401 and authorized:
                self.forget(host)
                continue
            if status == 503:
                time.sleep(min(int(fields.get('retry-after', '15') or 15), 60))
                continue
            require(status == 200, f'download of {shown} returned {status}')
            identity = digest.identity()
            require(int(fields.get('content-length', identity['size'])) == identity['size'],
                    'downloaded byte count differs from Content-Length')
            seconds = time.monotonic() - started
            return {**identity, 'host': host, 'seconds': round(seconds, 1), 'cacheControl': fields.get('cache-control'),
                    'megabytesPerSecond': round(identity['size'] / 1e6 / max(seconds, 0.001), 1)}
        raise HarnessError(f'download of {shown} kept returning 401/503')



def require_verified_selection(job):
    selection = job.get('selection') or {}
    require(job.get('phase') == 'Completed' and job.get('outcome') == 'Completed'
            and job.get('outcomeCode') == 'selected-work-complete' and not job.get('hasErrors')
            and job.get('requestMode') == 'Selection'
            and all(selection.get(key) == value for key, value in {
                'filter': 'RetainedRepresentations', 'limit': 1, 'conversionRequested': False,
                'selected': 1, 'created': 1, 'adopted': 0, 'skipped': 0, 'completed': 1, 'failed': 0,
                'enumerationComplete': True}.items()),
            'bounded verify-only selection did not complete exactly one owned retained representation')


def wait_for_job(api, job_id, timeout_seconds, interval=2.0):
    deadline = time.monotonic() + timeout_seconds
    while True:
        job = api.json(ADMIN_HOST, f'/api/jobs/{job_id}')
        if job.get('isTerminal'):
            return job
        require(time.monotonic() < deadline, f"job {job_id} not terminal after {timeout_seconds}s (phase {job.get('phase')})")
        time.sleep(interval)


def paged_items(api, path, *, host=ADMIN_HOST, **filters):
    """Read a complete canonical Page collection; an incomplete feed cannot qualify a run."""
    items, cursor, seen = [], None, set()
    while True:
        page = api.json(host, path + '?' + urlencode(
            {**filters, 'limit': 100, **({'cursor': cursor} if cursor is not None else {})}))
        require(isinstance(page, dict) and isinstance(page.get('items'), list)
                and all(isinstance(item, dict) for item in page['items'])
                and type(page.get('hasNextPage')) is bool, f'{path} returned an invalid page')
        next_cursor = page.get('nextCursor')
        if page['hasNextPage']:
            require(isinstance(next_cursor, str) and bool(next_cursor) and next_cursor not in seen,
                    f'{path} returned an invalid or repeated next cursor')
            seen.add(next_cursor)
        else:
            require(next_cursor is None, f'{path} returned a cursor after its final page')
        items.extend(page['items'])
        if not page['hasNextPage']:
            return items
        cursor = next_cursor


def all_jobs(api, job_type=None):
    return paged_items(api, '/api/jobs', archive='all', **({'jobType': job_type} if job_type else {}))


def job_items(api, job_id, *, feed='items'):
    return paged_items(api, f'/api/jobs/{job_id}/{feed}')


def upload(api, endpoint, container_path, max_time):
    _, _, body = api.call(ADMIN_HOST, endpoint, upload=container_path, max_time=max_time, expect=(202,))
    return json.loads(body)['jobId']


class JobWatcher(threading.Thread):
    """Polls one convergence job, recording poll-granular phase transitions and deferrals per attempt."""

    def __init__(self, api, job_id, interval):
        super().__init__(daemon=True)
        self.api, self.job_id, self.interval = api, job_id, interval
        self.attempt, self.timeline, self.deferrals = 1, [], []
        self.events, self.latest, self.error = queue.Queue(), None, None
        self._started_at, self._halt = time.monotonic(), threading.Event()

    def run(self):
        try:
            while not self._halt.is_set():
                # Captured before the request: a poll issued before the kill describes the earlier attempt
                # even when its response arrives after the controller advanced `attempt`.
                attempt = self.attempt
                job = self.api.json(ADMIN_HOST, f'/api/jobs/{self.job_id}')
                self._record(job, attempt)
                self.events.put(job)
                self._halt.wait(self.interval)
        except BaseException as error:  # surfaced by the controlling thread
            self.error = error

    def _record(self, job, attempt):
        phase = job.get('phase')
        if not self.timeline or (self.timeline[-1]['attempt'], self.timeline[-1]['phase']) != (attempt, phase):
            self.timeline.append({'attempt': attempt, 'phase': phase, 'observedUtc': iso(utc_now()),
                                  'elapsedSeconds': round(time.monotonic() - self._started_at, 1)})
        seen = {(entry['message'], entry['occurredAt']) for entry in self.deferrals}
        for error in job.get('errors') or []:
            key = (error.get('message'), error.get('occurredAt'))
            # A deferral keeps its original timestamp while it is still reported; attribute it to its first attempt.
            if error.get('item') == 'deferral' and key not in seen:
                seen.add(key)
                self.deferrals.append({'attempt': attempt, 'message': key[0], 'occurredAt': key[1]})
        self.latest = job

    def stop(self):
        self._halt.set()
        self.join(timeout=self.interval + 120)


class DiskSampler(threading.Thread):
    def __init__(self, filesystem, folder, interval=5.0):
        super().__init__(daemon=True)
        self.filesystem, self.folder, self.interval = filesystem, folder, interval
        self.free_before = self.minimum_free = shutil.disk_usage(filesystem).free
        self.maximum_folder = 0
        self._halt = threading.Event()

    @staticmethod
    def allocated(folder):
        total = 0
        for directory, _, files in os.walk(folder, onerror=lambda error: None):
            for name in files:
                try:
                    total += os.lstat(os.path.join(directory, name)).st_blocks * 512
                except OSError:
                    continue
        return total

    def sample(self):
        self.minimum_free = min(self.minimum_free, shutil.disk_usage(self.filesystem).free)
        self.maximum_folder = max(self.maximum_folder, self.allocated(self.folder))

    def run(self):
        while not self._halt.wait(self.interval):
            self.sample()

    def summary(self):
        self._halt.set()
        self.join(timeout=self.interval + 60)
        drop = self.free_before - self.minimum_free
        return {'freeBeforeBytes': self.free_before, 'peakHostFreeDropBytes': drop,
                'peakRunFolderBytes': self.maximum_folder, 'peakBytes': max(drop, self.maximum_folder),
                'sampleIntervalSeconds': self.interval}


def rdctl_path():
    return shutil.which('rdctl') or str(Path.home() / '.rd/bin/rdctl')


def release_rancher_bind_mount_files():
    """Deleted bind-mounted files stay allocated on macOS while the guest caches their inodes (known issues)."""
    rdctl = rdctl_path()
    if not Path(rdctl).is_file():
        return {'status': 'skipped', 'reason': 'rdctl not found; drop the guest caches manually'}
    dropped = run_command([rdctl, 'shell', 'sudo', 'sh', '-c', 'sync && echo 2 > /proc/sys/vm/drop_caches'],
                          'rdctl drop_caches', timeout=300)
    return {'status': 'released' if dropped.returncode == 0 else 'failed', 'reason': tail(dropped.stderr, 300)}


def trim_rancher_vm():
    """Return freed VM blocks to macOS; BusyBox fstrim needs the guest's real mountpoint (known issues)."""
    rdctl = rdctl_path()
    if not Path(rdctl).is_file():
        return {'status': 'skipped', 'reason': 'rdctl not found; trim the Docker VM manually'}
    probe = run_command([rdctl, 'shell', 'df', '-P', '/var/lib/docker'], 'rdctl df', timeout=120)
    if probe.returncode:
        return {'status': 'failed', 'reason': f'rdctl df failed: {tail(probe.stderr, 300)}'}
    mountpoint = guest_mountpoint(probe.stdout.decode('utf-8', 'replace'))
    trimmed = run_command([rdctl, 'shell', 'sudo', 'fstrim', '-v', mountpoint], 'rdctl fstrim', timeout=1800)
    return {'status': 'trimmed' if trimmed.returncode == 0 else 'failed', 'mountpoint': mountpoint,
            'output': tail(trimmed.stdout or trimmed.stderr, 300)}


# ---------------------------------------------------------------- one disposable run

@dataclasses.dataclass
class Subject:
    label: str
    role: str
    media: str
    redump_id: int
    identity: dict  # logical bytes: size, sha256, sha1, md5, crc32
    dat_identity: dict  # the DAT entry quartet the original download must match
    dat_container_path: str = ''
    iso_container_path: str = ''
    provenance: dict = dataclasses.field(default_factory=dict)  # real discs: fixture, manifest and DAT identities


@dataclasses.dataclass(frozen=True)
class Settings:
    synthetic: bool
    root: Path
    fixtures: Path | None
    evidence_directory: Path | None
    refs: dict
    recipe: str
    nginx_image: str
    synthetic_bytes: int
    convergence_timeout: int
    upload_timeout: int
    checkpoint: str = CHECKPOINT
    https_bind: str = "127.0.0.1"
    image_defaults: bool = False


class Qualification:
    encoding_operation = "encode-chd"
    def __init__(self, settings, subject):
        self.settings, self.subject = settings, subject
        self.run_id = new_run_id(subject.label)
        self.folder = run_folder(settings.root, self.run_id)
        self.data = self.folder / 'data'
        self.stack = self.api = self.sampler = None
        self.created = False  # only a folder this run created is ever deleted
        self.rom_id = self.title_id = self.chd = self.convergence_id = self.final_job = None
        self.record = {'disc': subject.label, 'role': subject.role, 'runId': self.run_id, 'composeProject': self.run_id,
                       'checkpoint': {'name': settings.checkpoint, 'definition': checkpoint_description(settings.checkpoint)},
                       'workerServingLimitation': WORKER_READ_LIMITATION, 'status': 'running', 'startedUtc': iso(utc_now())}
        if subject.provenance:
            self.record['fixture'] = {**subject.provenance, 'identity': subject.identity, 'datIdentity': subject.dat_identity}

    def run(self):
        failure = None
        try:
            self.prepare()
            self.assert_startup_failure()
            self.start()
            self.assert_activation()
            self.import_catalog()
            self.upload_disc()
            self.converge_through_crash()
            self.assert_recovered()
            self.assert_serving_admission()
            self.download_formats()
            self.assert_malformed_isolation()
            self.record['status'] = 'passed'
        except BaseException as error:
            failure = error
            self.record.update(status='failed', error=f'{type(error).__name__}: {error}')
            raise
        finally:
            with interrupts_ignored():
                cleanup_error = self.teardown(failed=failure is not None)
            self.record['finishedUtc'] = iso(utc_now())
            if cleanup_error and failure is None:
                raise HarnessError(cleanup_error)
        return self.record

    def step(self, message):
        print(f'[{self.run_id}] {message}', flush=True)

    # -- setup

    def prepare(self):
        settings, subject = self.settings, self.subject
        runs_directory(settings.root)
        self.folder.mkdir()
        self.created = True
        self.data.mkdir()
        # The image's app user (UID 1654, group 1654) is "other" to this host-owned directory, both on Linux and
        # through Rancher's virtiofs. It needs read, write and search there; group bits are unnecessary.
        self.data.chmod(0o707)
        (self.data / '.romd-e2e-run').write_text(self.run_id)
        tls = self.folder / 'tls'
        ingress_certificate(tls)
        inputs = self.folder / 'inputs'
        inputs.mkdir()
        write_malformed_archive(inputs / MALFORMED_ARCHIVE, SYNTHETIC_SEED)
        mock = None
        edge, redump = free_subnets(network_subnets(), 2)
        if settings.synthetic:
            self.step(f'generating a {settings.synthetic_bytes} byte synthetic PS2 DVD ISO')
            identity = write_synthetic_iso(inputs / SYNTHETIC_ISO, settings.synthetic_bytes, SYNTHETIC_SEED)
            subject.identity = subject.dat_identity = identity
            (inputs / 'romd-e2e-synthetic-ps2.dat').write_text(synthetic_dat(SYNTHETIC_GAME, SYNTHETIC_ISO, identity))
            subject.iso_container_path = f'/e2e/inputs/{SYNTHETIC_ISO}'
            subject.dat_container_path = '/e2e/inputs/romd-e2e-synthetic-ps2.dat'
            pages = self.folder / 'redump-pages'
            pages.mkdir()
            (pages / f'{subject.redump_id}.html').write_text(redump_page(SYNTHETIC_ISO, identity), encoding='utf-8')
            conf = self.folder / 'redump.conf'
            conf.write_text(mock_redump_conf(subject.redump_id))
            mock_tls = self.folder / 'redump-tls'
            ca = redump_certificates(mock_tls)
            readable_tree(pages)
            conf.chmod(0o644)
            mock = MockRedump(settings.nginx_image, redump, str(ipaddress.ip_network(redump).network_address + 10),
                              conf, mock_tls, pages, ca)
        readable_tree(inputs)
        templates = self.folder / 'ingress-templates'
        templates.mkdir()
        template = ROOT / 'deploy/examples/https/templates/default.conf.template'
        (templates / template.name).write_text(e2e_ingress_template(template.read_text(encoding='utf-8')), encoding='utf-8')
        readable_tree(templates)
        overlay = self.folder / 'compose.e2e.yaml'
        overlay.write_text(json.dumps(build_overlay(data=self.data, tls=tls, inputs=inputs, client_image=settings.refs['admin'],
                                                    ingress_templates=templates, fixtures=settings.fixtures, mock=mock,
                                                    image_defaults=settings.image_defaults), indent=2))
        env = test_environment() | image_environment(settings.refs) | edge_environment(edge)
        empty = self.folder / 'empty.env'
        empty.touch()
        env.update(ROMD_DEFAULT_ADMIN_EMAIL='admin@example.test', ROMD_TLS_DIRECTORY=str(tls), ROMD_HTTPS_PORT='0', ROMD_HTTPS_BIND=settings.https_bind,
                   ROMD_ENV_FILE=str(empty), ROMD_NGINX_IMAGE=settings.nginx_image)
        self.stack = Stack(self.run_id, env, [ROOT / 'compose.yaml', ROOT / 'deploy/examples/https/compose.yaml', overlay])
        self.api = Api(self.stack, env['ROMD_TRUSTED_PROXY'], env['ROMD_DEFAULT_ADMIN_EMAIL'], env['ROMD_DEFAULT_ADMIN_PASSWORD'])
        self.record['networks'] = {'edge': edge, **({'mockRedump': redump} if mock else {})}
        self.record['ingress'] = INGRESS_DEVIATION
        self.sampler = DiskSampler(settings.root, self.folder)
        self.sampler.start()

    def start(self):
        self.step('starting PostgreSQL, worker, admin, consumer, player, ingress and the test client')
        self.stack.started = True
        self.stack.compose('up', '-d', '--wait', '--wait-timeout', '600')
        images = {}
        for service in ('postgres', *HOSTS, 'romd-player', 'ingress'):
            info = self.stack.inspect(service)
            images[service] = {'ref': info['Config']['Image'], 'id': info['Image']}
        self.record['images'] = images

    def assert_startup_failure(self):
        """Exercise the actual image entrypoint as PID 1, including the failure that formerly required Docker init."""
        name = self.run_id + '-startup-failure'
        self.stack.started = True  # compose run can create this project's networks/volumes even before startup fails.
        try:
            result = self.stack.compose('run', '--rm', '--no-deps', '-T', '--name', name,
                                        '-e', LIBCHDR_KEY + '=/nonexistent/romd-e2e-libchdr', 'romd-worker',
                                        capture=True, check=False, timeout=30)
            require(result.returncode == 1 and b'Worker terminated unexpectedly' in result.stdout + result.stderr,
                    f'worker native-asset startup failure did not exit cleanly with status 1 ({result.returncode})')
            self.record['startupFailure'] = {'exitCode': result.returncode, 'boundedSeconds': 30,
                                             'entrypoint': 'unchanged image entrypoint; init: false'}
        finally:
            # --rm handles ordinary exits; a timed-out PID1 must not outlive this disposable drill.
            docker('rm', '-f', name, check=False, timeout=60)

    def assert_activation(self, *, decoder_source=None):
        activation = {}
        upstream, recipe_version = decoder_source or current_decoder_source()
        decoders = []
        for service in HOSTS:
            info = self.stack.inspect(service)
            environment = dict(item.split('=', 1) for item in info['Config'].get('Env') or [] if '=' in item)
            native = {key: environment[key] for key in (LIBCHDR_KEY, CHDMAN_KEY) if key in environment}
            expected = {LIBCHDR_KEY: LIBCHDR_DIRECTORY, **({CHDMAN_KEY: CHDMAN_DIRECTORY} if service == 'romd-worker' else {})}
            require(native == expected, f'{service} native configuration is {native}, expected {expected}')
            image_record = docker_json('image', 'inspect', info['Image'])[0]
            image = image_record['Config']
            image_env = dict(item.split('=', 1) for item in image.get('Env') or [] if '=' in item)
            image_native = {key: image_env[key] for key in (LIBCHDR_KEY, CHDMAN_KEY) if key in image_env}
            if self.settings.image_defaults:
                require(image_native == expected, f'{service} image defaults are {image_native}, expected {expected}')
            manifest = self.stack.exec(service, 'cat', LIBCHDR_DIRECTORY + '/build-manifest.json').stdout
            require(len(manifest) <= 1 << 20, 'native build record exceeds 1 MiB')
            decoder = decoder_identity(json.loads(manifest), image_record.get('Architecture'), upstream, recipe_version)
            decoders.append(decoder)
            entrypoint, command = info['Config'].get('Entrypoint'), info['Config'].get('Cmd')
            require(entrypoint == image.get('Entrypoint') and command == image.get('Cmd'),
                    f'{service} does not run its image entry point ({entrypoint} {command})')
            mounts = [mount for mount in info.get('Mounts') or [] if mount.get('Destination') == DATA_TARGET]
            require(len(mounts) == 1 and mounts[0].get('Type') == 'bind' and Path(mounts[0].get('Source', '')) == self.data,
                    f'{service} does not bind {DATA_TARGET} from the run folder: {mounts}')
            health = (info['State'].get('Health') or {}).get('Status')
            require(health == 'healthy', f'{service} is {health}, not healthy')
            if service == 'romd-worker':
                require(info['HostConfig'].get('Init') is not True and not info['HostConfig'].get('PidMode'),
                        'worker must own PID 1 in its private namespace; Docker init/shared PID namespaces break recovery')
                require(self.stack.exec(service, 'cat', '/proc/1/comm').stdout.strip() == b'dotnet',
                        'the stock worker is not namespace PID 1')
            activation[service] = {'native': native, 'imageNativeDefaults': image_native,
                                   'entrypoint': entrypoint, 'cmd': command, 'health': health,
                                   'dataBind': mounts[0]['Source'], 'decoder': decoder}
        require(all(decoder == decoders[0] for decoder in decoders), 'server hosts do not use the same decoder build')
        self.record['decoder'] = decoders[0]
        marker = self.stack.exec('romd-worker', 'cat', f'{DATA_TARGET}/.romd-e2e-run').stdout.decode().strip()
        require(marker == self.run_id, 'the worker does not see the run folder data marker')
        for service in READING_HOSTS:
            body = self.stack.exec(service, 'curl', '-sS', '--max-time', '10', 'http://localhost:8080/health/ready',
                                   check=False).stdout
            report = json.loads(body or b'{}')
            native = [check.get('status') for check in report.get('checks') or [] if check.get('name') == 'native-media']
            require(native == ['healthy'], f'{service} /health/ready native-media is {native} (overall {report.get("status")})')
            activation[service]['readiness'] = {'status': report.get('status'), 'nativeMedia': native[0]}
        activation['evidence'] = ('effective environment plus healthy startup, whose native asset checks fail closed; '
                                  '/health/ready native-media is recorded but is also healthy when CHD is off')
        activation['configurationOrigin'] = 'image-defaults' if self.settings.image_defaults else 'qualification-overlay'
        self.record['activation'] = activation
        self.step('native activation evidence: effective env (libchdr on admin/consumer/worker, chdman on the worker '
                  'only) and healthy startup; default entry points; run-folder data binds')

    # -- catalog and disc

    def import_catalog(self):
        subject = self.subject
        self.step('importing the PS2 DAT through the normal DAT upload')
        job = wait_for_job(self.api, upload(self.api, '/api/upload/dat?systemKey=ps2', subject.dat_container_path, 600), 1800)
        require(not job.get('hasErrors') and job.get('datsSucceeded') == 1, f'DAT import failed: {job.get("errors")}')
        self.step(f'importing Redump disc reference {subject.redump_id} through the worker command')
        result = self.stack.compose('exec', '-T', 'romd-worker', 'dotnet', 'romd-worker.dll', '--import-redump-disc',
                                    subject.redump_id, capture=True, check=False, timeout=300)
        require(result.returncode == 0, f'reference import exited {result.returncode}: {tail(result.stderr, 800)}')
        reference = parse_reference(result.stdout.decode('utf-8', 'replace'))
        problems = reference_mismatches(reference, subject.identity, subject.media, subject.redump_id)
        problems += reference_mismatches(reference, subject.dat_identity, subject.media, subject.redump_id)
        require(not problems, 'reference import disagrees with the fixture/DAT: ' + '; '.join(sorted(set(problems))))
        self.record['reference'] = {key: reference.get(key) for key in
                                    ('source', 'system', 'media', 'fileCount', 'logicalBytes', 'sha1', 'md5', 'crc32',
                                     'rawSha256', 'retrievedAtUtc')}

    def upload_disc(self):
        self.step('uploading the ISO from the read-only client mount')
        started = time.monotonic()
        job_id = upload(self.api, '/api/upload/rom', self.subject.iso_container_path, self.settings.upload_timeout)
        job = wait_for_job(self.api, job_id, self.settings.upload_timeout)
        require(not job.get('hasErrors') and job.get('romsIngested') == 1, f'ISO ingestion failed: {job.get("errors")}')
        roms = [item for item in job_items(self.api, job_id) if item.get('kind') == 'rom']
        require(len(roms) == 1 and roms[0].get('outcome') == 'ingested' and roms[0].get('romFileId'),
                f'unexpected ISO ingestion items: {roms}')
        self.rom_id = roms[0]['romFileId']
        deadline = time.monotonic() + 300
        while True:
            jobs = all_jobs(self.api, 'media-reconciliation')
            convergence = [job for job in jobs if job.get('jobType') == 'media-reconciliation' and job.get('requestMode') == 'Subject'
                           and job.get('subject') == {'kind': 'LogicalRom', 'id': self.rom_id}]
            if convergence:
                break
            require(time.monotonic() < deadline, 'no logical-ROM reconciliation demand appeared after the import')
            time.sleep(2)
        require(len(convergence) == 1, f'expected one convergence job for ROM {self.rom_id}, found {len(convergence)}')
        self.convergence_id = convergence[0]['id']
        self.record.update(romId=self.rom_id, uploadSeconds=round(time.monotonic() - started, 1),
                           reconciliation={'evidence': 'current logical-ROM demand staged by import', 'jobId': self.convergence_id})

    def converge_through_crash(self):
        settings = self.settings
        # Short synthetic phases need a fast poll for the re-execution evidence; real discs encode for minutes.
        watcher = JobWatcher(self.api, self.convergence_id, 0.5 if settings.synthetic else 2.0)
        worker = self.stack.container('romd-worker')  # resolved before arming: the kill must not wait for Compose
        watcher.start()
        try:
            checkpoint = settings.checkpoint
            self.step(f'watching for checkpoint {checkpoint}: {checkpoint_description(checkpoint)}')
            detector, deadline = PublishingCheckpoint(), time.monotonic() + settings.convergence_timeout
            previous_encoding = None
            while True:
                if watcher.error:
                    raise watcher.error
                while not watcher.events.empty():
                    detector.observe_job(watcher.events.get_nowait())
                if checkpoint == 'encoding':
                    job = watcher.latest or {}
                    if job.get('isTerminal'):
                        raise CheckpointMissed('MISSED checkpoint encoding: job became terminal before an active encode was observed')
                    encoded = active_encoding(self.data, self.convergence_id, self.encoding_operation)
                    if (job.get('phase') == 'Encoding' and encoded is not None and previous_encoding is not None and
                            encoded['runId'] == previous_encoding['runId'] and encoded['bytes'] > previous_encoding['bytes']):
                        # These are live process observations, not a job phase standing in for an active tool.
                        child = encoded['child']['ProcessId']
                        children = media_child_pids(self.stack, child)
                        require(len(children) == 1, 'active media child has no unique tool child')
                        tool = children[0]
                        image = pinned_tool_image(self.stack, tool)
                        self.record['checkpoint'].update(activeEncoding=encoded, toolPid=tool, toolExecutable=image['executable'],
                                                         toolImage={key: image[key] for key in ('bytes', 'sha256')})
                        break
                    previous_encoding = encoded
                elif detector.missed:
                    raise CheckpointMissed(detector.missed)
                sample = scan_data(self.data)
                if checkpoint == 'publishing' and detector.observe_data(sample):
                    break
                require(time.monotonic() < deadline, f'checkpoint {checkpoint} not reached within {settings.convergence_timeout}s')
                time.sleep(0.025)
            docker('kill', '-s', 'KILL', worker, timeout=60)
            killed = utc_now()
            staged_after_kill = dict(scan_data(self.data).staging)
            watcher.attempt = 2
            job = self.api.json(ADMIN_HOST, f'/api/jobs/{self.convergence_id}')
            if checkpoint == 'encoding':
                retained = active_encoding(self.data, self.convergence_id, self.encoding_operation)
                if reason := encoding_confirmation(job, encoded, retained):
                    raise CheckpointMissed(reason)
                self.record['checkpoint'].update(killedUtc=iso(killed), jobPhase=job.get('phase'), retainedEncoding=retained)
            else:
                name, before, after = detector.growth
                if reason := checkpoint_confirmation(job, name, staged_after_kill):
                    raise CheckpointMissed(reason)
                self.record['checkpoint'].update(killedUtc=iso(killed), jobPhase=job.get('phase'), stagingFile=name,
                                                 stagingBytesBefore=before, stagingBytesObserved=after,
                                                 runRecordsAtKill=list(sample.run_records), stagingAfterKill=staged_after_kill)
            self.step(f'checkpoint {checkpoint} held: worker killed with SIGKILL')
            self.stack.forget('romd-worker')
            recreate = ['--force-recreate', '--no-deps'] if checkpoint == 'encoding' else []
            self.stack.compose('up', '-d', '--wait', '--wait-timeout', '600', *recreate, 'romd-worker')
            if checkpoint == 'encoding':
                require(self.stack.container('romd-worker') != worker, 'worker container was not recreated')
            self.record['recovery'] = {'restartedUtc': iso(utc_now()), 'deferrals': watcher.deferrals}
            self.step('worker restarted; waiting for durable redelivery (execution lease expiry) and reconvergence')
            deadline = time.monotonic() + settings.convergence_timeout
            while not (watcher.latest or {}).get('isTerminal'):
                if watcher.error:
                    raise watcher.error
                if checkpoint == 'encoding' and 'activeEncoding' not in self.record['recovery']:
                    observed = active_encoding(self.data, self.convergence_id, self.encoding_operation)
                    if fresh_encoding(encoded, observed):
                        self.record['recovery']['activeEncoding'] = observed
                require(time.monotonic() < deadline, f'convergence did not recover within {settings.convergence_timeout}s')
                time.sleep(2)
            self.record['recovery']['terminalUtc'] = iso(utc_now())
        finally:
            watcher.stop()
            self.record['timeline'] = watcher.timeline
            self.record['timelineGranularitySeconds'] = watcher.interval
            self.record['encodeWallSeconds'] = encode_wall_seconds(watcher.timeline)
        self.final_job = self.api.json(ADMIN_HOST, f'/api/jobs/{self.convergence_id}')

    def wait_for_released_custody(self, timeout_seconds=300):
        deadline = time.monotonic() + timeout_seconds
        while problems := custody_residue(self.data):
            require(time.monotonic() < deadline, 'custody was not released: ' + '; '.join(problems))
            time.sleep(2)

    def assert_recovered(self):
        job = self.final_job
        require(job.get('phase') == 'Completed' and job.get('outcome') == 'Completed' and not job.get('hasErrors')
                and job.get('nextAttemptAt') is None,
                f"recovery ended in phase {job.get('phase')}, outcome {job.get('outcome')}, failure {job.get('failureCode')}")
        after_kill = [entry for entry in self.record['recovery']['deferrals'] if entry['attempt'] > 1]
        require(not after_kill, f'recovery was deferred (custody or capacity pin): {after_kill}')
        self.record['recovery']['reexecuted'] = (
            fresh_encoding(self.record['checkpoint']['activeEncoding'], self.record['recovery'].get('activeEncoding'))
            if self.settings.checkpoint == 'encoding' else recovery_reexecuted(self.record.get('timeline') or []))
        require(self.record['recovery']['reexecuted'],
                'recovery did not repeat the encode (no later Encoding/Verifying observed): the interrupted publication was not re-executed')
        query = ('SELECT r."Encoding", o."Id" = logical."PreferredOutputId", c."RecipeId" FROM romd."Roms" logical '
                 'JOIN romd."RepresentationOutputs" o ON o."RomId" = logical."Id" '
                 'JOIN romd."Representations" r ON r."Id" = o."RepresentationId" '
                 'JOIN romd."RepresentationCreations" c ON c."RepresentationId" = r."Id" '
                 f"""WHERE logical."Sha1" = decode('{normalize_identity(self.subject.identity)["sha1"]}', 'hex')""")
        rows = self.stack.compose('exec', '-T', 'postgres', 'psql', '-U', 'romd_bootstrap', '-d', 'romd', '-v', 'ON_ERROR_STOP=1',
                                  '-At', '-c', query, capture=True).stdout.decode().split()
        require(rows == [f'Chd|t|{self.settings.recipe}'], f'expected one retained, preferred CHD created with the current recipe; observed {rows}')
        self.wait_for_released_custody()
        health = (self.stack.inspect('romd-worker')['State'].get('Health') or {}).get('Status')
        require(health == 'healthy', f'the restarted worker is {health}')
        self.record['recovery'].update(outcome=job.get('outcome'), phase=job.get('phase'), representations=rows,
                                       custodyReleased=True)
        self.step('recovered: Completed, one preferred CHD, staging and media holds released')

    # -- delivery

    def admin_formats(self):
        rom = self.api.json(ADMIN_HOST, f'/api/roms/{self.rom_id}')
        return rom, {entry.get('format'): entry for entry in rom.get('formats') or []}

    def consumer_formats(self):
        title = self.api.json(CONSUMER_HOST, f'/api/titles/{self.title_id}')
        releases = [release for release in paged_items(
            self.api, f'/api/titles/{self.title_id}/releases', host=CONSUMER_HOST) if release.get('isComplete')]
        releases.sort(key=lambda release: release.get('id') != title.get('defaultReleaseId'))
        target = getattr(self, '_consumer_member', None)
        for release in releases:
            manifest = self.api.json(CONSUMER_HOST, f"/api/releases/{release['id']}/manifest", method='POST')
            for item in manifest.get('items') or []:
                formats = {entry.get('format'): entry for entry in item.get('formats') or []}
                member = (release['id'], item.get('relativePath'))
                # Unsupported formats intentionally omit content hashes. Locate the
                # same member captured while the baseline decoder could serve it.
                if target is not None:
                    if member == target:
                        return release['id'], formats
                elif (formats.get('original') or {}).get('sha256') == self.subject.identity['sha256']:
                    require(isinstance(member[1], str) and member[1], 'consumer member lacks its relative path')
                    self._consumer_member = member
                    return release['id'], formats
        raise HarnessError('no complete consumer release lists the captured disc member' if target else
                           'no complete consumer release lists the uploaded disc')

    def assert_serving_admission(self):
        rom, formats = self.admin_formats()
        chd, original = formats.get('chd') or {}, formats.get('original') or {}
        require(chd.get('state') == 'available' and str(chd.get('filename', '')).endswith('.chd') and chd.get('sha256')
                and 'pcsx2' in (chd.get('runtimeApplicability') or []), f'admin does not serve the CHD: {chd}')
        require(original.get('state') == 'available' and original.get('sha256') == self.subject.identity['sha256']
                and int(original.get('sizeBytes', -1)) == self.subject.identity['size'], f'admin original is {original}')
        self.chd = {'sha256': chd['sha256'], 'sizeBytes': int(chd['sizeBytes']), 'filename': chd['filename'],
                    'profileVersion': chd.get('profileVersion'),
                    'recipeMeasurement': recipe_measurement(self.settings.recipe, self.subject.role, chd['sha256'],
                                                            int(chd['sizeBytes']), self.settings.synthetic)}
        self.title_id = next((match['titleId'] for match in rom.get('matches') or [] if match.get('titleId')), None)
        require(self.title_id, 'the ROM matched no catalog title')
        self.step('publishing a library for the consumer host (this revokes the admin session)')
        status, _, body = self.api.call(ADMIN_HOST, '/api/libraries', expect=(201,), payload={
            'name': f'CHD e2e {self.run_id}', 'configuration': {
                'includeUntrackedTitles': True, 'contentRatingPolicy': {'unknownRatingPolicy': 'Allow'}}})
        library_id = json.loads(body)['id']
        self.api.call(ADMIN_HOST, f'/api/libraries/{library_id}/materialize', method='POST', expect=(202,))
        user_id = self.api.json(ADMIN_HOST, '/api/me')['id']
        self.api.call(ADMIN_HOST, f'/api/users/{user_id}/library', method='PUT', payload={'libraryId': library_id})
        self.api.forget(ADMIN_HOST)
        deadline = time.monotonic() + 600
        while True:
            status, _, _ = self.api.call(CONSUMER_HOST, f'/api/titles/{self.title_id}', expect=None)
            if status == 200:
                break
            # The library is unavailable until its first publication completes.
            require(status in (403, 404, 503) and time.monotonic() < deadline, f'consumer title lookup returned {status}')
            time.sleep(2)
        release_id, consumer = self.consumer_formats()
        consumer_chd = consumer.get('chd') or {}
        require(consumer_chd.get('state') == 'available' and consumer_chd.get('contentGrant')
                and consumer_chd.get('sha256') == self.chd['sha256'] and int(consumer_chd.get('sizeBytes', -1)) == self.chd['sizeBytes'],
                f'the consumer manifest does not grant the published CHD: {consumer_chd}')
        require((consumer.get('original') or {}).get('state') == 'available', 'the consumer manifest lacks the original')
        self.record['chd'] = self.chd
        # Evidence that the kill interrupted the CAS staging copy rather than following a committed publication.
        if self.settings.checkpoint == 'publishing':
            staged = self.record['checkpoint'].get('stagingAfterKill') or {}
            grown = staged.get(self.record['checkpoint'].get('stagingFile'))
            self.record['checkpoint']['copyInterrupted'] = grown is not None and grown < self.chd['sizeBytes']
        self.record['servingAdmission'] = {'romd-admin': 'GET /api/roms/{id}: chd available',
                                           'romd-consumer': f'release {release_id} manifest: chd available with grant',
                                           'romd-worker': WORKER_READ_LIMITATION}
        self.step(f"serving admission confirmed on admin and consumer for CHD {self.chd['sha256']}")

    def verify_download(self, label, observed, expect_original):
        if expect_original:
            problems = identity_mismatches(f'{label} vs fixture', self.subject.identity, observed)
            problems += identity_mismatches(f'{label} vs DAT', self.subject.dat_identity, observed, ('size', 'sha1', 'md5', 'crc32'))
        else:
            problems = identity_mismatches(f'{label} vs published CHD', {'size': self.chd['sizeBytes'], 'sha256': self.chd['sha256']},
                                           observed, ('size', 'sha256'))
        require(not problems, '; '.join(problems))
        directives = {token.strip().lower() for token in str(observed.get('cacheControl') or '').split(',')}
        require({'private', 'no-store'} <= directives, f'{label} Cache-Control is {observed.get("cacheControl")}')
        return {key: observed[key] for key in ('host', 'size', 'sha256', 'sha1', 'md5', 'crc32', 'seconds', 'megabytesPerSecond')}

    def download_formats(self):
        downloads = {}
        for kind in ('original', 'chd'):
            self.step(f'streaming the admin {kind} download through SHA-256/SHA-1/MD5/CRC32')
            ticket = self.api.json(ADMIN_HOST, '/api/download-tickets', method='POST',
                                   payload={'target': 'Rom', 'id': self.rom_id, 'format': kind}, expect=(201,))
            capability = ticket.get('downloadUrl') if isinstance(ticket, dict) else None
            require(isinstance(capability, str) and re.fullmatch(r'/api/download-tickets/[A-Za-z0-9_-]+', capability),
                    'admin download ticket returned an invalid capability address')
            observed = self.api.download(ADMIN_HOST, capability, authorized=False)
            downloads[f'admin-{kind}'] = self.verify_download(f'admin {kind}', observed, kind == 'original')
        for kind in ('original', 'chd'):
            self.step(f'streaming the consumer {kind} content grant')
            _, formats = self.consumer_formats()  # grants are short-lived; issue a fresh manifest per download
            grant = (formats.get(kind) or {}).get('contentGrant') or {}
            require(str(grant.get('downloadUrl', '')).startswith('/delivery/content/'), f'unexpected {kind} grant {grant}')
            observed = self.api.download(CONSUMER_HOST, grant['downloadUrl'], authorized=False)
            downloads[f'consumer-{kind}'] = self.verify_download(f'consumer {kind}', observed, kind == 'original')
        self.record['downloads'] = downloads

    def assert_malformed_isolation(self):
        self.step('uploading a malformed CHD beside a valid sibling')
        before = self.stack.inspect('romd-worker')['State']
        job_id = upload(self.api, '/api/upload?' + urlencode({'allowUnidentified': 'true', 'archiveOnly': 'true'}),
                        f'/e2e/inputs/{MALFORMED_ARCHIVE}', 600)
        job = wait_for_job(self.api, job_id, 900)
        items = job_items(self.api, job_id)
        problems = malformed_isolation_failures(items)
        require(not problems, '; '.join(problems))
        after = self.stack.inspect('romd-worker')['State']
        require(after.get('StartedAt') == before.get('StartedAt') and (after.get('Health') or {}).get('Status') == 'healthy',
                'the worker restarted or became unhealthy while isolating the malformed CHD')
        self.wait_for_released_custody(120)
        poison = next(item for item in items if str(item.get('fileName', '')).endswith(POISON_NAME))
        self.record['malformed'] = {'jobId': job_id, 'jobPhase': job.get('phase'), 'poisonOutcome': poison.get('outcome'),
                                    'poisonError': tail(poison.get('error'), 300), 'siblingOutcome': 'ingested',
                                    'workerHealthy': True, 'runRecords': []}
        self.step('malformed CHD failed alone; sibling ingested; worker healthy; no run records')

    # -- teardown

    def teardown(self, failed):
        """Never raises: every step is attempted and failures are returned for the caller to report."""
        errors, cleanup, stack_down = [], {}, True
        if self.sampler is not None:
            self.record['disk'] = self.sampler.summary()
        if self.stack is not None and self.stack.started:
            if failed or not self.settings.synthetic:
                try:
                    cleanup['logs'] = self.save_logs(failed)
                except (OSError, HarnessError) as error:
                    cleanup['logs'] = f'failed: {error}'
            try:
                result = self.stack.compose('down', '-v', '--remove-orphans', capture=True, check=False, timeout=900)
                down_error = tail(result.stderr, 300) if result.returncode else None
            except HarnessError as error:
                down_error = str(error)
            cleanup['composeDown'] = f'failed: {down_error}' if down_error else 'ok'
            if down_error:
                stack_down = False
                errors.append(f'docker compose -p {self.run_id} down -v --remove-orphans failed; resources may remain')
        if not self.created:
            cleanup['runFolder'] = 'not created by this run; left untouched'
        elif not stack_down:
            # Containers may still hold the binds, and the overlay is needed to finish the teardown by hand.
            cleanup['runFolder'] = 'kept because the stack may still be running'
            errors.append(f'run folder {self.folder} was kept because the stack was not removed')
        elif self.folder.exists():
            try:
                self.delete_run_folder()
                cleanup['runFolder'] = 'deleted'
            except (OSError, HarnessError) as error:
                cleanup['runFolder'] = f'failed: {error}'
                errors.append(f'run folder {self.folder} was not deleted: {error}')
        if cleanup.get('runFolder') == 'deleted' and sys.platform == 'darwin':
            try:
                cleanup['guestFileRelease'] = release_rancher_bind_mount_files()
            except (OSError, HarnessError) as error:
                cleanup['guestFileRelease'] = {'status': 'failed', 'reason': str(error)}
            if cleanup['guestFileRelease']['status'] == 'failed':
                # Until the guest drops their inodes, macOS keeps the deleted run folder's blocks allocated.
                # Without rdctl (not Rancher Desktop) the release is skipped silently and recorded.
                print(f"WARNING: guest file release {cleanup['guestFileRelease']['status']}: "
                      f"{cleanup['guestFileRelease'].get('reason')}; release by hand: "
                      "rdctl shell sudo sh -c 'sync && echo 2 > /proc/sys/vm/drop_caches'", file=sys.stderr)
        if not self.settings.synthetic and sys.platform == 'darwin':
            try:
                cleanup['vmTrim'] = trim_rancher_vm()
            except (OSError, HarnessError) as error:
                cleanup['vmTrim'] = {'status': 'failed', 'reason': str(error)}
            if cleanup['vmTrim']['status'] != 'trimmed':
                # Only reclaims macOS disk; the next disc's space preflight still guards, so it must not stop that disc.
                print(f"WARNING: Docker VM trim {cleanup['vmTrim']['status']}: {cleanup['vmTrim'].get('reason')}; "
                      'trim by hand: rdctl shell df -P /var/lib/docker, then rdctl shell sudo fstrim -v <that mountpoint>',
                      file=sys.stderr)
        self.record['cleanup'] = cleanup
        if errors:
            print(self.manual_cleanup(), file=sys.stderr)
        return '; '.join(errors) or None

    def manual_cleanup(self):
        # Label filters need no compose files or interpolated secrets; only this run's project is selected.
        label = f'label=com.docker.compose.project={self.run_id}'
        lines = [f'Cleanup for {self.run_id} did not finish. Finish it by hand:',
                 f'  docker ps -aq --filter {label} | xargs docker rm -f',
                 f'  docker volume ls -q --filter {label} | xargs docker volume rm',
                 f'  docker network ls -q --filter {label} | xargs docker network rm']
        if self.created:
            lines.append(f"  rm -rf '{self.folder}'   # only after the stack is gone; may need the app user's ownership")
        if sys.platform == 'darwin':
            lines.append("  rdctl shell sudo sh -c 'sync && echo 2 > /proc/sys/vm/drop_caches'   # after the folder is gone")
        if not self.settings.synthetic and sys.platform == 'darwin':
            lines.append('  rdctl shell df -P /var/lib/docker   # then: rdctl shell sudo fstrim -v <that mountpoint>')
        return '\n'.join(lines)

    def save_logs(self, failed):
        # Bounded so a wedged daemon cannot stall the teardown that follows.
        result = self.stack.compose('logs', '--no-color', '--timestamps', '--tail', '3000', capture=True, check=False, timeout=120)
        output = (result.stdout or b'')[-(8 << 20):]
        if failed:
            print('\n'.join(output.decode('utf-8', 'replace').splitlines()[-150:]), file=sys.stderr)
        if self.settings.evidence_directory is None:
            return 'printed' if failed else 'not retained'
        self.settings.evidence_directory.mkdir(parents=True, exist_ok=True)
        path = self.settings.evidence_directory / f'{self.run_id}.log'
        path.write_bytes(output)
        return str(path)

    def delete_run_folder(self):
        require(self.created, f'refusing to delete {self.folder}: this run did not create it')
        folder = require_disposable(self.settings.root, self.folder)
        try:
            shutil.rmtree(folder)
        except PermissionError:
            # On Linux the app user (UID 1654) owns what the hosts wrote; let that user remove it.
            docker('run', '--rm', '--network', 'none', '--mount', f'type=bind,source={self.data},target=/cleanup',
                   '--entrypoint', 'find', self.settings.refs['worker'], '/cleanup', '-mindepth', '1', '-delete', timeout=900)
            shutil.rmtree(folder)


# ---------------------------------------------------------------- subjects and CLI

def real_subject(fixtures, disc):
    manifest = load_manifest(fixtures)
    manifest_sha256 = hashlib.sha256((fixtures / 'manifest.json').read_bytes()).hexdigest()
    entry = manifest_entry(manifest, 'files', disc.role)
    require(valid_identity(entry), f'manifest.json {disc.role} entry lacks a complete size/digest identity')
    iso_path, iso_container = fixture_file(fixtures, entry['path'])
    dat = manifest_entry(manifest, 'dats', 'ps2-redump-dat')
    dat_path, dat_container = fixture_file(fixtures, dat['path'])
    require(os.stat(iso_path).st_size == int(entry['size']), f'{entry["path"]} size differs from manifest.json')
    require(hashlib.sha256(dat_path.read_bytes()).hexdigest() == str(dat.get('sha256', '')).lower(),
            'the PS2 DAT differs from its manifest.json SHA-256')
    game = (dat.get('discEntries') or {}).get(disc.role)
    require(isinstance(game, str) and game, f'manifest.json names no DAT entry for {disc.role}')
    rom = dat_rom(dat_path, game)
    problems = identity_mismatches(f'{game} DAT entry', entry, rom, ('size', 'sha1', 'md5', 'crc32'))
    require(not problems, '; '.join(problems))
    print(f'Hashing fixture {entry["path"]} against manifest.json', flush=True)
    problems = identity_mismatches(entry['path'], entry, digest_file(iso_path))
    require(not problems, '; '.join(problems))
    provenance = {'fixturePath': entry['path'], 'manifestSha256': manifest_sha256, 'datPath': dat['path'],
                  'datSha256': str(dat['sha256']).lower(), 'datGame': game}
    return Subject(disc.key, disc.role, disc.media, disc.redump_id, normalize_identity(entry), normalize_identity(rom),
                   dat_container, iso_container, provenance)


def synthetic_subject():
    return Subject('synthetic', 'dvd5', 'Dvd5', SYNTHETIC_REDUMP_ID, {}, {})


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--synthetic', action='store_true', help='generated inputs and a mock redump.info; no fixtures')
    parser.add_argument('--checkpoint', choices=('publishing', 'encoding'), default=CHECKPOINT,
                        help='crash during CAS publication, or during a live encode followed by container recreation')
    parser.add_argument('--disc', action='append', choices=sorted(DISCS),
                        help='real disc to run, in order (repeatable; default: ffx then gt4)')
    parser.add_argument('--https-bind', default='127.0.0.1', help='IPv4 loopback address for the disposable ingress publication')
    parser.add_argument('--image-tag', help='use existing romd-{admin,consumer,worker,player}:TAG images instead of building')
    parser.add_argument('--image-defaults', action='store_true',
                        help='require CHD activation from the images, without native settings in the generated overlay')
    parser.add_argument('--keep-images', action='store_true', help='keep images this invocation built')
    parser.add_argument('--evidence', help='absolute JSON summary path (default: $ROMD_E2E_ROOT/evidence/<invocation>.json)')
    parser.add_argument('--synthetic-mib', type=int, default=1024,
                        help='synthetic ISO size; incompressible padding keeps the Publishing copy observable (default 1024)')
    parser.add_argument('--convergence-timeout', type=int, metavar='MINUTES',
                        help='limit for reaching the checkpoint and again for recovery (default: 240 real, 45 synthetic)')
    parser.add_argument('--upload-timeout', type=int, metavar='MINUTES', help='ISO upload and ingestion limit (default: 90 real, 20 synthetic)')
    args = parser.parse_args(argv)
    try:
        address = ipaddress.IPv4Address(args.https_bind)
        if not address.is_loopback:
            raise ValueError()
    except ValueError:
        parser.error('--https-bind must be an IPv4 loopback address')
    if args.synthetic and args.disc:
        parser.error('--disc applies to real-disc runs only')
    if not 64 <= args.synthetic_mib <= 4096:
        parser.error('--synthetic-mib must be between 64 and 4096')
    if args.image_tag and not re.fullmatch(r'[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}', args.image_tag):
        parser.error('--image-tag must be a valid Docker tag')
    return args


def print_summary(summary):
    print('\nPS2 DVD CHD end-to-end summary', flush=True)
    print(f"  status: {summary['status']}  mode: {summary['mode']}  checkpoint: {summary['checkpoint']['name']}")
    source = summary.get('source') or {}
    print(f"  source: {source.get('revision')}{' (dirty working tree)' if source.get('dirty') else ''}  recipe: {summary.get('expectedRecipe')}")
    for name, image in (summary.get('images') or {}).items():
        print(f"  image {name}: {image.get('ref')} {image.get('id')}")
    for run in summary.get('runs', []):
        chd, disk = run.get('chd') or {}, run.get('disk') or {}
        print(f"  {run['disc']} [{run['status']}] run {run['runId']}")
        if chd:
            print(f"    CHD sha256 {chd['sha256']} size {chd['sizeBytes']}; recipe measurement: {chd['recipeMeasurement']}")
        print(f"    encode wall seconds by attempt: {run.get('encodeWallSeconds')}; peak disk bytes: {disk.get('peakBytes')}")
        for entry in run.get('timeline') or []:
            print(f"      attempt {entry['attempt']} {entry['phase']:<11} +{entry['elapsedSeconds']}s {entry['observedUtc']}")
        if run.get('error'):
            print(f"    error: {run['error']}")
    if summary.get('evidence'):
        print(f"  evidence: {summary['evidence']}")


def main(argv=None):
    args = parse_args(argv)
    invocation = f'{utc_now():%Y%m%dt%H%M%S}-{secrets.token_hex(3)}'
    environ, temporary_root, fixtures = dict(os.environ), None, None
    try:
        if args.synthetic and not environ.get('ROMD_E2E_ROOT'):
            temporary_root = root = Path(tempfile.mkdtemp(prefix='romd-e2e-chd-')).resolve()
        elif args.synthetic:
            root = canonical_directory(environ, 'ROMD_E2E_ROOT')
            require(not within(root, ROOT) and not within(ROOT, root), 'ROMD_E2E_ROOT must be outside the repository checkout.')
        else:
            fixtures, root = real_paths(environ)
        evidence = resolve_evidence(args.evidence, None if temporary_root else root, invocation, fixtures)
    except HarnessError as error:
        print(f'Refusing to run: {error}', file=sys.stderr)
        if temporary_root is not None:
            shutil.rmtree(temporary_root, ignore_errors=True)
        return 2
    summary = {'schema': 'romd-e2e-chd/v1', 'mode': 'synthetic' if args.synthetic else 'real', 'invocation': invocation,
               'checkpoint': {'name': args.checkpoint, 'definition': checkpoint_description(args.checkpoint)}, 'startedUtc': iso(utc_now()),
               'status': 'running', 'runs': [], 'evidence': str(evidence) if evidence else None}
    built, exit_code = [], 1
    print(f'Checkpoint: {args.checkpoint} ({checkpoint_description(args.checkpoint)})', flush=True)
    install_interrupt_handlers()
    try:
        for tool in ('docker', 'openssl', 'git'):
            require(shutil.which(tool), f'{tool} is required on PATH')
        docker('version', '--format', '{{.Server.Version}}', timeout=60)
        summary['environment'] = docker_environment()
        summary['source'] = source_revision()
        recipe = summary['expectedRecipe'] = expected_recipe(RECIPE_SOURCE.read_text(encoding='utf-8'))
        runs_directory(root)
        refs = image_refs(args.image_tag or f'e2e-{invocation}')
        if not args.image_tag:
            build_images(refs, f'romd-e2e-build-{invocation}', built)
        summary['images'] = {f'romd-{name}': {'ref': ref, 'id': image_id(ref)} for name, ref in refs.items()}
        require(all(image['id'] for image in summary['images'].values()), f'missing images: {summary["images"]}')
        synthetic_bytes = args.synthetic_mib << 20
        settings = Settings(args.synthetic, root, fixtures, evidence.parent if evidence else None, refs, recipe,
                            environ.get('ROMD_NGINX_IMAGE', test_environment()['ROMD_NGINX_IMAGE']), synthetic_bytes,
                            60 * (args.convergence_timeout or (45 if args.synthetic else 240)),
                            60 * (args.upload_timeout or (20 if args.synthetic else 90)), args.checkpoint, args.https_bind, args.image_defaults)
        for label in ['synthetic'] if args.synthetic else (args.disc or ['ffx', 'gt4']):
            free = shutil.disk_usage(root).free
            if args.synthetic:
                needed, basis = synthetic_required_bytes(synthetic_bytes), 'synthetic estimate'
            else:
                measured = prior_peak(settings.evidence_directory, label)
                needed, basis = required_free_bytes(measured), 'measured peak' if measured else 'assumed 30 GiB peak'
            held = ("; on macOS the Docker VM may still hold deleted run files: rdctl shell sudo sh -c "
                    "'sync && echo 2 > /proc/sys/vm/drop_caches'") if sys.platform == 'darwin' else ''
            require(free >= needed, f'{root} has {free} bytes free; {label} needs {needed} ({basis} + headroom){held}')
            subject = synthetic_subject() if args.synthetic else real_subject(fixtures, DISCS[label])
            qualification = Qualification(settings, subject)
            summary['runs'].append(qualification.record)
            qualification.record['spacePreflight'] = {'freeBytes': free, 'requiredBytes': needed, 'basis': basis}
            qualification.run()
            write_evidence(summary, evidence)  # a later disc's hard failure keeps this disc's record
        summary['status'], exit_code = 'passed', 0
    except KeyboardInterrupt:
        summary.update(status='failed', error='interrupted')
        exit_code = 130
    except Exception as error:
        summary.update(status='failed', error=f'{type(error).__name__}: {error}')
        if not isinstance(error, HarnessError):
            traceback.print_exc()
        print(f'FAILED: {error}', file=sys.stderr)
    finally:
        with interrupts_ignored():
            finish(summary, args, built, temporary_root, evidence)
    return exit_code


def finish(summary, args, built, temporary_root, evidence):
    if built and not args.keep_images:
        try:
            removed = docker('image', 'rm', *built, check=False, timeout=600).returncode == 0
        except HarnessError:
            removed = False
        summary['imageRemoval'] = 'ok' if removed else 'failed'
        if not removed:
            print('Image removal failed; remove them by hand: docker image rm ' + ' '.join(built), file=sys.stderr)
    if temporary_root is not None:
        kept = [run['runId'] for run in summary['runs'] if str((run.get('cleanup') or {}).get('runFolder', '')).startswith('kept')]
        if kept:
            print(f'Kept {temporary_root}: the stack for {", ".join(kept)} may still be running.', file=sys.stderr)
        else:
            shutil.rmtree(temporary_root, ignore_errors=True)
    summary['finishedUtc'] = iso(utc_now())
    write_evidence(summary, evidence)
    print_summary(summary)


def write_evidence(summary, evidence):
    if evidence is not None:
        evidence.parent.mkdir(parents=True, exist_ok=True)
        evidence.write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    sys.exit(main())
