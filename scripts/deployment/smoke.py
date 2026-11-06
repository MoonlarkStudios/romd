#!/usr/bin/env python3
"""Disposable real-Compose startup, HTTPS/PKCE, restart and backup/restore drill.
Only destroys uniquely named projects created by this invocation. Never uses .env.
"""
import argparse
import base64
import hashlib
import html
import ipaddress
import json
import os
import re
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import zlib
import xml.etree.ElementTree as ET
from urllib.parse import urlencode, urlsplit, parse_qs

from test_contract import ROOT, test_environment
from container_lifecycle import ContainerLifecycle, HOSTS


def run(args, env, capture=False):
    return subprocess.run([str(x) for x in args], env=env, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None).stdout


def copy_readable_fixture(compose, env, source, destination):
    """Docker cp preserves restrictive modes and creates root-owned files.

    Make only the disposable container copies readable by its application user;
    the caller's read-only source, private key and evidence keep their modes.
    """
    if destination not in ('/tmp/smoke-ca.pem', '/tmp/vfs-smoke.dat', '/tmp/vfs-smoke.iso'):
        raise ValueError('Unexpected disposable smoke fixture destination')
    run([compose, 'cp', source, 'romd-admin:' + destination], env)
    run([compose, 'exec', '-T', '--user', 'root', 'romd-admin', 'chmod', '0644', destination], env)


def find_system_key(fetch, name):
    """Follow the keyset Pages of GET /api/systems to the first system whose name contains name."""
    cursor, seen = None, set()
    while True:
        page = fetch('/api/systems?' + urlencode({'limit': 100, **({} if cursor is None else {'cursor': cursor})}))
        key = next((item['key'] for item in page['items'] if name in item['name']), None)
        if key is not None:
            return key
        cursor = page['nextCursor']
        if not page['hasNextPage']:
            assert cursor is None, 'systems page returned a cursor after its final page'
            raise AssertionError(f'{name} system is missing')
        assert isinstance(cursor, str) and cursor and cursor not in seen, 'systems page returned an invalid or repeated next cursor'
        seen.add(cursor)


def backup_and_stop_source(compose, env, gate, backup_root):
    """Capture the source before stopping it to expose the restored identity."""
    source_project = env['COMPOSE_PROJECT_NAME']
    with gate.stopping(source_project, HOSTS):
        run([ROOT / 'scripts/backup/backup.sh', backup_root], env)
    # The backup's EXIT trap starts the hosts without waiting for readiness. Finish
    # that restart before deliberately stopping the source installation again.
    run([compose, 'up', '-d', '--wait', '--wait-timeout', '240'], env)
    gate.checkpoint(source_project)
    backup = next(backup_root.iterdir())
    # Keep source data intact, but stop its containers before exposing a restored identity.
    with gate.stopping(source_project):
        run([compose, 'stop'], env)
    return backup


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--build', action='store_true')
    parser.add_argument('--https-bind', default='127.0.0.1',
                        help='IPv4 loopback address for the disposable ingress publication')
    parser.add_argument('--evidence-dir', type=Path, help='New directory for sanitized lifecycle evidence')
    parser.add_argument('--source-revision')
    parser.add_argument('--release-tag')
    parser.add_argument('--vfs', action='store_true', help='Verify upload, publication, registered-connection WebDAV content integrity and revocation')
    parser.add_argument('--vfs-file', type=Path, help='Read-only local disc image for the VFS drill (requires --vfs); default is a synthetic 4 MiB file')
    args = parser.parse_args()
    try:
        if not ipaddress.IPv4Address(args.https_bind).is_loopback:
            raise ValueError()
    except ValueError:
        parser.error('--https-bind must be an IPv4 loopback address')
    if bool(args.source_revision) != bool(args.release_tag):
        parser.error('--source-revision and --release-tag must be supplied together')
    if args.source_revision and (not re.fullmatch('[0-9a-f]{40}', args.source_revision)
            or not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?', args.release_tag)):
        parser.error('Invalid release build identity')
    if args.vfs_file and (not args.vfs or not args.vfs_file.is_file()):
        parser.error('--vfs-file requires --vfs and an existing regular file')
    evidence = (args.evidence_dir or ROOT / '.artifacts/deployment-smoke' / secrets.token_hex(12)).resolve()
    evidence.mkdir(parents=True, mode=0o700, exist_ok=False)
    print(f'Lifecycle evidence: {evidence / "lifecycle.json"}', flush=True)
    with tempfile.TemporaryDirectory(prefix='romd-compose-smoke-') as temporary:
        work = Path(temporary)
        env = test_environment()
        if args.source_revision:
            env.update(ROMD_SOURCE_REVISION=args.source_revision, ROMD_RELEASE_TAG=args.release_tag)
        if args.vfs:
            env['ROMD_VFS_ENABLED'] = 'true'
        env['ROMD_DEFAULT_ADMIN_EMAIL'] = 'admin@example.test'
        env['ROMD_TLS_DIRECTORY'] = str(work)
        env['ROMD_HTTPS_PORT'] = '0'
        env['ROMD_HTTPS_BIND'] = args.https_bind
        env['ROMD_NGINX_IMAGE'] = os.environ.get('ROMD_NGINX_IMAGE', env['ROMD_NGINX_IMAGE'])
        env['COMPOSE_PROJECT_NAME'] = 'romd-smoke-' + secrets.token_hex(5)
        source_project = env['COMPOSE_PROJECT_NAME']
        target_project = source_project + '-restore'
        env['ROMD_ENV_FILE'] = str(work / 'empty.env')
        (work / 'empty.env').touch()
        compose = ROOT / 'scripts/deployment/compose.sh'
        run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', work / 'privkey.pem',
             '-out', work / 'fullchain.pem', '-days', '1', '-subj', '/CN=ROMD smoke test',
             '-addext', 'subjectAltName=DNS:admin.example.test,DNS:console.example.test,DNS:play.example.test,DNS:unknown.example.test'], env, capture=True)
        if args.build:
            env['COMPOSE_FILE'] = f'{ROOT}/compose.yaml:{ROOT}/compose.build.yaml'
            run([compose, 'build'], env)
        env['COMPOSE_FILE'] = f'{ROOT}/compose.yaml:{ROOT}/deploy/examples/https/compose.yaml'
        env.update(ROMD_EDGE_SUBNET='172.30.0.0/24', ROMD_EDGE_DYNAMIC_RANGE='172.30.0.128/25', ROMD_TRUSTED_PROXY='172.30.0.2', ROMD_ADMIN_EDGE_IP='172.30.0.3', ROMD_CONSUMER_EDGE_IP='172.30.0.4', ROMD_PLAYER_EDGE_IP='172.30.0.5')

        fixture_file = args.vfs_file.resolve() if args.vfs_file else work / 'vfs-smoke.iso'
        fixture_size, fixture_crc = 0, 0
        fixture_hashes = {name: hashlib.new(name) for name in ('md5', 'sha1', 'sha256')}
        if args.vfs:
            if not args.vfs_file:
                fixture_file.write_bytes(secrets.token_bytes(4 * 1024 * 1024))
            with fixture_file.open('rb') as source:
                while chunk := source.read(1024 * 1024):
                    fixture_size += len(chunk)
                    fixture_crc = zlib.crc32(chunk, fixture_crc)
                    for digest in fixture_hashes.values():
                        digest.update(chunk)
            if not 65536 <= fixture_size <= 10 * 1024 ** 3:
                parser.error('VFS fixture must be between 64 KiB and 10 GiB')
            print(f'VFS source: {fixture_size} bytes; SHA-256 {fixture_hashes["sha256"].hexdigest()}', flush=True)
        fixture_imported = False

        def fixture_range(start, length):
            with fixture_file.open('rb') as source:
                source.seek(start)
                return source.read(length)

        def request(host, path, form=None, token=None, method=None, payload=None, extra_headers=None, upload=None, raw=False, hash_content=False, authorization=None):
            # Trust only the fixture certificate, without changing the container or host trust store.
            command = [compose, 'exec', '-T', 'romd-admin', 'curl', '-sS', '--cacert', '/tmp/smoke-ca.pem',
                       '--max-time', '300' if upload or hash_content else '20', '--noproxy', '*',
                       '--resolve', f'{host}:443:{env["ROMD_TRUSTED_PROXY"]}', '-b', '/tmp/smoke-cookies', '-c', '/tmp/smoke-cookies']
            command += ['--dump-header', '/tmp/smoke-download-headers'] if hash_content else ['-i']
            if form is not None:
                command += ['--data', urlencode(form)]
            if payload is not None:
                command += ['-H', 'Content-Type: application/json', '--data', json.dumps(payload)]
            if upload:
                command += ['-F', 'file=@' + upload]
            if method == 'HEAD':
                command += ['--head']
            elif method:
                command += ['-X', method]
            for name, value in (extra_headers or {}).items():
                command += ['-H', name + ': ' + value]
            if token:
                command += ['-H', 'Authorization: Bearer ' + token]
            elif authorization:
                command += ['-H', 'Authorization: ' + authorization]
            command += ['https://' + host + path]
            if hash_content:
                digest, size = hashlib.sha256(), 0
                started = time.monotonic()
                # Keep the multi-GiB body out of both subprocess buffers and temporary disk files.
                with subprocess.Popen([str(x) for x in command], env=env, stdout=subprocess.PIPE) as process:
                    while chunk := process.stdout.read(1024 * 1024):
                        digest.update(chunk)
                        size += len(chunk)
                    assert process.wait() == 0, 'streamed content request failed'
                result = {'bytes': size, 'sha256': digest.hexdigest(), 'seconds': time.monotonic() - started}
                response = subprocess.check_output([str(compose), 'exec', '-T', 'romd-admin',
                                                    'cat', '/tmp/smoke-download-headers'], env=env)
            else:
                response = subprocess.check_output([str(x) for x in command], env=env)
            headers, body = response.split(b'\r\n\r\n', 1)
            # Multipart uploads can emit an interim 100 Continue before the final response.
            while 100 <= int(headers.splitlines()[0].split()[1]) < 200:
                headers, body = body.split(b'\r\n\r\n', 1)
            headers = headers.decode('iso-8859-1')
            if hash_content:
                body = result
            elif not raw:
                body = body.decode('utf-8')
            status = int(headers.splitlines()[0].split()[1])
            fields = dict(line.split(': ', 1) for line in headers.splitlines()[1:] if ': ' in line)
            return status, {k.lower(): v for k, v in fields.items()}, body

        def sign_in(host, client_id):
            # Authenticate with authorization-code + PKCE through the real HTTPS proxy.
            verifier = secrets.token_urlsafe(32)
            challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).decode().rstrip('=')
            redirect = f'https://{host}/auth/callback'
            authorize = '/connect/authorize?' + urlencode(dict(client_id=client_id, response_type='code',
                scope='openid profile email roles offline_access', redirect_uri=redirect, code_challenge=challenge,
                code_challenge_method='S256', state='smoke'))
            status, _, body = request(host, '/connect/login')
            assert status == 200, f'{host} login form failed'
            token = re.search(r'name="__RequestVerificationToken" value="([^"]+)"', body)
            assert token is not None, f'{host} login form had no antiforgery token'
            status, _, _ = request(host, '/connect/login', dict(login=env['ROMD_DEFAULT_ADMIN_EMAIL'],
                password=env['ROMD_DEFAULT_ADMIN_PASSWORD'], returnUrl=authorize,
                __RequestVerificationToken=html.unescape(token[1])))
            assert status == 302, f'{host} login failed'
            status, headers, _ = request(host, authorize)
            assert status == 302, f'{host} authorization failed'
            code = parse_qs(urlsplit(headers['location']).query)['code'][0]
            status, _, body = request(host, '/connect/token', dict(grant_type='authorization_code',
                client_id=client_id, redirect_uri=redirect, code=code, code_verifier=verifier))
            assert status == 200, f'{host} token exchange failed'
            return json.loads(body)['access_token']

        def verify():
            copy_readable_fixture(compose, env, work / 'fullchain.pem', '/tmp/smoke-ca.pem')
            for host, trust in [('console.example.test', False), ('wrong.example.test', True)]:
                command = [str(compose), 'exec', '-T', 'romd-admin', 'curl', '-s', '--max-time', '20',
                           '--noproxy', '*', '--resolve', f'{host}:443:{env["ROMD_TRUSTED_PROXY"]}']
                if trust:
                    command += ['--cacert', '/tmp/smoke-ca.pem']
                result = subprocess.run(command + ['https://' + host + '/'], env=env, capture_output=True)
                assert result.returncode == 60 and not result.stdout, 'TLS trust/hostname rejection failed'
            run([ROOT / 'scripts/deployment/verify.sh'], env)
            direct_status = run([compose, 'exec', '-T', 'romd-admin', 'curl', '-s', '-o', '/dev/null',
                '-w', '%{http_code}', '-H', 'X-Forwarded-Proto: https', '-H', 'Host: admin.example.test',
                'http://localhost:8080/.well-known/openid-configuration'], env, capture=True)
            assert direct_status != '200', 'untrusted peer can spoof HTTPS'

            for host in ('admin.example.test', 'console.example.test'):
                status, _, body = request(host, '/.well-known/openid-configuration')
                assert status == 200 and json.loads(body)['issuer'] == f'https://{host}/', 'discovery origin mismatch'
                assert request(host, '/')[0] == 200, 'portal unavailable'
            assert request('play.example.test', '/player-config.json')[0] == 200
            assert request('unknown.example.test', '/')[0] == 421, 'unknown hostname accepted'
            host = 'admin.example.test'
            token = sign_in(host, 'romd-admin-spa')
            status, _, me = request(host, '/api/me', token=token)
            assert status == 200, 'authenticated API failed'
            if args.vfs:
                verify_vfs(token, json.loads(me)['id'])
            status, _, identity = request('console.example.test', '/api/server/identity')
            assert status == 200
            return json.loads(identity)

        def verify_vfs(admin_token, user_id):
            nonlocal fixture_imported
            admin = 'admin.example.test'
            consumer = 'console.example.test'
            if not fixture_imported:
                import_vfs_fixture(admin_token)
                fixture_imported = True
            status, _, body = request(admin, '/api/libraries', token=admin_token,
                                      payload={'name': 'VFS smoke ' + secrets.token_hex(4),
                                               'configuration': {'includeUntrackedTitles': True,
                                                   'contentRatingPolicy': {'unknownRatingPolicy': 'Allow'}}})
            assert status == 201, 'VFS library creation failed'
            library_id = json.loads(body)['id']
            assert request(admin, f'/api/libraries/{library_id}/materialize', token=admin_token,
                           method='POST')[0] == 202, 'VFS publication did not queue'
            # Changing our own library revokes existing sessions, including admin_token.
            # Finish admin operations before assigning access, then sign in again as a consumer.
            assert request(admin, f'/api/users/{user_id}/library', token=admin_token,
                           method='PUT', payload={'libraryId': library_id})[0] == 200, 'VFS assignment failed'
            assert request(consumer, '/vfs/library/', method='PROPFIND', extra_headers={'Depth': '1'})[0] == 401
            # Release setup: consumer sign-in manages a registered connection whose app password reads /vfs.
            consumer_token = sign_in(consumer, 'romd-consumer-spa')
            assert request(consumer, '/vfs/library/', token=consumer_token, method='PROPFIND',
                           extra_headers={'Depth': '1'})[0] == 401, 'VFS accepted a bearer token by default'
            status, headers, body = request(consumer, '/api/me/connections', token=consumer_token,
                                      payload={'name': 'Smoke drill'})
            assert status == 201, f'VFS connection issuance failed ({status})'
            issued = json.loads(body)
            assert issued['endpoint'] == f'https://{consumer}/vfs/library/', 'VFS export used the wrong origin'
            assert 'vendor = owncloud' in issued['rcloneConfig'], 'VFS export lacks the checksum profile'
            connection = issued['connection']
            assert headers.get('location') == '/api/me/connections/' + connection['id'], 'VFS connection Location is not canonical'
            credential = 'Basic ' + base64.b64encode(f"{connection['username']}:{issued['password']}".encode()).decode()
            deadline = time.monotonic() + 90
            while True:
                status, _, body = request(consumer, '/vfs/library/', authorization=credential,
                                           method='PROPFIND', extra_headers={'Depth': '1'})
                if status == 207:
                    content_path = find_vfs_file(credential)
                    verify_vfs_content(content_path, credential)
                    break
                # The library is unavailable (404) until its first publication completes.
                assert status in (403, 404, 503) and time.monotonic() < deadline, f'VFS publication/listing failed ({status})'
                time.sleep(1)
            assert request(consumer, '/api/me/connections/' + connection['id'], token=consumer_token,
                           method='DELETE')[0] == 204, 'VFS connection revocation failed'
            assert request(consumer, '/vfs/library/', authorization=credential, method='PROPFIND',
                           extra_headers={'Depth': '1'})[0] == 401, 'revoked connection still works'
            status, _, denied = request(consumer, content_path, authorization=credential,
                                        extra_headers={'Range': 'bytes=0-4095'}, raw=True)
            assert status == 401 and fixture_range(0, 4096) not in denied, 'revoked content access succeeded'
            print('Production VFS upload, publication, exact ranges, full hash and revocation passed.', flush=True)

        def import_vfs_fixture(token):
            def fetch(path):
                status, _, body = request('admin.example.test', path, token=token)
                assert status == 200, f'system lookup failed ({status})'
                return json.loads(body)
            system = find_system_key(fetch, 'GameCube')
            rom = fixture_file
            dat = work / 'vfs-smoke.dat'
            dat.write_text(f'''<?xml version="1.0"?>
<datafile><header><name>ROMD VFS Smoke</name><description>Disposable synthetic fixture</description><author>ROMD</author></header>
<game name="ROMD VFS Smoke (USA)"><rom name="vfs-smoke.iso" size="{fixture_size}" crc="{fixture_crc:08x}" md5="{fixture_hashes["md5"].hexdigest()}" sha1="{fixture_hashes["sha1"].hexdigest()}"/></game></datafile>''')
            for file, endpoint, counter in [(dat, '/api/upload/dat?' + urlencode({'systemKey': system}), 'datsSucceeded'),
                                             (rom, '/api/upload/rom', 'romsIngested')]:
                destination = '/tmp/vfs-smoke.dat' if file == dat else '/tmp/vfs-smoke.iso'
                copy_readable_fixture(compose, env, file, destination)
                status, _, body = request('admin.example.test', endpoint, token=token, upload=destination)
                assert status == 202, f'{file.name} upload failed ({status})'
                # Compose cp creates a root-owned file in sticky /tmp; remove only this fixture.
                run([compose, 'exec', '-T', '--user', 'root', 'romd-admin', 'rm', destination], env)
                job_id = json.loads(body)['jobId']
                deadline = time.monotonic() + 300
                while True:
                    status, _, body = request('admin.example.test', '/api/jobs/' + job_id, token=token)
                    assert status == 200, 'upload job lookup failed'
                    job = json.loads(body)
                    if job['isTerminal']:
                        assert not job['hasErrors'] and job[counter] == 1, f'{file.name} processing failed: {job}'
                        break
                    assert time.monotonic() < deadline, f'{file.name} processing timed out'
                    time.sleep(1)

        def find_vfs_file(credential):
            pending, visited, files = ['/vfs/library/'], set(), []
            while pending:
                path = pending.pop()
                assert path.startswith('/vfs/library/') and path not in visited and len(visited) < 16
                visited.add(path)
                status, _, body = request('console.example.test', path, authorization=credential,
                                           method='PROPFIND', extra_headers={'Depth': '1'})
                assert status == 207, f'VFS traversal failed ({status})'
                for response in ET.fromstring(body).findall('{DAV:}response'):
                    href = response.findtext('{DAV:}href')
                    if href == path:
                        continue
                    assert href and href.startswith('/vfs/library/')
                    if response.find('.//{DAV:}collection') is not None:
                        pending.append(href)
                    else:
                        files.append(href)
            assert len(files) == 1 and files[0].endswith('/vfs-smoke.iso'), f'unexpected VFS files: {files}'
            return files[0]

        def verify_vfs_content(path, credential):
            host = 'console.example.test'
            size = fixture_size
            status, headers, _ = request(host, path, authorization=credential, method='HEAD')
            assert status == 200 and int(headers['content-length']) == size
            etag = headers['etag']
            assert etag.startswith('"') and not etag.startswith('W/')
            for start, length in [(0, 4096), (65530, 65536), (size // 2 + 3, 4096), (size - 4096, 8192)]:
                end = min(start + length, size) - 1
                status, headers, data = request(host, path, authorization=credential, raw=True,
                    extra_headers={'Range': f'bytes={start}-{start + length - 1}'})
                assert status == 206 and headers['content-range'] == f'bytes {start}-{end}/{size}'
                assert headers['etag'] == etag and data == fixture_range(start, end - start + 1), 'range bytes differ'
            status, _, data = request(host, path, authorization=credential, hash_content=True)
            assert status == 200 and data['bytes'] == size and data['sha256'] == fixture_hashes['sha256'].hexdigest()
            print('VFS streamed integrity: ' + json.dumps(data), flush=True)
            assert request(host, path, authorization=credential, extra_headers={'If-None-Match': etag})[0] == 304
            assert request(host, path, authorization=credential, extra_headers={'Range': f'bytes={size}-'})[0] == 416
            assert request(host, path, extra_headers={'Range': 'bytes=0-4095'})[0] == 401

        gate = ContainerLifecycle(env, (source_project, target_project), env['ROMD_NGINX_IMAGE'], evidence / 'lifecycle.json')
        cleanups = []
        for project in (source_project, target_project):
            cleanup_env = dict(env, COMPOSE_PROJECT_NAME=project)
            cleanups.append((project,
                lambda selected=cleanup_env: run([compose, 'stop'], selected),
                lambda selected=cleanup_env: run([compose, 'down', '--volumes', '--remove-orphans'], selected)))
        with gate.monitor(cleanups):
            run([compose, 'up', '-d', '--wait', '--wait-timeout', '240'], env)
            gate.checkpoint(source_project)
            identity = verify()
            run([compose, 'exec', '-T', 'romd-worker', 'sh', '-c',
                 'mkdir -p /var/lib/romd/content/smoke && printf restore-proof > /var/lib/romd/content/smoke/proof'], env)
            with gate.stopping(source_project, HOSTS):
                run([compose, 'restart', 'romd-worker', 'romd-admin', 'romd-consumer'], env)
            run([compose, 'up', '-d', '--wait', '--wait-timeout', '240'], env)
            gate.checkpoint(source_project)
            assert identity == verify(), 'identity changed across restart'
            backup_root = work / 'backups'
            backup = backup_and_stop_source(compose, env, gate, backup_root)
            env['COMPOSE_PROJECT_NAME'] = target_project
            env.update(ROMD_EDGE_SUBNET='172.31.0.0/24', ROMD_EDGE_DYNAMIC_RANGE='172.31.0.128/25', ROMD_TRUSTED_PROXY='172.31.0.2', ROMD_ADMIN_EDGE_IP='172.31.0.3', ROMD_CONSUMER_EDGE_IP='172.31.0.4', ROMD_PLAYER_EDGE_IP='172.31.0.5')
            run([compose, 'up', '-d', '--wait', 'postgres'], env)
            run([compose, 'create', '--no-recreate', 'romd-worker'], env)
            with gate.stopping(target_project, HOSTS):
                run([ROOT / 'scripts/backup/restore.sh', backup], env)
            run([compose, 'up', '-d', '--wait', '--wait-timeout', '240'], env)
            gate.checkpoint(target_project)
            assert identity == verify(), 'restored identity differs'
            proof = run([compose, 'exec', '-T', 'romd-worker', 'cat', '/var/lib/romd/content/smoke/proof'], env, capture=True)
            assert proof == 'restore-proof', 'content did not survive restore'
        print('Compose startup, HTTPS discovery, PKCE login, restart, restore and cleanup drill passed.', flush=True)


if __name__ == '__main__':
    main()
