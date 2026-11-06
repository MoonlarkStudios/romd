#!/usr/bin/env python3
"""In-container half of the Linux rclone/FUSE per-user isolation probe.

Runs as root inside a disposable container started by linux_fuse_probe.py. A private
`server` account serves authenticated WebDAV on loopback (standing in for ROMD); `alice`
mounts it with rclone/FUSE using default options; `bob` and root attempt access, and
alice attempts to widen access with --allow-other. Prints one marker line carrying the
JSON report. `owner <mount>` runs the owner-side checks and is invoked as alice.
"""
import errno
import hashlib
import json
import os
from pathlib import Path
import random
import shutil
import socket
import subprocess
import sys
import time

MIB = 1024 * 1024
MARKER = 'ROMD_LINUX_FUSE_REPORT '
FIXTURES = {'small.bin': 20 * 1024, 'medium.bin': 4 * MIB + 1000, 'large.bin': 64 * MIB + 1000}
EXPECTED = Path('/expected')
LIBRARY = Path('/srv/romd-data')
PRIVATE = Path('/home/alice/.romd/server1/user1/vfs')
OPEN = Path('/srv/open-mounts/alice')
WIDE = Path('/srv/open-mounts/alice-wide')
PORT = 18080
SECRET = 'fixture-secret'


def attempt(label, action):
    try:
        action()
        return dict(action=label, denied=False)
    except OSError as error:
        return dict(action=label, denied=True, errno=errno.errorcode.get(error.errno, error.errno))


def owner_checks(mount):
    reads = []
    for name in FIXTURES:
        source = (EXPECTED / name).read_bytes()
        size, randomizer = len(source), random.Random(name)
        cases = [(size - 1000, 5000), (size - 1000, 100000), (size - 4096, 4096), (size, 4096)]
        cases += [(randomizer.randrange(max(1, size - 70000)), randomizer.choice([4096, 32768, 65536]) + 7)
                  for _ in range(16)]
        with open(mount / name, 'rb', buffering=0) as stream:
            for offset, length in cases:
                data = os.pread(stream.fileno(), length, offset)
                reads.append(dict(file=name, offset=offset, requested=length, returned=len(data),
                                  exact=data == source[offset:offset + length]))
    large = (EXPECTED / 'large.bin').read_bytes()
    digest = hashlib.sha256()
    with open(mount / 'large.bin', 'rb', buffering=0) as stream:
        while data := stream.read(MIB):
            digest.update(data)
    copy = Path('/tmp/alice-copy.bin')
    shutil.copyfile(mount / 'large.bin', copy)
    copy_exact = copy.read_bytes() == large
    copy.unlink()
    return dict(
        reads=dict(count=len(reads), exact=all(r['exact'] for r in reads), failures=[r for r in reads if not r['exact']]),
        sequential_sha256_matches=digest.digest() == hashlib.sha256(large).digest(),
        copy_exact=copy_exact,
        mutations=[
            attempt('create', lambda: (mount / 'new.txt').open('xb').close()),
            attempt('open for write', lambda: (mount / 'medium.bin').open('r+b').close()),
            attempt('unlink', lambda: (mount / 'small.bin').unlink()),
            attempt('rename', lambda: (mount / 'small.bin').rename(mount / 'renamed.bin')),
            attempt('mkdir', lambda: (mount / 'newdir').mkdir()),
            attempt('chmod', lambda: (mount / 'small.bin').chmod(0o666)),
        ])


def run(argv, user=None, timeout=60):
    command = (['runuser', '-u', user, '--'] if user else []) + argv
    result = subprocess.run(command, capture_output=True, timeout=timeout)
    return result.returncode, result.stdout, result.stderr.decode(errors='replace').strip()


def fuse_mounts():
    return [line for line in Path('/proc/mounts').read_text().splitlines() if ' fuse' in line]


def is_mounted(path):
    return any(f' {path} fuse' in line for line in fuse_mounts())


def prepare():
    for account in ('server', 'alice', 'bob'):
        run(['useradd', '-m', '-s', '/bin/bash', account])
    LIBRARY.mkdir(parents=True, mode=0o700)
    EXPECTED.mkdir()
    for name, size in FIXTURES.items():
        data = random.Random(name).randbytes(size)
        (LIBRARY / name).write_bytes(data)
        (EXPECTED / name).write_bytes(data)
        (LIBRARY / name).chmod(0o600)
        (EXPECTED / name).chmod(0o644)
    shutil.chown(LIBRARY, 'server', 'server')
    for name in FIXTURES:
        shutil.chown(LIBRARY / name, 'server', 'server')
    run(['install', '-d', '-m', '0700', '/home/alice/.romd'], user='alice')
    run(['install', '-d', '-m', '0700', str(PRIVATE)], user='alice')
    run(['install', '-d', '-m', '0755', '/srv/open-mounts'])
    for path in (OPEN, WIDE):
        run(['install', '-d', '-m', '0755', '-o', 'alice', '-g', 'alice', str(path)])


def remote_arguments():
    obscured = subprocess.run(['rclone', 'obscure', SECRET], capture_output=True, text=True).stdout.strip()
    return [':webdav:', '--webdav-url', f'http://127.0.0.1:{PORT}', '--webdav-user', 'romd',
            '--webdav-pass', obscured, '--config', '/dev/null']


def start_server():
    server = subprocess.Popen(['runuser', '-u', 'server', '--', 'rclone', 'serve', 'webdav', str(LIBRARY),
                               '--addr', f'127.0.0.1:{PORT}', '--read-only', '--user', 'romd', '--pass', SECRET],
                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(100):
        with socket.socket() as probe:
            if probe.connect_ex(('127.0.0.1', PORT)) == 0:
                return server
        time.sleep(.1)
    raise TimeoutError('WebDAV fixture server did not start')


def mount_as_alice(path, remote):
    process = subprocess.Popen(['runuser', '-u', 'alice', '--', 'rclone', 'mount', *remote, str(path), '--read-only'],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(100):
        if is_mounted(path):
            return process
        time.sleep(.1)
    return process


def access_probe(user, label, argv):
    code, stdout, stderr = run(argv, user=None if user == 'root' else user)
    return dict(user=user, action=label, exit=code, bytes_returned=len(stdout),
                denied=code != 0 and not stdout and 'Permission denied' in stderr, message=stderr[:200])


def widen_attempt(remote):
    code, _, stderr = run(['rclone', 'mount', *remote, str(WIDE), '--read-only', '--allow-other'], user='alice', timeout=30)
    return dict(exit=code, refused=code != 0 and not is_mounted(WIDE),
                message=next((line for line in stderr.splitlines() if 'allow_other' in line), stderr[-200:]))


def verdict(report):
    owner_ok = all(o.get('reads', {}).get('exact') and o.get('sequential_sha256_matches') and o.get('copy_exact')
                   and all(m['denied'] for m in o.get('mutations', [])) for o in report.get('owner', {}).values())
    return bool(report.get('mounted') and all(report['mounted'].values()) and len(report.get('owner', {})) == 2
                and owner_ok and report.get('access') and all(p['denied'] for p in report['access'])
                and report.get('widen', {}).get('refused') and report.get('cleanup_verified'))


def orchestrate():
    report = dict(kernel=os.uname().release,
                  rclone=subprocess.run(['rclone', 'version'], capture_output=True, text=True).stdout.splitlines()[0],
                  fuse_conf_user_allow_other=any(line.strip().startswith('user_allow_other')
                                                 for line in Path('/etc/fuse.conf').read_text().splitlines()))
    processes = []
    try:
        prepare()
        processes.append(start_server())
        remote = remote_arguments()
        processes += [mount_as_alice(PRIVATE, remote), mount_as_alice(OPEN, remote)]
        report['mounted'] = {str(PRIVATE): is_mounted(PRIVATE), str(OPEN): is_mounted(OPEN)}
        report['mount_lines'] = fuse_mounts()
        report['owner'] = {}
        for path in (PRIVATE, OPEN):
            code, stdout, stderr = run(['python3', __file__, 'owner', str(path)], user='alice', timeout=300)
            report['owner'][str(path)] = json.loads(stdout) if code == 0 else dict(error=stderr[-400:])
        report['access'] = [
            access_probe('bob', 'list world-traversable mount', ['ls', '-la', str(OPEN)]),
            access_probe('bob', 'stat file on world-traversable mount', ['stat', str(OPEN / 'medium.bin')]),
            access_probe('bob', 'read file on world-traversable mount', ['head', '-c', '16', str(OPEN / 'medium.bin')]),
            access_probe('bob', 'list ~/.romd', ['ls', '-la', '/home/alice/.romd']),
            access_probe('bob', 'read file on private mount', ['head', '-c', '16', str(PRIVATE / 'medium.bin')]),
            access_probe('root', 'list world-traversable mount', ['ls', '-la', str(OPEN)]),
            access_probe('root', 'read file on world-traversable mount', ['head', '-c', '16', str(OPEN / 'medium.bin')]),
        ]
        report['widen'] = widen_attempt(remote)
    except Exception as error:
        report['error'] = f'{type(error).__name__}: {error}'
    finally:
        for path in (OPEN, PRIVATE, WIDE):
            if is_mounted(path):
                run(['fusermount3', '-u', str(path)], user='alice')
        for process in processes:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
        report['cleanup_verified'] = not fuse_mounts()
    report['passed'] = verdict(report)
    print(MARKER + json.dumps(report), flush=True)


if __name__ == '__main__':
    if sys.argv[1:2] == ['owner']:
        print(json.dumps(owner_checks(Path(sys.argv[2]))))
    else:
        orchestrate()
