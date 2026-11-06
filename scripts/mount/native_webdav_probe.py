#!/usr/bin/env python3
"""Opt-in macOS probe, driven by the disposable ROMD integration fixture.

Private credentials arrive on stdin. No production authentication is implemented.
Exit nonzero for any observed contract failure, preserving a credential-free report.
"""
import base64
import errno
import hashlib
import ipaddress
import json
import os
from pathlib import Path, PurePosixPath
import pty
import random
import select
import signal
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


def deadline(_signal, _frame):
    raise TimeoutError("Native probe exceeded its operation deadline")


def mount(url, path, password):
    pid, master = pty.fork()
    if pid == 0:
        os.execl('/sbin/mount_webdav', 'mount_webdav', '-i', '-o', 'rdonly', url, str(path))
    output = b''
    user_sent = password_sent = False
    status = None
    end = time.monotonic() + 45
    try:
        while time.monotonic() < end:
            done, current = os.waitpid(pid, os.WNOHANG)
            if done:
                status = current
                break
            if not select.select([master], [], [], .1)[0]:
                continue
            try:
                data = os.read(master, 4096)
            except OSError as error:
                if error.errno != errno.EIO:
                    raise
                _, status = os.waitpid(pid, 0)
                break
            output = (output + data)[-8192:]
            if b'username' in output.lower() and not user_sent:
                os.write(master, b'davprobe\n')
                user_sent = True
            if b'password' in output.lower() and not password_sent:
                os.write(master, (password + '\n').encode())
                password_sent = True
        if status is None:
            raise TimeoutError('Native mount did not finish in 45 seconds')
        if os.waitstatus_to_exitcode(status) != 0:
            raise RuntimeError(f'Native mount exited {os.waitstatus_to_exitcode(status)}')
    finally:
        if status is None:
            try:
                os.kill(pid, signal.SIGKILL)
                os.waitpid(pid, 0)
            except ProcessLookupError:
                pass
        os.close(master)


def run(config, point, report):
    parsed = urllib.parse.urlsplit(config['url'])
    if parsed.scheme != 'http' or not ipaddress.ip_address(parsed.hostname).is_loopback:
        raise ValueError('This disposable credential adapter is restricted to loopback HTTP')
    relative = PurePosixPath(config['relativePath'])
    if relative.is_absolute() or '..' in relative.parts:
        raise ValueError('Invalid fixture path')
    uri = config['url'].rstrip('/') + '/' + urllib.parse.quote(str(relative), safe='/')
    authorization = 'Basic ' + base64.b64encode(('davprobe:' + config['password']).encode()).decode()
    # Ignore workstation proxy environment for this explicitly loopback fixture.
    client = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(method='GET', headers=None, auth=authorization):
        values = dict(headers or {})
        if auth is not None:
            values['Authorization'] = auth
        try:
            return client.open(urllib.request.Request(uri, method=method, headers=values), timeout=20)
        except urllib.error.HTTPError as error:
            return error

    for label, auth in [('anonymous', None), ('wrong_credentials', 'Basic ZGF2cHJvYmU6d3Jvbmc=')]:
        with request('HEAD', auth=auth) as response:
            report[label + '_status'] = response.status
            if response.status != 401:
                report['failures'].append(label + ' was not denied')
    with request('HEAD') as response:
        if response.status != 200 or int(response.headers['Content-Length']) != config['length']:
            raise AssertionError('Authenticated fixture HEAD failed')

    mount(config['url'], point, config['password'])
    file = point.joinpath(*relative.parts)
    report['native_stat_size'] = file.stat().st_size
    if report['native_stat_size'] != config['length']:
        report['failures'].append('Native stat length mismatch')
    randomizer = random.Random(20260926)
    # Check EOF before the random workload can populate the client's background cache.
    regions = [(0, 4096), (65533, 65536), (config['length'] - 1024, 4096)] + [
        (randomizer.randrange(config['length'] - 65536), randomizer.choice([4096, 32768, 65536]))
        for _ in range(64)]
    report['ranges'] = []
    with file.open('rb', buffering=0) as stream:
        for offset, length in regions:
            with request(headers={'Range': f'bytes={offset}-{offset + length - 1}'}) as response:
                expected = response.read()
                expected_length = min(length, config['length'] - offset)
                if response.status != 206 or len(expected) != expected_length:
                    raise AssertionError('Direct ROMD range contract failed')
            started = time.monotonic()
            actual = os.pread(stream.fileno(), length, offset)
            result = dict(offset=offset, requested=length, http_bytes=len(expected),
                          native_bytes=len(actual), exact=actual == expected,
                          native_seconds=time.monotonic() - started)
            if actual != expected:
                result['extra_bytes_all_zero'] = len(actual) > len(expected) and not any(actual[len(expected):])
                report['failures'].append(f'Native range mismatch at offset {offset}')
            report['ranges'].append(result)
        report['at_eof_bytes'] = len(os.pread(stream.fileno(), 4096, config['length']))
        if report['at_eof_bytes'] != 0:
            report['failures'].append('Native read at EOF returned bytes')
    started = time.monotonic()
    digest = hashlib.sha256()
    total = 0
    with file.open('rb') as stream:
        while data := stream.read(1024 * 1024):
            digest.update(data)
            total += len(data)
    report['full_read'] = dict(bytes=total, sha256=digest.hexdigest(), seconds=time.monotonic() - started)
    if total != config['length'] or digest.hexdigest() != config['sha256']:
        report['failures'].append('Native whole-file integrity mismatch')
    try:
        with (point / 'romd-native-write-probe').open('xb') as stream:
            stream.write(b'disposable probe')
        report['failures'].append('Native write unexpectedly succeeded')
    except OSError as error:
        report['write_errno'] = error.errno
        if error.errno not in (errno.EROFS, errno.EACCES, errno.EPERM):
            report['failures'].append('Unexpected native write error')


def main():
    config = json.load(sys.stdin)
    root = Path(tempfile.mkdtemp(prefix='romd-native-dav-'))
    point = root / 'mounted'
    point.mkdir(mode=0o700)
    report = {'scope': 'native macOS WebDAV to real ROMD; test-only loopback Basic-to-bearer adapter',
              'failures': [], 'cleanup_verified': False}
    signal.signal(signal.SIGALRM, deadline)
    signal.alarm(180)
    try:
        run(config, point, report)
    except Exception as error:
        # Exception type is enough; never emit request credentials or response bodies.
        report['failures'].append(type(error).__name__)
    finally:
        signal.alarm(0)
        try:
            table = subprocess.check_output(['/sbin/mount'], text=True, timeout=10)
            marker = ' on ' + str(point.resolve()) + ' ('
            if marker in table:
                subprocess.run(['/sbin/umount', str(point)], check=True, capture_output=True, timeout=20)
            table = subprocess.check_output(['/sbin/mount'], text=True, timeout=10)
            if marker in table:
                raise RuntimeError('Mount remains')
            point.rmdir()
            root.rmdir()
            report['cleanup_verified'] = True
        except Exception:
            report['failures'].append('Cleanup unverified; preserved ' + str(root))
    if config.get('report'):
        Path(config['report']).write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({'failures': report['failures'], 'cleanup_verified': report['cleanup_verified']}))
    return 1 if report['failures'] else 0


if __name__ == '__main__':
    sys.exit(main())
