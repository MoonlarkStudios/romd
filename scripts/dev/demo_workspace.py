#!/usr/bin/env python3
"""Prepare a private, hash-locked .demo workspace from an explicit curation profile."""
import argparse
from contextlib import contextmanager
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import stat
import subprocess
import tarfile
import tempfile
import time
from urllib.request import urlopen
import uuid
import xml.etree.ElementTree as ET
import zipfile

import manage

MAX_FILE = 32 * 1024 * 1024
MAX_TOTAL = 128 * 1024 * 1024
PROFILE = 'profile.json'  # Private curation profile inside the ignored workspace.
EXAMPLE = Path(__file__).with_name('demo-profile.example.json')
PROFILE_SCHEMA = 1


def require(condition, message):
    if not condition:
        raise manage.SetupError(message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def workspace(root=manage.ROOT):
    path = root / '.demo'
    require(not path.is_symlink(), '.demo must be a directory owned by this checkout, not a link.')
    return path


def checked_path(root, relative):
    value = PurePosixPath(relative)
    require(not value.is_absolute() and value.parts and '..' not in value.parts and '\\' not in relative,
            'Demo paths must be relative and remain inside their workspace.')
    path = root
    for part in value.parts:
        path = path / part
        require(not path.is_symlink(), 'Demo sources and assets must not be symbolic links.')
    require(path.resolve().is_relative_to(root.resolve()), 'A demo asset escaped its workspace.')
    return path


def read(root, relative, expected=None):
    path = checked_path(root, relative)
    require(path.is_file() and 0 < path.stat().st_size <= MAX_FILE, f'Missing, empty or oversized demo asset: {relative}')
    with path.open('rb') as handle:
        data = handle.read(MAX_FILE + 1)
    require(0 < len(data) <= MAX_FILE, f'Demo asset exceeds 32 MiB: {relative}')
    require(expected is None or sha(data) == expected, f'Demo asset hash mismatch: {relative}')
    return data


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    with temporary.open('x', encoding='utf-8') as handle:
        os.fchmod(handle.fileno(), 0o600)
        json.dump(value, handle, indent=2)
        handle.write('\n')
    temporary.replace(path)


def retain(root, relative, data):
    path = checked_path(root, relative)
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    if path.exists():
        require(read(root, relative) == data, f'Refusing to overwrite changed demo asset: {relative}')
    else:
        # Publish complete bytes only; os.link never replaces an object that appeared concurrently.
        temporary = path.with_name('.' + path.name + '.' + uuid.uuid4().hex + '.tmp')
        try:
            with temporary.open('xb') as handle:
                os.fchmod(handle.fileno(), 0o600)
                handle.write(data)
                handle.flush()
                os.fsync(handle.fileno())
            os.link(temporary, path)
        finally:
            temporary.unlink(missing_ok=True)
    return relative


def cartridge(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        files = [item for item in archive.infolist() if not item.is_dir()]
        require(len(files) == 1, 'A curated cartridge ZIP must have exactly one file.')
        item = files[0]
        require(stat.S_IFMT(item.external_attr >> 16) in (0, stat.S_IFREG) and
                0 < item.file_size <= MAX_FILE and PurePosixPath(item.filename).suffix.lower() in
                ('.gb', '.gbc', '.gba', '.nes', '.sfc', '.md'), 'Unsupported curated cartridge member.')
        require('\\' not in item.filename and '..' not in PurePosixPath(item.filename).parts and
                not PurePosixPath(item.filename).is_absolute(), 'Curated ZIP member paths must be portable.')
        return PurePosixPath(item.filename).name, archive.read(item)


def selection(source, profile):
    """Exact recipe entries only: no globbing, recursive discovery or first-file fallback."""
    source = source.expanduser().resolve(strict=True)
    files, total = [], 0
    for kind, recipes in (('rom', profile['roms']), ('bios', profile['bios'])):
        for recipe in recipes:
            data = read(source, recipe['source'])
            filename, payload = cartridge(data) if kind == 'rom' else (Path(recipe['source']).name, data)
            if kind == 'bios':
                require(len(data) == recipe['size'] and hashlib.md5(data).hexdigest() == recipe['md5'],
                        f'BIOS does not match its documented size/MD5: {filename}')
            total += len(payload)
            require(total <= MAX_TOTAL, 'Curated demo payloads exceed 128 MiB.')
            files.append(dict(kind=kind, system=recipe['system'], source=recipe['source'], filename=filename,
                              purpose=recipe['purpose'], sha256=sha(data), payloadSha256=sha(payload),
                              payloadSha1=hashlib.sha1(payload).hexdigest(), size=len(data), payloadSize=len(payload),
                              data=data))
    return files


def validate_profile(profile):
    """Structural checks only; selection() still verifies every listed file and BIOS size/MD5."""
    def text(row, key):
        return isinstance(row.get(key), str) and row[key].strip()
    require(isinstance(profile, dict) and profile.get('schemaVersion') == PROFILE_SCHEMA,
            f'Unsupported demo profile; expected schemaVersion {PROFILE_SCHEMA} (see {EXAMPLE.name}).')
    require(text(profile, 'name') and text(profile, 'catalogSite') and profile['catalogSite'].startswith('https://'),
            'A demo profile needs a name and an https catalogSite.')
    roms, bios = profile.get('roms'), profile.get('bios')
    require(isinstance(roms, list) and roms and isinstance(bios, list),
            'A demo profile needs a non-empty roms list and a bios list (which may be empty).')
    for row in roms + bios:
        require(isinstance(row, dict) and all(text(row, key) for key in ('system', 'source', 'purpose')),
                'Every demo profile entry needs system, source and purpose.')
    for row in roms:
        require(len(PurePosixPath(row['source']).parts) >= 3 and row['source'].startswith('roms/'),
                'ROM sources must be roms/<complete DAT name>/<archive>.zip.')
    for row in bios:
        require(isinstance(row.get('size'), int) and row['size'] > 0 and isinstance(row.get('md5'), str) and
                re.fullmatch(r'[0-9a-f]{32}', row['md5']), 'Every BIOS entry needs its documented size and MD5.')
    return profile


def read_profile(root, path=None, *, required=True):
    """Load an explicit --profile PATH or the workspace's own profile.json; never a tracked default."""
    path = Path(path).expanduser() if path is not None else workspace(root) / PROFILE
    if not path.is_file():
        require(not required, f'No demo profile at {path}. Copy {EXAMPLE.relative_to(manage.ROOT)} to '
                f'.demo/{PROFILE}, list files from your own collection, or pass --profile PATH; '
                'see docs/development/demo-data.md.')
        return None, None
    data = path.read_bytes()
    require(len(data) <= 1024 * 1024, 'The demo profile exceeds 1 MiB.')
    return validate_profile(json.loads(data)), data


def reader_pins(root):
    """Use the reviewed production reader pins rather than a sibling checkout's mutable HEAD."""
    text = (root / 'Dockerfile').read_text()
    source = re.search(r'ADD --checksum=sha256:([a-f0-9]{64}) (https://codeload.github.com/MoonlarkStudios/romd-dat-catalogs/tar.gz/([a-f0-9]{40}))', text)
    go = re.search(r'FROM --platform=\$BUILDPLATFORM golang:([\d.]+)-bookworm', text)
    trust = re.search(r"echo '([a-f0-9]{64})  trust/1.root.json'", text)
    require(source and go and trust, 'Cannot resolve the reviewed catalog-reader pins from Dockerfile.')
    return dict(archiveSha256=source[1], sourceUrl=source[2], sourceCommit=source[3],
                goVersion=go[1], rootSha256=trust[1])


def command(root, name, arguments, *, environment=None, timeout=300):
    directory = checked_path(workspace(root), 'logs')
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    with (directory / (name + '.log')).open('w') as log:
        result = subprocess.run(arguments, cwd=root, env=environment, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
    require(result.returncode == 0, f'{name} failed; inspect .demo/logs/{name}.log. Existing assets and TUF cache were preserved.')


def reader(root):
    base, pins = workspace(root), reader_pins(root)
    relative = f'tools/{pins["sourceCommit"]}/{sys_platform()}-{platform.machine()}'
    folder = checked_path(base, relative)
    receipt = folder / 'reader.json'
    if receipt.exists():
        record = json.loads(receipt.read_text())
        require(record['pins'] == pins and record['platform'] == [sys_platform(), platform.machine()],
                'The prepared reader belongs to different pins/platform; prepare a separate workspace.')
        read(base, record['binary'], record['binarySha256'])
        read(base, record['root'], pins['rootSha256'])
        return record
    folder.mkdir(parents=True, exist_ok=True, mode=0o700)
    print('Building the production-pinned signed catalog reader.', flush=True)
    with urlopen(pins['sourceUrl'], timeout=60) as response:
        archive = response.read(16 * 1024 * 1024 + 1)
    require(len(archive) <= 16 * 1024 * 1024 and sha(archive) == pins['archiveSha256'], 'Catalog reader source archive hash mismatch.')
    with tempfile.TemporaryDirectory(prefix='build-', dir=folder) as temporary:
        temporary = Path(temporary)
        with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
            require(all(not m.issym() and not m.islnk() for m in tar.getmembers()), 'Unexpected link in pinned reader source.')
            tar.extractall(temporary, filter='data')
        directories = list(temporary.iterdir())
        require(len(directories) == 1 and directories[0].is_dir(), 'Unexpected reader archive layout.')
        source = directories[0]
        trust = (source / 'trust/1.root.json').read_bytes()
        require(sha(trust) == pins['rootSha256'], 'Independently pinned public trust root mismatch.')
        environment = {key: value for key, value in os.environ.items() if key not in ('GOOS', 'GOARCH', 'GOFLAGS', 'GOWORK')}
        environment.update(GOTOOLCHAIN='local', CGO_ENABLED='0')
        binary = folder / 'catalog-reader'
        command(root, 'reader-build', ['mise', 'exec', 'go@' + pins['goVersion'], '--', 'go', '-C', str(source),
                'build', '-trimpath', '-o', str(binary), './cmd/distribution'], environment=environment)
        binary.chmod(0o700)
        retain(base, relative + '/1.root.json', trust)
    record = dict(pins=pins, platform=[sys_platform(), platform.machine()], binary=relative + '/catalog-reader',
                  binarySha256=sha(binary.read_bytes()), root=relative + '/1.root.json')
    write_json(receipt, record)
    return record


def sys_platform():
    import sys
    return sys.platform


def catalogs(root, profile):
    base, tool = workspace(root), reader(root)
    result = []
    for system in sorted({row['system'] for row in profile['roms']}):
        catalog_id = f'no-intro/{system}/standard'
        expected = next(row['source'].split('/')[1] for row in profile['roms'] if row['system'] == system)
        attempt = checked_path(base, 'catalog-attempts/' + uuid.uuid4().hex)
        attempt.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        print('Authenticating complete DAT ' + catalog_id, flush=True)
        command(root, 'catalog-' + system, [str(base / tool['binary']), 'candidate', '--root', str(base / tool['root']),
                '--site', profile['catalogSite'], '--cache', str(base / 'catalog-cache'), '--out', str(attempt),
                '--catalog', catalog_id, '--name', expected], timeout=180)
        metadata = json.loads((attempt / 'candidate.json').read_text())
        data = read(attempt, 'candidate.dat', metadata['sha256'])
        require(metadata['catalogId'] == catalog_id and metadata['systemId'] == system and
                metadata['name'] == expected and metadata['bytes'] == len(data), 'Signed DAT identity mismatch.')
        relative = f'dats/{system}/{sha(data)}.dat'
        retain(base, relative, data)
        record = dict(system=system, path=relative, sha256=sha(data), name=expected,
                      catalogId=catalog_id, authentication='production-pinned Go/TUF reader',
                      receipt=metadata, fetchedAt=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()))
        write_json((base / relative).with_suffix('.json'), record)
        result.append(record)
        shutil.rmtree(attempt)  # Only this reader invocation's successful output.
    return result, tool


def load(root=manage.ROOT, manifest=None):
    base = workspace(root)
    require(manifest is not None or (base / 'manifest.json').exists(),
            'Prepare .demo first: mise run dev:demo:prepare --from /path/to/collection '
            '(or --from-workspace /other/checkout/.demo).')
    manifest = json.loads(read(base, 'manifest.json')) if manifest is None else manifest
    require(manifest['schemaVersion'] == 2, 'Unsupported .demo manifest; run dev:demo:prepare.')
    definitions = {}
    for record in manifest['catalogs']:
        data = read(base, record['path'], record['sha256'])
        require(ET.fromstring(data).findtext('header/name') == record['name'], 'Prepared DAT header identity mismatch.')
        definitions[record['system']] = record | {'data': data}
    entries, total = [], 0
    matches = {}
    wanted = {(row['system'], row['payloadSha1'], row['payloadSize']) for row in manifest['files'] if row['kind'] == 'rom'}
    for system, definition in definitions.items():
        for _, game in ET.iterparse(io.BytesIO(definition['data']), events=('end',)):
            if game.tag not in ('game', 'machine'):
                continue
            for rom in game.findall('rom'):
                identity = (system, rom.get('sha1', '').lower(), int(rom.get('size', '-1')))
                if identity in wanted:
                    matches.setdefault(identity, set()).add(game.get('name'))
            game.clear()
    for row in manifest['files']:
        data = read(base, row['path'], row['sha256'])
        filename, payload = cartridge(data) if row['kind'] == 'rom' else (row['filename'], data)
        require(sha(payload) == row['payloadSha256'] and len(payload) == row['payloadSize'], 'Prepared cartridge member changed.')
        total += len(payload)
        require(total <= MAX_TOTAL, 'Prepared demo payloads exceed 128 MiB.')
        if row['kind'] == 'rom':
            names = matches.get((row['system'], hashlib.sha1(payload).hexdigest(), len(payload)), set())
            require(len(names) == 1, f'Curated ROM needs exactly one exact-size/SHA-1 DAT match: {row["filename"]}')
            name = next(iter(names))
        else:
            name = '[BIOS] ROMD Demo ' + row['system'].upper() + ' Boot Firmware'
        entries.append(dict(system=row['system'], name=name, filename=filename, data=payload,
                            source=row['path'], present=True, bios=row['kind'] == 'bios',
                            archive=data if row['kind'] == 'rom' else None))
    return manifest, entries, definitions


def prepare(source=None, *, refresh=False, root=manage.ROOT, plan=False, profile=None):
    """--from always needs a profile; otherwise the prepared manifest is the recipe and a profile is optional."""
    base = workspace(root)
    if plan and source is None:
        manifest, _, _ = load(root)
        return manifest
    prepared = (base / 'manifest.json').exists()
    require(source is not None or prepared, 'Initial preparation requires --from /path/to/collection or --from-workspace PATH.')
    recipe, profile_bytes = read_profile(root, profile, required=source is not None or profile is not None)
    if plan:
        files = selection(source, recipe)
        return dict(profile=recipe['name'], files=[{k: v for k, v in row.items() if k != 'data'} for row in files])
    if prepared and not (refresh and source is not None):
        old, _, _ = load(root)
        require(recipe is None or old['profileSha256'] == sha(profile_bytes),
                'Curation profile changed; use --refresh --from /path/to/collection to prepare the new selection.')
    if prepared and not refresh:
        manifest = old
        tool = reader(root)
        if manifest['reader'] != tool:
            # Assets/DAT snapshots are portable; executable tools belong to the
            # current host. Preparing on another OS never resets the TUF cache.
            manifest['reader'] = tool
            write_json(base / 'manifest.json', manifest)
        print('Verified existing .demo workspace; frozen DAT snapshots retained.', flush=True)
        return manifest
    if source is not None:
        files = selection(source, recipe)
        name, digest, site = recipe['name'], sha(profile_bytes), recipe['catalogSite']
    else:
        # Refresh DATs for the already staged files; the manifest records their recipe.
        files = [row | {'data': read(base, row['path'], row['sha256'])} for row in old['files']]
        name, digest, site = old['profile'], old['profileSha256'], old['catalogSite']
    base.mkdir(parents=True, exist_ok=True, mode=0o700)
    base.chmod(0o700)
    for row in files:
        relative = f'{"roms" if row["kind"] == "rom" else "bios"}/{row["system"]}/{row["sha256"][:16]}/{Path(row["source"]).name}'
        retain(base, relative, row.pop('data'))
        row['path'] = relative
    roms = [row for row in files if row['kind'] == 'rom']
    definitions, tool = catalogs(root, dict(roms=roms, catalogSite=site))
    manifest = dict(schemaVersion=2, profile=name, profileSha256=digest,
                    files=files, catalogs=definitions, reader=tool, catalogSite=site)
    # Verify DAT-to-payload coverage before atomically replacing any prior manifest.
    # Immutable objects and the persistent TUF cache survive a failed preparation.
    load(root, manifest)
    write_json(base / 'manifest.json', manifest)
    overview = base / 'README.md'
    if not overview.exists():
        retain(base, 'README.md', b'# Private ROMD demo workspace\n\nSee [the maintained setup guide](../docs/development/demo-data.md).\n'
               b'`profile.json` is the private curation profile. `manifest.json` pins the curated assets\n'
               b'and complete authenticated DATs.\n'
               b'ROMs, BIOS, DATs, tools, TUF cache, fixtures, logs and receipts stay here.\n'
               b'This directory is excluded from Git and container build contexts.\n')
    print(f'Prepared .demo: {len(roms)} curated ROM archives, {len(files) - len(roms)} verified BIOS files, '
          f'{len(definitions)} authenticated complete DATs.', flush=True)
    return manifest


def fingerprint(manifest):
    """Identity of a workspace's declared inputs; the host-local reader record is excluded."""
    value = dict(schemaVersion=manifest['schemaVersion'], profileSha256=manifest['profileSha256'],
                 files=sorted([row['path'], row['sha256']] for row in manifest['files']),
                 catalogs=sorted([row['system'], row['path'], row['sha256']] for row in manifest['catalogs']))
    return sha(json.dumps(value, sort_keys=True).encode())


def copy_source(source, destination_root):
    """Re-verify another checkout's prepared workspace without writing to it."""
    source = Path(os.path.abspath(source))
    require(source.name == '.demo' and not source.is_symlink(),
            'The source must be a checkout\'s prepared .demo directory itself, not a link or another directory.')
    require(source.is_dir() and (source / 'manifest.json').is_file(),
            f'No prepared demo workspace at {source}; prepare it there with dev:demo:prepare --from.')
    target = workspace(destination_root)
    require(source.resolve() != target.resolve() and not (target.exists() and os.path.samefile(source, target)),
            'The source is this checkout\'s own .demo; choose another checkout\'s workspace.')
    manifest, _, _ = load(source.parent)  # Every declared hash plus DAT-to-payload coverage.
    return source, target, manifest


def copy_status(target, manifest):
    """Return 'absent' or 'matching'; refuse to replace a different prepared workspace."""
    if not (target / 'manifest.json').exists():
        return 'absent'
    existing, _, _ = load(target.parent)
    require(fingerprint(existing) == fingerprint(manifest),
            'This checkout already has a different prepared .demo workspace; refusing to overwrite it. '
            'Keep using it, or move it aside explicitly before copying.')
    return 'matching'


def reusable_reader(tool, destination_root):
    """A copied reader is only useful for this checkout's production pins and host platform."""
    return (bool(tool.get('pins')) and tool['pins'] == reader_pins(destination_root) and
            tool.get('platform') == [sys_platform(), platform.machine()])


def copy_plan(source, destination_root=manage.ROOT):
    source, target, manifest = copy_source(source, destination_root)
    return dict(source=str(source), destination=str(target), fingerprint=fingerprint(manifest),
                destinationState=copy_status(target, manifest), profile=manifest['profile'],
                files=len(manifest['files']), catalogs=len(manifest['catalogs']),
                reader='copy verified binary and pinned root' if reusable_reader(manifest['reader'], destination_root)
                else 'not copied; run dev:demo:prepare afterwards to build it',
                notCopied=['state/', 'catalog-cache/', 'catalog-attempts/', 'fixtures/', 'logs/'])


def copy_workspace(source, destination_root):
    """Copy only declared immutable inputs; never copy another checkout's runtime receipts/cache.

    The manifest is written last and verified in place. On failure every file and
    directory created by this call is removed, so no prepared workspace remains; a
    different existing workspace is refused rather than replaced. The TUF cache is
    deliberately not copied: a later refresh here starts from the hash-pinned root.
    """
    source, target, manifest = copy_source(source, destination_root)
    recipe = target / PROFILE
    require(not recipe.exists() or manifest['profileSha256'] == sha(recipe.read_bytes()),
            'The source workspace was prepared from a different curation profile than this checkout\'s '
            '.demo/profile.json; use dev:demo:prepare --from /path/to/collection.')
    if copy_status(target, manifest) == 'matching':
        print('This checkout already has the same verified .demo inputs; nothing copied.', flush=True)
        return manifest
    tool = manifest['reader']
    rows = [(row['path'], row['sha256']) for row in manifest['files'] + manifest['catalogs']]
    reusable = reusable_reader(tool, destination_root)
    if reusable:
        rows += [(tool['binary'], tool['binarySha256']), (tool['root'], tool['pins']['rootSha256'])]
    created, directories = [], []
    try:
        if not target.exists():
            target.mkdir(mode=0o700)
            directories.append(target)
        target.chmod(0o700)
        for relative, expected in rows:
            data = read(source, relative, expected)
            path = checked_path(target, relative)
            directories += [parent for parent in reversed(path.parents)
                            if parent.is_relative_to(target) and parent != target and not parent.exists()]
            if not path.exists():
                created.append(path)
            retain(target, relative, data)
        for record in manifest['catalogs']:
            # Provenance receipts are regenerated from the verified manifest, which embeds them.
            sidecar = checked_path(target, record['path']).with_suffix('.json')
            if not sidecar.exists():
                created.append(sidecar)
                write_json(sidecar, record)
        if reusable:
            (target / tool['binary']).chmod(0o700)
            receipt = (target / tool['binary']).with_name('reader.json')
            if not receipt.exists():
                created.append(receipt)
            write_json(receipt, tool)
        created.append(target / 'manifest.json')
        write_json(target / 'manifest.json', manifest)
        load(destination_root)
    except BaseException:
        for path in reversed(created):
            path.unlink(missing_ok=True)
        for directory in sorted(directories, key=lambda item: len(item.parts), reverse=True):
            try:
                directory.rmdir()
            except OSError:
                pass  # Retain anything another owner placed there.
        raise
    if not reusable:
        print('The source reader belongs to different pins or platform and was not copied; run '
              'mise run dev:demo:prepare to build this checkout\'s reader before subscriptions.', flush=True)
    print(f'Copied verified .demo inputs: {len(manifest["files"])} ROM/BIOS files and {len(manifest["catalogs"])} '
          'DAT snapshots; no seeding receipts or TUF cache were copied.', flush=True)
    return manifest


def subscription_environment(root):
    manifest, _, _ = load(root)
    base, tool = workspace(root), manifest['reader']
    require(tool['pins'] == reader_pins(root) and tool['platform'] == [sys_platform(), platform.machine()],
            'Prepare the reader for this checkout version and host platform before enabling subscriptions.')
    read(base, tool['binary'], tool['binarySha256'])
    read(base, tool['root'], tool['pins']['rootSha256'])
    return dict(DatSubscriptions__Enabled='true', DatSubscriptions__ReaderPath=str(base / tool['binary']),
                DatSubscriptions__RootPath=str(base / tool['root']), DatSubscriptions__Site=manifest['catalogSite'],
                DatSubscriptions__BundlePath='')


@contextmanager
def seed_lock(directory):
    import fcntl
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    with (directory / 'seed.lock').open('a') as handle:
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise manage.SetupError('Another demo seed owns this checkout.') from error
        try:
            yield
        finally:
            fcntl.flock(handle, fcntl.LOCK_UN)



def main(arguments=None, root=manage.ROOT):
    parser = argparse.ArgumentParser(description=__doc__)
    origin = parser.add_mutually_exclusive_group()
    origin.add_argument('--from', dest='source', type=Path, help='One-time explicit source for the curated recipe')
    origin.add_argument('--from-workspace', type=Path,
                        help="Copy another checkout's prepared .demo inputs after re-verifying them (no network)")
    parser.add_argument('--profile', type=Path,
                        help=f'Curation profile (default .demo/{PROFILE}; format: scripts/dev/{EXAMPLE.name})')
    parser.add_argument('--refresh', action='store_true', help='Explicitly replace frozen DAT snapshots after re-verification')
    parser.add_argument('--plan', action='store_true', help='Show the explicit local selection without writing or networking')
    args = parser.parse_args(arguments)
    if args.from_workspace is not None:
        if args.refresh:
            parser.error('--from-workspace copies frozen snapshots; run --refresh separately afterwards.')
        if args.profile is not None:
            parser.error('--from-workspace copies a prepared manifest; --profile applies to --from preparation.')
        if args.plan:
            print(json.dumps(copy_plan(args.from_workspace, root), indent=2))
            return
        target = workspace(root)
        existed = target.exists()
        try:
            with seed_lock(target):
                copy_workspace(args.from_workspace, root)
        except BaseException:
            # Remove only the lock directory this invocation created for a failed copy.
            if not existed and target.is_dir() and {path.name for path in target.iterdir()} <= {'seed.lock'}:
                (target / 'seed.lock').unlink(missing_ok=True)
                target.rmdir()
            raise
        return
    # Serialize preparation with seeding without introducing global ownership.
    if args.plan:
        print(json.dumps(prepare(args.source, refresh=args.refresh, root=root, plan=True, profile=args.profile), indent=2))
    else:
        with seed_lock(workspace(root)):
            prepare(args.source, refresh=args.refresh, root=root, profile=args.profile)


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        raise SystemExit(130)
    except (manage.SetupError, OSError, ValueError, subprocess.TimeoutExpired, zipfile.BadZipFile) as error:
        raise SystemExit(f'Demo preparation failed: {error}')
