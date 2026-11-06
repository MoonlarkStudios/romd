#!/usr/bin/env python3
"""Exercise the selected engine with owned disposable resources; leave developer data alone."""
import json
from pathlib import Path
import shutil
import socket
import subprocess
import tempfile
import uuid

from runtime import ContainerError, ROOT, Runtime


def smoke():
    runtime = Runtime()
    print(json.dumps(runtime.doctor()), flush=True)
    environment = runtime.child_environment(tests=True)
    project = 'romd-container-smoke-' + uuid.uuid4().hex[:12]
    volume = project + '-data'
    with tempfile.TemporaryDirectory(prefix='romd-container-smoke-') as directory:
        work = Path(directory)
        context = work / 'build'
        context.mkdir()
        (context / 'Containerfile').write_text('FROM scratch\nCOPY . /context/\n')
        shutil.copyfile(ROOT / '.dockerignore', context / '.dockerignore')
        for name in ('.env', '.mise.local.toml'):
            (context / name).write_text('private-local-fixture')
        (context / 'sentinel').write_text('public-build-input')
        subprocess.run(runtime.engine_command('build', '--file', str(context / 'Containerfile'),
                        '--output', f'type=local,dest={work / "output"}', str(context)),
                       env=environment, check=True)
        output = work / 'output/context'
        assert (output / 'sentinel').read_text() == 'public-build-input'
        assert not (output / '.env').exists() and not (output / '.mise.local.toml').exists()
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        image = 'docker.io/library/postgres:18-bookworm'
        model = {'services': {
            'postgres': {'image': image,
                'environment': {'POSTGRES_USER': 'romd', 'POSTGRES_DB': 'romd', 'POSTGRES_HOST_AUTH_METHOD': 'trust'},
                'ports': [f'127.0.0.1:{port}:5432'],
                'volumes': [f'data:/var/lib/postgresql'],
                'healthcheck': {'test': ['CMD-SHELL', 'pg_isready -U romd -d romd'], 'interval': '1s', 'timeout': '3s', 'retries': 60}},
            'client': {'image': image, 'init': True, 'user': '1654:1654',
                'environment': {'PGHOST': 'postgres', 'PGUSER': 'romd', 'PGDATABASE': 'romd'},
                'tmpfs': ['/run/romd:mode=1777', '/var/lib/postgresql'],
                'depends_on': {'postgres': {'condition': 'service_healthy'}},
                'command': ['sh', '-ec', 'touch /run/romd/ready; exec sleep infinity'],
                'healthcheck': {'test': ['CMD-SHELL', 'test -f /run/romd/ready && psql -qtAc "SELECT 1" | grep -qx 1'], 'interval': '1s', 'timeout': '3s', 'retries': 30}}},
            'volumes': {'data': {'external': True, 'name': volume}}}
        config = work / 'compose.json'
        config.write_text(json.dumps(model))
        compose = runtime.compose_command() + ['-p', project, '-f', str(config)]

        def run(arguments, capture=False):
            return subprocess.run(arguments, env=environment, check=True, text=True,
                                  stdout=subprocess.PIPE if capture else None, timeout=180)

        run(runtime.engine_command('volume', 'create', '--label', f'com.romd.container-smoke={project}', volume))
        try:
            run(compose + ['up', '--no-build', '-d', '--wait', '--wait-timeout', '120'])
            with socket.create_connection(('127.0.0.1', port), timeout=5):
                pass
            query = compose + ['exec', '-T', 'client', 'psql', '-v', 'ON_ERROR_STOP=1', '-qtAc']
            run(query + ["CREATE TABLE sentinel(value text); INSERT INTO sentinel VALUES ('persisted');"])
            run(compose + ['down', '--volumes', '--timeout', '10'])
            run(compose + ['up', '--no-build', '-d', '--wait', '--wait-timeout', '120'])
            assert run(query + ['SELECT value FROM sentinel'], capture=True).stdout.strip() == 'persisted'
            assert len(run(compose + ['ps', '--status', 'running', '--quiet'], capture=True).stdout.splitlines()) == 2
        finally:
            # --volumes removes only this project's anonymous volumes; the external
            # sentinel volume is explicitly owned here and removed separately.
            try:
                run(compose + ['down', '--volumes', '--timeout', '10'])
            finally:
                run(runtime.engine_command('volume', 'rm', volume))
    print('PASS container builds, private-context exclusions, health ordering, DNS, loopback, non-root init, persistence and owned cleanup.')


if __name__ == '__main__':
    try:
        smoke()
    except (ContainerError, subprocess.SubprocessError, OSError, AssertionError) as error:
        raise SystemExit(f'Container smoke failed: {error}')
