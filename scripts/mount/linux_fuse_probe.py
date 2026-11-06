#!/usr/bin/env python3
"""Opt-in Linux rclone/FUSE per-user isolation probe in a disposable Docker container.

Copies linux_fuse_probe_container.py into a pinned Debian image granted /dev/fuse and
CAP_SYS_ADMIN, runs it, writes its JSON report, and always removes the container.
Exit 0 when every expectation held and the container was removed, 1 otherwise.
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys

from linux_fuse_probe_container import MARKER

IMAGE = 'debian:bookworm-slim@sha256:3783cc01769c7b2b1b83a5c5ad96c815348e28ed7da68e2e3687004faa906251'
CONTAINER_SCRIPT = Path(__file__).with_name('linux_fuse_probe_container.py')
BOOTSTRAP = ('export DEBIAN_FRONTEND=noninteractive; apt-get update -qq >/dev/null && '
             'apt-get install -y -qq --no-install-recommends rclone fuse3 python3 >/dev/null && '
             'exec python3 /romd-linux-fuse-probe.py')


def parse_report(output):
    line = next((line for line in output.splitlines() if line.startswith(MARKER)), None)
    if line is None:
        raise ValueError('Container produced no report')
    return json.loads(line[len(MARKER):])


def docker(docker_binary, *arguments, timeout=900):
    return subprocess.run([docker_binary, *arguments], capture_output=True, text=True, timeout=timeout)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--report', required=True, help='path for the JSON report')
    parser.add_argument('--image', default=IMAGE, help='container image (default: pinned Debian bookworm-slim)')
    parser.add_argument('--docker', default='docker', help='docker-compatible CLI')
    arguments = parser.parse_args()

    created = docker(arguments.docker, 'create', '--device', '/dev/fuse', '--cap-add', 'SYS_ADMIN',
                     '--security-opt', 'apparmor=unconfined', arguments.image, 'bash', '-c', BOOTSTRAP)
    if created.returncode:
        raise SystemExit(f'docker create failed: {created.stderr.strip()}')
    container = created.stdout.strip()
    try:
        copied = docker(arguments.docker, 'cp', str(CONTAINER_SCRIPT), f'{container}:/romd-linux-fuse-probe.py')
        if copied.returncode:
            raise SystemExit(f'docker cp failed: {copied.stderr.strip()}')
        output = docker(arguments.docker, 'start', '-a', container).stdout
    finally:
        docker(arguments.docker, 'rm', '-f', container)
    removed = docker(arguments.docker, 'ps', '-a', '-q', '--no-trunc', '--filter', f'id={container}').stdout.strip() == ''
    server = docker(arguments.docker, 'version', '--format', '{{.Server.Version}} {{.Server.Os}}/{{.Server.Arch}}')
    report = dict(image=arguments.image, docker_server=server.stdout.strip(), **parse_report(output),
                  container_removed=removed)
    Path(arguments.report).write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(dict(passed=report['passed'], container_removed=removed, error=report.get('error'))))
    return 0 if report['passed'] and removed else 1


if __name__ == '__main__':
    sys.exit(main())
