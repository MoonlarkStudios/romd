#!/usr/bin/env python3
"""Run local development containers without a Docker alias or global socket overrides."""
import http.client
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]


class ContainerError(RuntimeError):
    pass


def capture(command, environment):
    try:
        result = subprocess.run(command, env=environment, capture_output=True, text=True,
                                timeout=30, check=False)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ContainerError(f'{Path(command[0]).name} could not finish; check installation and runtime access.') from error
    if result.returncode:
        raise ContainerError(f'{Path(command[0]).name} failed; check the selected runtime and its local logs.')
    return result.stdout


def socket_version(path):
    connection = http.client.HTTPConnection('localhost', timeout=5)
    try:
        connection.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        connection.sock.settimeout(5)
        connection.sock.connect(path)
        connection.request('GET', '/version')
        response = connection.getresponse()
        if response.status != 200:
            raise ValueError('API version request failed')
        return json.loads(response.read())
    except (OSError, ValueError, http.client.HTTPException) as error:
        raise ContainerError('Podman API is unavailable. On native Linux run systemctl --user enable --now podman.socket; see docs/development/getting-started.md.') from error
    finally:
        connection.close()


class Runtime:
    def __init__(self, environment=None):
        self.environment = dict(os.environ if environment is None else environment)
        selected = self.environment.get('ROMD_CONTAINER_ENGINE', '')
        if selected and selected not in ('docker', 'podman'):
            raise ContainerError('ROMD_CONTAINER_ENGINE must be docker or podman.')
        available = [name for name in ('docker', 'podman') if shutil.which(name, path=self.environment.get('PATH'))]
        if not selected:
            if len(available) != 1:
                raise ContainerError('Select ROMD_CONTAINER_ENGINE=docker or podman in .mise.local.toml; install a container engine if neither is available.')
            selected = available[0]
        if selected not in available:
            raise ContainerError(f'Selected engine {selected} is missing. Install it or correct ROMD_CONTAINER_ENGINE; no fallback was attempted.')
        self.engine = selected
        self._info = None
        self._child_environment = None
        self._compose = None

    def info(self):
        if self._info is not None:
            return self._info.copy()
        try:
            data = json.loads(capture([self.engine, 'info', '--format', '{{json .}}'], self.environment))
            if self.engine == 'docker':
                self._info = {'engine': self.engine, 'version': data['ServerVersion']}
            else:
                host = data['host']
                if host['os'] != 'linux' or host.get('serviceIsRemote') or not host['security']['rootless']:
                    raise ContainerError('Podman development currently requires native rootless Linux (including WSL2 Linux). Use Docker on macOS; Podman Machine is not qualified.')
                self._info = {'engine': self.engine, 'version': data['version']['Version'],
                              'socket': host['remoteSocket']['path']}
            return self._info.copy()
        except (KeyError, ValueError, TypeError) as error:
            raise ContainerError('Cannot read selected runtime information; check its version and local logs.') from error

    def child_environment(self, *, tests=False):
        if self._child_environment is None:
            self._child_environment = self._resolve_environment()
        environment = self._child_environment.copy()
        if tests and self.engine == 'podman':
            override = environment.get('TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE')
            path = environment['DOCKER_HOST'].removeprefix('unix://')
            if override and Path(override).resolve() != Path(path):
                raise ContainerError('Testcontainers socket override disagrees with the selected Podman endpoint.')
            environment['TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE'] = path
        if tests and environment.get('TESTCONTAINERS_RYUK_DISABLED', '').lower() == 'true':
            raise ContainerError('Remove TESTCONTAINERS_RYUK_DISABLED before running supported container checks; cleanup must stay enabled.')
        return environment

    def _resolve_environment(self):
        environment = self.environment.copy()
        environment['ROMD_CONTAINER_ENGINE'] = self.engine
        if self.engine == 'podman':
            info = self.info()
            endpoint = environment.get('DOCKER_HOST') or 'unix://' + info['socket']
            if not endpoint.startswith('unix://'):
                raise ContainerError('Local Podman development requires a Unix socket; remove the conflicting DOCKER_HOST.')
            native_path = Path(info['socket']).resolve()
            selected_path = Path(endpoint.removeprefix('unix://'))
            if not selected_path.is_absolute() or selected_path.resolve() != native_path:
                raise ContainerError('DOCKER_HOST selects a different Podman instance from the native CLI; remove the override or use the socket reported by podman info.')
            endpoint = 'unix://' + str(native_path)
            version = socket_version(str(native_path))
            if not any(component.get('Name') == 'Podman Engine' for component in version.get('Components', [])):
                raise ContainerError('Selected Podman endpoint belongs to another engine; correct DOCKER_HOST before continuing.')
            environment.pop('DOCKER_CONTEXT', None)
            environment['DOCKER_HOST'] = endpoint
            # Qualified with the pinned provider and ROMD's actual Dockerfile.
            # Select the compatible API builder explicitly, avoiding Buildx fallback.
            environment['DOCKER_BUILDKIT'] = '0'
        return environment

    def engine_command(self, *arguments):
        self.child_environment()  # Validate before native mutations as well as API calls.
        return [self.engine, *arguments]

    def compose_command(self, *arguments):
        command = self.compose_client(*arguments)
        self.child_environment()
        return command

    def compose_client(self, *arguments):
        """Resolve the Compose client without contacting the engine.

        Only for engine-independent operations such as `config`; anything that
        creates, builds or inspects containers must use compose_command.
        """
        if self._compose is None:
            self._compose = self._compose_provider()
        return [*self._compose, *arguments]

    def _compose_provider(self):
        provider = self.environment.get('ROMD_COMPOSE_PROVIDER') or shutil.which('docker-compose', path=self.environment.get('PATH'))
        if provider:
            executable = shutil.which(provider, path=self.environment.get('PATH'))
            if not executable:
                raise ContainerError('ROMD_COMPOSE_PROVIDER must identify an executable Docker Compose client.')
            return [executable]
        if self.engine == 'docker':
            return ['docker', 'compose']
        raise ContainerError('Docker Compose client is missing: run mise install. podman-compose is not a compatible provider for ROMD config --format json.')

    def doctor(self, root=ROOT):
        info = self.info()
        environment = self.child_environment(tests=True)
        compose = self.compose_command()
        version = capture(compose + ['version', '--short'], environment).strip()
        try:
            model = json.loads(capture(compose + ['-f', str(root / 'compose.dev.yaml'), 'config', '--format', 'json'], environment))
            if 'services' not in model:
                raise ValueError('services missing')
        except ValueError as error:
            raise ContainerError('Compose must support config --format json; use the pinned Docker Compose client.') from error
        for command, options in (('up', ('--wait', '--wait-timeout')), ('ps', ('--status', '--quiet'))):
            help_text = capture(compose + [command, '--help'], environment)
            if any(option not in help_text for option in options):
                raise ContainerError('Compose lacks required development options; run mise install.')
        return info | {'composeVersion': version}


def main():
    if len(sys.argv) < 2:
        raise ContainerError('Usage: runtime.py doctor | engine ARGS | compose ARGS | build-compose CONFIG [SERVICES] | test COMMAND')
    operation, *arguments = sys.argv[1:]
    runtime = Runtime()
    if operation == 'doctor':
        print(json.dumps(runtime.doctor()))
        return 0
    if not arguments:
        raise ContainerError('A container command is required.')
    if operation == 'build-compose':
        command = runtime.compose_command('-f', arguments[0], 'build', *arguments[1:])
        environment = runtime.child_environment()
    elif operation == 'compose':
        command, environment = runtime.compose_command(*arguments), runtime.child_environment()
    elif operation == 'engine':
        command, environment = runtime.engine_command(*arguments), runtime.child_environment()
    elif operation == 'test':
        command, environment = arguments, runtime.child_environment(tests=True)
    else:
        raise ContainerError('Unknown container operation.')
    os.execvpe(command[0], command, environment)


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (ContainerError, OSError) as error:
        # Tool stderr can contain credentials. Child build output is already local.
        print(f'Container setup: {error}' if isinstance(error, ContainerError) else 'Container command failed; inspect local diagnostics.', file=sys.stderr)
        sys.exit(1)
