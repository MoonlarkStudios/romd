#!/usr/bin/env python3
"""Seed a managed source session through its public APIs; originals remain read-only."""
import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import sys
import time
from urllib.error import HTTPError
from urllib.parse import urlencode, urlsplit
from urllib.request import build_opener, ProxyHandler, Request
import uuid
import xml.etree.ElementTree as ET
import zlib

import manage
import demo_workspace
from demo_workspace import seed_lock
from smoke import authorize, NoRedirect, require

VERSION = 2
PASSWORD = 'RomdDemo123!'


@dataclass(frozen=True)
class Entry:
    system: str
    name: str
    filename: str
    data: bytes
    source: str
    present: bool = True
    bios: bool = False
    archive: bytes | None = None

    def manifest(self):
        return dict(system=self.system, name=self.name, filename=self.filename,
                    source=self.source, present=self.present, bios=self.bios,
                    size=len(self.data), sha256=hashlib.sha256(self.data).hexdigest(),
                    archiveSha256=hashlib.sha256(self.archive).hexdigest() if self.archive else None)


def dataset():
    """Explicit synthetic qualification fixtures; ordinary demo runs load .demo."""
    entries = []
    for system in ('gb', 'gba', 'nes'):
        for index, title in enumerate(('Orbit', 'Harbor', 'Missing')):
            name = f'ROMD Demo {system.upper()} {title} (World)'
            data = hashlib.shake_256(name.encode()).digest(32768)
            entries.append(Entry(system, name, title.lower() + '.bin', data, 'generated', index < 2))
    entries.append(Entry('gb', '[BIOS] ROMD Demo Placeholder', 'demo-bios.bin',
                         b'ROMD synthetic BIOS placeholder\n' * 8, 'generated', bios=True))
    return entries


def fingerprint(entries, catalogs=None):
    value = dict(version=VERSION, entries=[e.manifest() for e in entries],
                 catalogs={key: row['sha256'] for key, row in (catalogs or {}).items()})
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


def dat_bytes(entries, name):
    root = ET.Element('datafile')
    header = ET.SubElement(root, 'header')
    for key, value in dict(name=name, description='ROMD development fixture', version=str(VERSION), author='ROMD').items():
        ET.SubElement(header, key).text = value
    for entry in entries:
        game = ET.SubElement(root, 'game', name=entry.name, **({'isbios': 'yes'} if entry.bios else {}))
        ET.SubElement(game, 'description').text = entry.name
        ET.SubElement(game, 'rom', name=entry.filename, size=str(len(entry.data)),
                      crc=f'{zlib.crc32(entry.data):08x}', md5=hashlib.md5(entry.data).hexdigest(),
                      sha1=hashlib.sha1(entry.data).hexdigest())
    return ET.tostring(root, encoding='utf-8', xml_declaration=True)


def cover(index):
    """Small original geometric PNG; no downloaded artwork or imaging dependency."""
    width, height = 120, 160
    colors = ((30, 90, 170), (130, 45, 140), (15, 125, 100))
    color = colors[index % len(colors)]
    pixels = b''.join(b'\x00' + b''.join(bytes(color if (x // 20 + y // 20) % 2 else (25, 30, 45))
                                        for x in range(width)) for y in range(height))
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0)) +
            chunk(b'IDAT', zlib.compress(pixels)) + chunk(b'IEND', b''))


def library_configuration(title_ids):
    return dict(titleSelectionMode='IncludeOnly', includeTitleIds=sorted(title_ids),
                includeUntrackedTitles=True, showMissingGames=True,
                contentRatingPolicy={'unknownRatingPolicy': 'Allow'})


def managed_session(root):
    record = manage.session_status(root)
    require(record.get('active') and record.get('mode') == 'source' and record.get('state') == 'ready' and
            record.get('checkout') == str(root.resolve()),
            'Start this checkout with mise run dev:demo:serve (or mise run dev) and wait for ready, or add --wait.')
    urls = record.get('urls', {})
    require(set(urls) == set(manage.port_settings('source', root=root)), 'Managed session has unexpected endpoints.')
    for value in urls.values():
        parsed = urlsplit(value)
        require(parsed.scheme == 'http' and parsed.hostname == 'localhost' and parsed.port is not None and
                not parsed.username and not parsed.password and not parsed.path and not parsed.query and not parsed.fragment,
                'Demo data requires the managed loopback source endpoints.')
    return record


WAIT_DEFAULT = 900
START_GRACE = 90
PENDING = ('starting', 'reloading')


def wait_for_session(root, timeout, *, grace=START_GRACE, interval=2.0, clock=time.monotonic, sleep=time.sleep,
                     output=print):
    """Wait for this checkout's own managed session to become ready, failing fast on terminal states.

    An inactive session is tolerated only for a short grace period, because a
    controller launched moments ago still checks tools/containers before it owns
    the session lock. Once seen active, losing the controller fails immediately.
    """
    start = clock()
    seen_active, last, reported = False, None, start
    while True:
        record = manage.session_status(root)
        state, active, elapsed = record.get('state'), record.get('active'), clock() - start
        if active and state == 'ready':
            output(f'Managed session ready after {elapsed:.0f}s.', flush=True)
            return managed_session(root)
        if active:
            seen_active = True
            require(state in PENDING, f'Managed session is {state}; inspect mise run dev:status and its logs, '
                    'fix the cause and restart or save a change before seeding.')
        else:
            require(not seen_active, f'Managed session ended while waiting (state: {state}); inspect mise run dev:status '
                    'and the run logs, then restart mise run dev:demo:serve.')
            require(elapsed < min(grace, timeout), f'No managed session became active for this checkout within '
                    f'{min(grace, timeout)}s (state: {state}). Start mise run dev:demo:serve (or mise run dev) here first.')
        require(elapsed < timeout, f'Managed session was still {state} after {timeout}s; inspect mise run dev:status '
                'and rerun with a longer --wait SECONDS if startup is still progressing.')
        if state != last or clock() - reported >= 30:
            output(f'Waiting for this checkout\'s managed session: {state if active else "not active yet"} '
                   f'({elapsed:.0f}s of {timeout}s).', flush=True)
            last, reported = state, clock()
        sleep(interval)


class ApiError(manage.SetupError):
    def __init__(self, method, path, status):
        safe_path = re.sub(r'(/delivery/(?:bios|content)/)[^/?]+', r'\1<redacted>', path.split('?')[0])
        super().__init__(f'Demo API {method} {safe_path} returned HTTP {status}; inspect host/job logs.')
        self.status = status


class Api:
    def __init__(self, origin, token=None):
        self.origin, self.token = origin, token
        self.opener = build_opener(NoRedirect, ProxyHandler({}))

    def call(self, path, method='GET', value=None, file=None, raw=False, *, if_match=None, with_etag=False):
        require(path.startswith('/') and not path.startswith('//'), 'API path must remain on the selected host.')
        headers, body = {}, None
        if self.token:
            headers['Authorization'] = 'Bearer ' + self.token
        if if_match is not None:
            require(strong_etag(if_match), 'A title write requires its observed strong ETag.')
            headers['If-Match'] = if_match
        if value is not None:
            headers['Content-Type'] = 'application/json'
            body = json.dumps(value).encode()
        if file is not None:
            filename, data = file
            boundary = 'romd-' + uuid.uuid4().hex
            headers['Content-Type'] = 'multipart/form-data; boundary=' + boundary
            # Never put a private source filename into multipart headers.
            suffix = Path(filename).suffix
            require(suffix.isascii() and suffix.replace('.', '').isalnum(), 'Unsupported upload filename.')
            body = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="demo{suffix}"\r\n'
                    'Content-Type: application/octet-stream\r\n\r\n').encode() + data + f'\r\n--{boundary}--\r\n'.encode()
        try:
            response = self.opener.open(Request(self.origin + path, body, headers, method=method), timeout=180)
        except HTTPError as error:
            error.close()
            raise ApiError(method, path, error.code) from None
        with response:
            content = response.read()
            value = content if raw else json.loads(content) if content else None
            if with_etag:
                etag = response.headers.get('ETag')
                require(strong_etag(etag), 'The title response has no strong ETag; refusing to change it.')
                return value, etag
            return value


def strong_etag(value):
    return isinstance(value, str) and re.fullmatch(r'"[\x21\x23-\x7e]+"', value) is not None


def save(path, value):
    temporary = path.with_suffix('.tmp')
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    os.fchmod(descriptor, 0o600)
    with os.fdopen(descriptor, 'w') as handle:
        json.dump(value, handle, indent=2)
        handle.write('\n')
    temporary.replace(path)


_NO_PAGE = object()


def paged_items(api, path, *, first_page=_NO_PAGE, **filters):
    """Finish a coherent collection before making identity or reuse decisions."""
    items, cursor, seen = [], None, set()
    while True:
        page = (api.call(path + '?' + urlencode({**filters, 'limit': 100,
                **({'cursor': cursor} if cursor is not None else {})}))
                if first_page is _NO_PAGE else first_page)
        first_page = _NO_PAGE
        require(isinstance(page, dict) and isinstance(page.get('items'), list)
                and all(isinstance(item, dict) for item in page['items'])
                and type(page.get('hasNextPage')) is bool and 'nextCursor' in page,
                f'{path} returned an invalid page.')
        next_cursor = page['nextCursor']
        if page['hasNextPage']:
            require(isinstance(next_cursor, str) and bool(next_cursor) and next_cursor not in seen,
                    f'{path} returned an invalid or repeated next cursor.')
            seen.add(next_cursor)
        else:
            require(next_cursor is None, f'{path} returned a cursor after its final page.')
        items.extend(page['items'])
        if not page['hasNextPage']:
            return items
        cursor = next_cursor


class Seeder:
    def __init__(self, root, entries, email, password, timeout=600, catalogs=None):
        self.root, self.entries, self.timeout = root, entries, timeout
        self.catalogs = catalogs or {}
        record = managed_session(root)
        self.urls = record['urls']
        self.api = Api(self.urls['admin_api'], authorize(self.urls['admin_api'], self.urls['admin_web'],
                       'romd-admin-spa', email, password))
        Api(self.urls['admin_api']).call('/health/ready')
        consumer = Api(self.urls['consumer_api'])
        consumer.call('/health/ready')
        instance = consumer.call('/api/server/identity')['instanceId']
        self.key = fingerprint(entries, self.catalogs)
        self.directory = demo_workspace.workspace(root) / 'state' / self.key
        self.directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.path = self.directory / 'state.json'
        self.state = json.loads(self.path.read_text()) if self.path.exists() else {}
        if self.state.get('instanceId') != instance:
            self.state = dict(version=VERSION, instanceId=instance, fingerprint=self.key, jobs={})
        save(self.directory / 'manifest.json', dict(version=VERSION, fingerprint=self.key,
             entries=[entry.manifest() for entry in entries]))

    def wait_job(self, job):
        deadline = time.monotonic() + self.timeout
        while time.monotonic() < deadline:
            result = self.api.call('/api/jobs/' + job)
            if result['isTerminal']:
                require(result['phase'] == 'Completed' and not result['hasErrors'] and
                        not result.get('romsRejected', 0), f'Demo job {job} did not complete cleanly; inspect Jobs.')
                return result
            time.sleep(0.5)
        raise manage.SetupError(f'Timed out waiting for demo job {job}; rerun to resume it.')

    def upload(self, key, system, filename, data):
        job = self.state['jobs'].get(key)
        if not job:
            accepted = self.api.call('/api/upload?' + urlencode(dict(systemKey=system)), 'POST', file=(filename, data))
            job = self.state['jobs'][key] = accepted['jobId']
            save(self.path, self.state)
        return self.wait_job(job)

    def ensure_dat(self, system, key, name, data):
        existing = [dat for dat in paged_items(self.api, '/api/dats') if dat['name'] == name and dat['systemKey'] == system]
        require(len(existing) <= 1, 'Duplicate DAT names require manual inspection.')
        if existing:
            actual = self.api.call('/api/dats/' + existing[0]['id'] + '/download', raw=True)
            require(demo_workspace.sha(actual) == demo_workspace.sha(data),
                    'The installed DAT differs from the frozen .demo snapshot; review its subscription or re-prepare explicitly.')
            return existing[0]['id']
        job = self.state['jobs'].get(key)
        if job:
            try:
                stale = self.api.call('/api/jobs/' + job)['isTerminal']
            except ApiError as error:
                if error.status != 404:
                    raise
                stale = True
            if stale:
                self.state['jobs'] = {k: v for k, v in self.state['jobs'].items() if not k.startswith(system + ':')}
        self.upload(key, system, 'fixture.dat', data)
        existing = [dat for dat in paged_items(self.api, '/api/dats') if dat['name'] == name and dat['systemKey'] == system]
        require(len(existing) == 1, 'DAT upload did not publish its definition.')
        return existing[0]['id']

    def source_rows(self, dat_id, entries):
        deadline = time.monotonic() + self.timeout
        result = []
        for entry in entries:
            while True:
                rows = [row for row in paged_items(self.api, '/api/dats/' + dat_id + '/source-entries', query=entry.name)
                        if row['name'] == entry.name]
                if len(rows) == 1 and rows[0]['titleId'] and (not entry.present or rows[0]['hasLocalPayload']):
                    result.append(rows[0])
                    break
                require(time.monotonic() < deadline, 'Selected source links/payloads did not converge; inspect worker logs.')
                time.sleep(0.5)
        return result

    def subscriptions(self):
        directory = self.api.call('/api/dat-catalogs')
        require(directory['enabled'], 'Start the source session with mise run dev:demo:serve to enable its pinned catalog reader.')
        available = {row['catalogId']: row for row in directory['catalogs']}
        existing = {row['catalogId']: row for row in self.api.call('/api/dat-subscriptions')}
        reviews = []
        for definition in self.catalogs.values():
            catalog_id = definition['catalogId']
            require(catalog_id in available, 'A selected catalog is absent from the authenticated directory.')
            # Enrollment runs the first check; an existing subscription is checked again explicitly.
            status = (self.api.call('/api/dat-subscriptions/' + existing[catalog_id]['id'] + '/check', 'POST')
                      if catalog_id in existing else
                      self.api.call('/api/dat-subscriptions', 'POST', {'catalogId': catalog_id}))
            require(status['state'] in ('UpToDate', 'ReadyToImport', 'UpdateAvailable'),
                    f'Catalog check did not produce a usable candidate: {catalog_id}')
            preview = None if status['state'] == 'UpToDate' else self.api.call(
                '/api/dat-subscriptions/' + status['id'] + '/preview')
            reviews.append(dict(catalogId=catalog_id, subscription=status, preview=preview))
            print(f'{catalog_id}: {status["state"]}; future activation requires review.', flush=True)
        save(self.directory / 'subscription-review.json', reviews)
        return reviews

    def verify_bios(self, consumer):
        evidence = []
        for entry in (e for e in self.entries if e.bios):
            expected = demo_workspace.sha(entry.data)
            items = consumer.call('/api/systems/' + entry.system + '/bios')['items']
            matches = [item for item in items if item['isAvailable'] and item['sha256'] == expected and item['contentGrant']]
            require(matches, 'Prepared BIOS is not available to the assigned consumer library: ' + entry.system)
            url = urlsplit(matches[0]['contentGrant']['downloadUrl'])
            origin = urlsplit(self.urls['consumer_api'])
            require((not url.scheme and not url.netloc or (url.scheme, url.netloc) == (origin.scheme, origin.netloc)) and
                    url.path.startswith(('/delivery/bios/', '/api/delivery/bios/')) and not url.query and not url.fragment,
                    'BIOS content grant points outside the selected consumer host.')
            data = Api(self.urls['consumer_api']).call(url.path, raw=True)
            require(data == entry.data, 'Consumer BIOS download does not match the prepared bytes: ' + entry.system)
            evidence.append(dict(system=entry.system, sha256=expected, bytes=len(data)))
        save(self.directory / 'bios-verification.json', evidence)

    def curate_generated_title(self, title_id, index):
        path = '/api/titles/' + title_id
        def title_response(response):
            detail, etag = response
            require(isinstance(detail, dict) and detail.get('id') == title_id and
                    isinstance(detail.get('revision'), str) and etag == '"' + detail['revision'] + '"',
                    'The generated title identity or revision changed; refusing to curate it.')
            return detail, etag

        detail, etag = title_response(self.api.call(path, with_etag=True))
        require('media' in detail, 'The title response has no media page; refusing to change it.')
        # Finish the collection before deciding whether any write is necessary.
        media = paged_items(self.api, path + '/media', first_page=detail['media'])
        if not detail.get('description'):
            detail, etag = title_response(self.api.call(path, 'PATCH', dict(
                description='Generated ROMD development fixture. Not a playable game.',
                genre=('Adventure', 'Puzzle', 'Action')[index % 3], developer='ROMD Demo',
                publisher='ROMD Demo', releaseDate='2000-01-01', players=1, rating=70 + index),
                if_match=etag, with_etag=True))
        if not media:
            self.api.call(path + '/media?type=Cover', 'POST', file=('cover.png', cover(index)),
                          if_match=etag, with_etag=True)

    def seed(self):
        selected = {}
        for system in sorted({e.system for e in self.entries}):
            print('Preparing demo system ' + system, flush=True)
            self.api.call('/api/system-setup/' + system + '/enabled', 'PUT', {'enabled': True})
            group = [entry for entry in self.entries if entry.system == system]
            normal = [entry for entry in group if not entry.bios]
            name = f'ROMD Development v{VERSION} {self.key[:16]} {system}'
            if system in self.catalogs:
                definition = self.catalogs[system]
                dat_id = self.ensure_dat(system, system + ':dat:' + definition['sha256'], definition['name'], definition['data'])
                firmware = [entry for entry in group if entry.bios]
                if firmware:
                    bios_name = name + ' BIOS Inventory'
                    self.ensure_dat(system, system + ':bios-dat', bios_name, dat_bytes(firmware, bios_name))
            else:
                definition = dat_bytes(group, name)
                relative = f'fixtures/{self.key}/{system}.dat'
                demo_workspace.retain(demo_workspace.workspace(self.root), relative, definition)
                dat_id = self.ensure_dat(system, system + ':dat', name, definition)
            for entry in group:
                if entry.present:
                    data = entry.archive if entry.archive else entry.data
                    filename = Path(entry.source).name if entry.archive else entry.filename
                    self.upload(system + ':' + entry.manifest()['sha256'], system, filename, data)
            for row in self.source_rows(dat_id, normal):
                title = selected.setdefault(row['titleId'], dict(id=row['titleId'], name=row['titleName'], owned=False))
                title['owned'] |= row['hasLocalPayload']
        selected = sorted(selected.values(), key=lambda title: title['id'])
        # Only generated titles get authored metadata and original fixture covers.
        # Existing real titles are imported without overwriting their curation.
        if all(e.source == 'generated' for e in self.entries):
            for index, title in enumerate(selected):
                self.curate_generated_title(title['id'], index)
        library_name = 'ROMD Demo ' + self.key[:16]
        title_ids = sorted(t['id'] for t in selected)
        configuration = library_configuration(title_ids)
        libraries = [x for x in paged_items(self.api, '/api/libraries') if x['name'] == library_name]
        require(len(libraries) <= 1, 'Duplicate demo library names require manual inspection.')
        if libraries:
            library = self.api.call('/api/libraries/' + libraries[0]['id'])
            require(isinstance(library, dict) and library.get('id') == libraries[0]['id'],
                    'The selected demo library identity changed.')
            actual = library['configuration']
            require(library['name'] == library_name and actual['titleSelectionMode'] == 'IncludeOnly' and sorted(actual['includeTitleIds']) == title_ids and
                    actual['includeUntrackedTitles'] is True and actual['showMissingGames'] is True and library['isDefault'] is False and
                    actual['contentRatingPolicy']['unknownRatingPolicy'] == 'Allow',
                    'The demo library was edited; refusing to overwrite it.')
        else:
            library = self.api.call('/api/libraries', 'POST', dict(name=library_name, configuration=configuration))
        require(type(library.get('needsMaterialization')) is bool and 'lastMaterializedAt' in library,
                'Demo library materialization state is incomplete; refusing to change it.')
        if library['needsMaterialization'] or library['lastMaterializedAt'] is None:
            job = self.api.call('/api/libraries/' + library['id'] + '/materialize', 'POST')['jobId']
            self.wait_job(job)
        email = f'demo-{self.key[:12]}@localhost'
        users = [u for u in paged_items(self.api, '/api/users') if u['email'] == email]
        require(len(users) <= 1, 'Duplicate demo accounts require manual inspection.')
        if not users:
            user = self.api.call('/api/users', 'POST', dict(email=email, password=PASSWORD, role='User', libraryId=library['id']))
        else:
            user = self.api.call('/api/users/' + users[0]['id'])
            require(isinstance(user, dict) and user.get('id') == users[0]['id'],
                    'The selected demo account identity changed.')
        require(user['email'] == email and user['roles'] == ['User'] and user['libraryId'] == library['id'] and
                user['isSuspended'] is False and user['requiresActivation'] is False,
                'The demo account was edited; refusing to reset its password, role or library.')
        token = authorize(self.urls['consumer_api'], self.urls['consumer_web'], 'romd-consumer-spa', email, PASSWORD)
        consumer = Api(self.urls['consumer_api'], token)
        context = consumer.call('/api/me/library')
        require(context['name'] == library_name, 'Consumer did not receive its demo library.')
        visible = paged_items(consumer, '/api/catalog')
        owned_ids = {title['id'] for title in selected if title['owned']}
        save(self.directory / 'verification.json', dict(adminTitleIds=title_ids,
             expectedConsumerTitleIds=sorted(owned_ids), consumerTitleIds=sorted(x['id'] for x in visible),
             libraryCounts=context['counts']))
        require(owned_ids and {x['id'] for x in visible} == owned_ids and
                context['counts']['ownedTitleCount'] == len(owned_ids),
                'Consumer demo titles do not match the owned fixtures.')
        if self.catalogs:
            self.verify_bios(consumer)
        self.state.update(status='ready', libraryId=library['id'], libraryName=library_name,
                          consumerEmail=email, titles=len(title_ids), consumerTitles=len(visible),
                          ownedTitles=context['counts']['ownedTitleCount'],
                          completedAt=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()))
        save(self.path, self.state)
        print(f'Demo ready: {len(title_ids)} admin titles, {len(visible)} consumer titles; consumer {email}; password {PASSWORD}', flush=True)
        print(f'Private manifest and resumable state: {self.directory}', flush=True)
        return self.state


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--synthetic', action='store_true', help='Use generated, non-playable qualification fixtures explicitly')
    parser.add_argument('--plan', action='store_true', help='Verify and show prepared inputs without contacting hosts')
    action = parser.add_mutually_exclusive_group()
    action.add_argument('--serve', action='store_true', help='Run managed source hosts with the prepared signed catalog reader')
    action.add_argument('--subscriptions', action='store_true', help='Subscribe/check/preview prepared catalogs; never approve updates')
    parser.add_argument('--timeout', type=int, default=600, help='Maximum seconds per job/source-link wait')
    parser.add_argument('--wait', type=int, nargs='?', const=WAIT_DEFAULT, metavar='SECONDS',
                        help=f'Wait for this checkout\'s managed session to become ready (default {WAIT_DEFAULT}s)')
    args = parser.parse_args(arguments)
    require(args.timeout > 0, '--timeout must be positive.')
    require(args.wait is None or args.wait > 0, '--wait SECONDS must be positive.')
    require(args.wait is None or not (args.plan or args.serve), '--wait applies to seeding and --subscriptions only.')
    require(not args.synthetic or not (args.serve or args.subscriptions), 'Synthetic fixtures have no live catalog subscriptions.')
    catalogs = {}
    if args.synthetic:
        entries = dataset()
    else:
        _, values, catalogs = demo_workspace.load()
        entries = [Entry(**value) for value in values]
    if args.plan:
        print(json.dumps(dict(version=VERSION, fingerprint=fingerprint(entries, catalogs),
              entries=[e.manifest() for e in entries], catalogs=[{k: v for k, v in row.items() if k != 'data'}
              for row in catalogs.values()]), indent=2))
        return
    if args.serve:
        environment = demo_workspace.subscription_environment(manage.ROOT)
        # .NET keys are case-insensitive; prevent inherited aliases winning order.
        prefixes = ('datsubscriptions:',)
        for key in list(os.environ):
            if key.lower().replace('__', ':').startswith(prefixes):
                del os.environ[key]
        os.environ.update(environment)
        manage.development('source')
        return
    email = os.environ.get('ROMD_DEMO_ADMIN_EMAIL', 'admin@localhost')
    password = os.environ.get('ROMD_DEMO_ADMIN_PASSWORD', 'ChangeMe123!')
    if args.wait is not None:
        wait_for_session(manage.ROOT, args.wait)
    with seed_lock(demo_workspace.workspace()):
        seeder = Seeder(manage.ROOT, entries, email, password, args.timeout, catalogs)
        if args.subscriptions:
            require(catalogs, 'Prepare real catalogs before setting up subscriptions.')
            seeder.subscriptions()
        else:
            if args.synthetic:
                for entry in entries:
                    if entry.present:
                        demo_workspace.retain(demo_workspace.workspace(),
                            f'fixtures/{seeder.key}/{entry.system}/{entry.filename}', entry.data)
            seeder.seed()


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(130)
    except (manage.SetupError, OSError, ValueError) as error:
        sys.exit(f'Demo setup failed: {error}')
