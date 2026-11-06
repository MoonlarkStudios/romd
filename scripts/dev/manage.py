#!/usr/bin/env python3
"""Restore prerequisites, diagnose setup, and own one isolated development run."""
import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import signal
import socket
import subprocess
import sys
import time
import tomllib
from urllib.error import URLError
from urllib.request import Request, urlopen
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
from containers.runtime import ContainerError, Runtime

HOSTS = {name: f'src/Romd.{name.title()}.Host' for name in ('worker', 'admin', 'consumer')}
PORTALS = {'admin': 'romd-admin-app', 'consumer': 'romd-consumer-app', 'player': 'romd-player-app'}
SHUTDOWN_SIGNALS = (signal.SIGINT, signal.SIGTERM, *((signal.SIGHUP,) if hasattr(signal, 'SIGHUP') else ()))


SetupError = ContainerError
STATUS_SCHEMA_VERSION = 2
RESTART_TRIGGER = '.data/dev/restart-backend'
IMAGE_SERVICES = ('romd-worker', 'romd-admin', 'romd-consumer', 'romd-player')
BUILD_ERROR = re.compile(r'\berror (?:CS|MSB|NU|NETSDK)\d+\b.*')
WEB_DEPENDENCY_MARKER = '.romd-bootstrap-lock'


class HostFailure(SetupError):
    """An owned build or host failed; source mode can recover on the next edit."""

    def __init__(self, message, component, log=None, details=()):
        super().__init__(message)
        self.component = component
        self.log = log
        self.details = list(details)


class ProcessExited(HostFailure):
    pass


def log_tail(path, lines=5):
    try:
        text = Path(path).read_text(errors='replace')
    except OSError:
        return []
    return [line for line in text.splitlines() if line.strip()][-lines:]


def build_errors(log, root=ROOT, limit=3):
    """Return the first distinct compiler/MSBuild errors from a build log."""
    try:
        text = Path(log).read_text(errors='replace')
    except OSError:
        return []
    prefix = str(Path(root).resolve()) + os.sep
    found = []
    for line in text.splitlines():
        match = BUILD_ERROR.search(line)
        if not match:
            continue
        location = line[:match.start()].strip().rstrip(':').strip().replace(prefix, '')
        message = re.sub(r'\s+\[[^\]]+\]$', '', match.group(0)).strip()
        entry = f'{location}: {message}' if location else message
        if entry not in found:
            found.append(entry)
            if len(found) == limit:
                break
    return found


def failure_fields(error):
    """Short, credential-free status fields for a failed component."""
    message = str(error)
    fields = {'error': message[:1000]}
    if isinstance(error, HostFailure):
        if error.details and not isinstance(error, ProcessExited):
            message = f'{message} {"; ".join(error.details)}'
        fields = {'error': message[:1000], 'failedComponent': error.component}
        if error.log:
            fields['log'] = str(error.log)
    return fields


def print_failure(prefix, error, suffix=None):
    print(f'{prefix}: {error}', flush=True)
    for line in getattr(error, 'details', ()):
        print(f'  {line}', flush=True)
    if suffix:
        print(suffix, flush=True)


@contextmanager
def uninterrupted_cleanup():
    # Terminal Ctrl-C and a task runner can both signal us. Complete ownership
    # cleanup before allowing a repeated signal to interrupt the controller.
    previous = {number: signal.signal(number, signal.SIG_IGN) for number in SHUTDOWN_SIGNALS}
    try:
        yield
    finally:
        for number, handler in previous.items():
            signal.signal(number, handler)


def tool_versions(root=ROOT):
    return tomllib.loads((root / '.mise.toml').read_text())['tools']


def call(command, *, cwd=ROOT, env=None, timeout=300, capture=False):
    """Do not include arbitrary tool stderr or environment values in setup errors."""
    try:
        result = subprocess.run(command, cwd=cwd, env=env, timeout=timeout, check=False,
                                stdout=subprocess.PIPE if capture else None,
                                stderr=subprocess.PIPE if capture else None, text=True)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise SetupError(f'{command[0]} could not finish; check installation, access, and connectivity.') from error
    if result.returncode:
        raise SetupError(f'{command[0]} exited {result.returncode}; check its local logs and setup prerequisites.')
    return result.stdout if capture else None


def check_tools(root=ROOT, console=False):
    expected = tool_versions(root)
    for tool in ('dotnet', 'node', 'pnpm'):
        if not shutil.which(tool):
            raise SetupError(f'{tool} is missing: run mise install, then use mise exec or mise run.')
        actual = call([tool, '--version'], cwd=root, capture=True).strip().removeprefix('v')
        if actual != expected[tool]:
            raise SetupError(f'{tool} version does not match {expected[tool]}: run mise install and mise exec.')
    call([sys.executable, str(root / 'scripts/ci/toolchain.py'), '--configuration-only'], cwd=root,
         capture=True)
    if console:
        version = tomllib.loads((root / 'clients/romd_console/.mise.toml').read_text())['tools']['flutter']
        sdk = Path(call(['mise', 'where', f'flutter@{version}'], cwd=root, capture=True).strip())
        actual = json.loads(call([str(sdk / 'bin/flutter'), '--version', '--machine'],
                                 cwd=root / 'clients/romd_console', capture=True))['frameworkVersion']
        if actual != version:
            raise SetupError('Flutter version does not match the console pin; run bootstrap --console.')


def bootstrap(root=ROOT, console=False):
    call(['mise', 'install', *(f'{tool}@{version}' for tool, version in tool_versions(root).items())], cwd=root)
    check_tools(root)
    call(['dotnet', 'tool', 'restore'], cwd=root)
    call(['dotnet', 'restore'], cwd=root)
    environment = os.environ | {'CI': 'true'}
    call(['pnpm', 'install', '--frozen-lockfile'], cwd=root / 'web', env=environment)
    record_web_dependencies(root)
    if console:
        version = tomllib.loads((root / 'clients/romd_console/.mise.toml').read_text())['tools']['flutter']
        call(['mise', 'install', f'flutter@{version}'], cwd=root)
        call(['mise', 'exec', f'flutter@{version}', '--', 'flutter', 'pub', 'get', '--enforce-lockfile'],
             cwd=root / 'clients/romd_console', env=environment)
    print('Dependencies restored. Existing databases and local configuration were not reset.')


def lockfile_digest(root=ROOT):
    return hashlib.sha256((root / 'web/pnpm-lock.yaml').read_bytes()).hexdigest()


def record_web_dependencies(root=ROOT):
    modules = root / 'web/node_modules'
    if modules.is_dir() and (root / 'web/pnpm-lock.yaml').is_file():
        (modules / WEB_DEPENDENCY_MARKER).write_text(lockfile_digest(root) + '\n')


def check_web_dependencies(root=ROOT):
    """Cheap, side-effect-free check that web/node_modules matches the frozen lockfile.

    pnpm keeps a copy of the lockfile it installed at node_modules/.pnpm/lock.yaml;
    bootstrap additionally records the digest of the lockfile it installed from.
    """
    modules = root / 'web/node_modules'
    if not (modules / '.modules.yaml').is_file():
        raise SetupError('Web dependencies are not installed: run `mise run bootstrap`.')
    try:
        expected = (root / 'web/pnpm-lock.yaml').read_bytes()
    except OSError as error:
        raise SetupError('web/pnpm-lock.yaml is unreadable; restore it from Git.') from error
    try:
        if (modules / '.pnpm/lock.yaml').read_bytes() == expected:
            return
    except OSError:
        pass
    try:
        if (modules / WEB_DEPENDENCY_MARKER).read_text().strip() == hashlib.sha256(expected).hexdigest():
            return
    except OSError:
        pass
    raise SetupError('Web dependencies are stale relative to web/pnpm-lock.yaml: run `mise run bootstrap`.')


def check_dotnet_tools(root=ROOT, environment=None):
    """Detect an unrestored repository tool manifest from the NuGet package folder."""
    environment = os.environ if environment is None else environment
    manifest = root / '.config/dotnet-tools.json'
    if not manifest.is_file():
        return
    try:
        tools = json.loads(manifest.read_text()).get('tools', {})
    except (OSError, ValueError) as error:
        raise SetupError('.config/dotnet-tools.json is unreadable; restore it from Git.') from error
    packages = Path(environment.get('NUGET_PACKAGES') or Path.home() / '.nuget/packages')
    missing = sorted(name for name, tool in tools.items()
                     if not (packages / name.lower() / str(tool.get('version', '')).lower()).is_dir())
    if missing:
        raise SetupError(f'Repository .NET tools are not restored ({", ".join(missing)}): run `mise run bootstrap`.')


def check_dependencies(root=ROOT):
    check_web_dependencies(root)
    check_dotnet_tools(root)


def checkout_id(root=ROOT):
    return hashlib.sha256(str(root.resolve()).encode()).hexdigest()[:12]


def linked_worktree(root):
    if not (root / '.git').is_file():
        return False
    paths = call(['git', 'rev-parse', '--path-format=absolute', '--git-dir', '--git-common-dir'],
                 cwd=root, capture=True).splitlines()
    return len(paths) == 2 and paths[0] != paths[1]


def port_settings(mode, environment=None, root=ROOT):
    environment = os.environ if environment is None else environment
    defaults = {'admin_api': ('ROMD_DEV_ADMIN_PORT', 5000 if mode == 'source' else 11337),
                'consumer_api': ('ROMD_DEV_CONSUMER_PORT', 5002 if mode == 'source' else 11338),
                'admin_web': ('ROMD_DEV_ADMIN_WEB_PORT', 5137),
                'consumer_web': ('ROMD_DEV_CONSUMER_WEB_PORT', 5174),
                'player': ('ROMD_DEV_PLAYER_PORT', 5175)}
    base = environment.get('ROMD_DEV_PORT_BASE')
    if base is None and linked_worktree(root):
        base = 20000 + (int(checkout_id(root)[:8], 16) % 8000) * 5
    if base is not None:
        if not str(base).isdecimal() or not 1024 <= int(base) <= 65531:
            raise SetupError('ROMD_DEV_PORT_BASE must be from 1024 to 65531 (five consecutive ports).')
        defaults = {name: (variable, int(base) + index)
                    for index, (name, (variable, _)) in enumerate(defaults.items())}
    ports = {}
    for name, (variable, default) in defaults.items():
        value = str(environment.get(variable, default))
        if not value.isdecimal() or not 1024 <= int(value) <= 65535:
            raise SetupError(f'{variable} must be a port from 1024 to 65535.')
        ports[name] = int(value)
    if len(set(ports.values())) != len(ports):
        raise SetupError('ROMD_DEV_* port selections must be distinct.')
    return ports


def required_ports(mode, root=ROOT):
    return list(port_settings(mode, root=root).values())


def check_ports(mode, root=ROOT):
    for port in required_ports(mode, root):
        for family, address in ((socket.AF_INET, '127.0.0.1'), (socket.AF_INET6, '::1')):
            with socket.socket(family, socket.SOCK_STREAM) as listener:
                if os.name != 'nt':
                    # Match normal server rebinding after shutdown. SO_REUSEADDR
                    # accepts TIME_WAIT without sharing an active listening port.
                    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
                try:
                    listener.bind((address, port))
                except OSError as error:
                    if family == socket.AF_INET6 and error.errno in (47, 97, 99):
                        continue
                    raise SetupError(f'Port {port} is unavailable: choose a free ROMD_DEV_PORT_BASE or ROMD_DEV_* ports; leave unrelated owners running.') from error


def check_containers(root=ROOT):
    runtime = Runtime()
    info = runtime.doctor(root)
    print(f'Container engine: {info["engine"]} {info["version"]}; Compose {info["composeVersion"]}')
    return runtime


def source_data_directory(root=ROOT, environment=None):
    environment = os.environ if environment is None else environment
    selections = {value for key, value in environment.items()
                  if key.lower().replace('__', ':') == 'romd:datadirectory' and value}
    if len(selections) > 1:
        raise SetupError('Environment settings disagree on durable data paths. Select one shared Romd__DataDirectory before startup.')
    selected = next(iter(selections), None)
    if selected:
        if not Path(selected).is_absolute():
            raise SetupError('Romd__DataDirectory must identify the existing absolute shared data path.')
        return str(Path(selected).resolve())
    configured = set()
    for project in HOSTS.values():
        value = None
        for filename in ('appsettings.json', 'appsettings.Development.json'):
            path = root / project / filename
            if not path.exists():
                continue
            try:
                document = json.loads(path.read_text())
                section = next((v for k, v in document.items() if k.lower() == 'romd'), {})
                current = next((v for k, v in section.items() if k.lower() == 'datadirectory'), None)
                flat = next((v for k, v in document.items() if k.lower() == 'romd:datadirectory'), None)
                if flat is not None:
                    value = flat
                elif current is not None:
                    value = current
            except (ValueError, AttributeError, OSError) as error:
                raise SetupError('Cannot inspect host settings safely. Set the existing shared Romd__DataDirectory explicitly; settings were not changed.') from error
        if value is not None:
            if not isinstance(value, str) or not value or not Path(value).is_absolute():
                raise SetupError('A host uses a relative data path. Set the existing absolute shared Romd__DataDirectory before managed startup.')
            configured.add(str(Path(value).resolve()))
    if len(configured) > 1:
        raise SetupError('Host settings disagree on durable data paths. Select the existing shared Romd__DataDirectory explicitly before startup.')
    return configured.pop() if configured else str(root.resolve() / '.data/romd')


def build_urls(root=ROOT):
    dockerfile = (root / 'Dockerfile').read_text()
    urls = re.findall(r'''https://(?:codeload\.github\.com|github\.com|www\.libsdl\.org)/[^\s\\'"]+''', dockerfile)
    # git clone uses this repository URL; HEAD on the GitHub repository is a
    # read-only visibility check, while pinned archive URLs check build inputs.
    resolved = set()
    for url in urls:
        if '$cmake' in url or '${cmake}' in url:
            for architecture in set(re.findall(r'cmake=([a-zA-Z0-9_]+)', dockerfile)):
                resolved.add(url.replace('${cmake}', architecture).replace('$cmake', architecture))
        elif '$' in url:
            raise SetupError('A Dockerfile build URL uses an unrecognized variable; update the setup source-access check.')
        else:
            resolved.add(url.removesuffix('.git'))
    return sorted(resolved)


def check_build_access(root=ROOT):
    for url in build_urls(root):
        try:
            with urlopen(Request(url, method='HEAD'), timeout=20) as response:
                if response.status != 200:
                    raise SetupError('A pinned source build input is inaccessible; review Dockerfile source URLs and repository visibility.')
        except (OSError, URLError) as error:
            raise SetupError('A pinned source build input is inaccessible; review Dockerfile source URLs and repository visibility.') from error


def check_test_tools():
    if sys.platform.startswith('linux') and not shutil.which('clang'):
        raise SetupError('Linux media tests require host clang; install the clang package. See docs/development/getting-started.md.')


def doctor(root=ROOT, mode='source', console=False, test_tools=False):
    checks = (
        ('tools and selection', lambda: check_tools(root, console)),
        ('restored web and .NET tool dependencies', lambda: check_dependencies(root)),
        *((('backend test prerequisites', check_test_tools),) if test_tools else ()),
        ('container engine, API and Compose', lambda: check_containers(root)),
        ('development ports', lambda: check_ports(mode, root)),
        *((('pinned companion/build source access', lambda: check_build_access(root)),) if mode != 'source' else ()),
        *((('source durable data pairing', lambda: source_data_directory(root)),) if mode == 'source' else ()),
    )
    failed = False
    for name, check in checks:
        try:
            check()
            print(f'PASS {name}')
        except SetupError as error:
            failed = True
            print(f'FAIL {name}: {error}')
    if mode != 'source':
        image_residue_note(root)
    return not failed


def image_residue_note(root=ROOT):
    try:
        legacy, _, dangling = owned_images(root, Runtime())
    except SetupError:
        return
    if legacy or dangling:
        print(f'NOTE {len(legacy)} legacy per-run and {len(dangling)} untagged development images belong to this '
              'checkout; reclaim them with `mise run dev:clean-images`.')


@contextmanager
def session_lock(root=ROOT):
    if os.name == 'nt':
        raise SetupError('Managed development requires a POSIX host; use the manual split-host commands on Windows.')
    import fcntl
    directory = root / '.data/dev'
    directory.mkdir(parents=True, exist_ok=True)
    with (directory / 'session.lock').open('a') as handle:
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise SetupError('A managed development run already owns this checkout; stop it before starting another.') from error
        try:
            yield
        finally:
            fcntl.flock(handle, fcntl.LOCK_UN)


def session_active(root):
    import fcntl
    path = root / '.data/dev/session.lock'
    if not path.exists():
        return False
    with path.open('r') as handle:
        try:
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return True
        fcntl.flock(handle, fcntl.LOCK_UN)
    return False


def write_session(root, record):
    path = root / '.data/dev/session.json'
    temporary = path.with_suffix('.tmp')
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    os.fchmod(descriptor, 0o600)
    with os.fdopen(descriptor, 'w') as handle:
        json.dump(record, handle, indent=2)
        handle.write('\n')
    temporary.replace(path)


def session_status(root=ROOT):
    path = root / '.data/dev/session.json'
    if path.exists():
        record = json.loads(path.read_text())
    else:
        # Ports are deterministic, so report where `mise run dev` will listen.
        record = {'schemaVersion': STATUS_SCHEMA_VERSION, 'state': 'not-started', 'mode': 'source'}
        try:
            record['urls'] = {name: f'http://localhost:{port}'
                              for name, port in port_settings('source', root=root).items()}
        except SetupError as error:
            record['error'] = str(error)
    record['active'] = session_active(root)
    if not record['active'] and record['state'] not in ('not-started', 'stopped', 'failed'):
        record['state'] = 'interrupted'
    return record


class Processes:
    def __init__(self, directory):
        self.directory = directory
        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.children = []

    def start(self, name, command, *, cwd=ROOT, env=None):
        log = (self.directory / f'{name}.log').open('w')
        try:
            child = subprocess.Popen(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT,
                                     start_new_session=True)
        finally:
            log.close()
        self.children.append((name, child))
        return child

    def exited(self):
        """Return the first owned (name, child) that has exited, or None."""
        for name, child in self.children:
            if child.poll() is not None:
                return name, child
        return None

    def assert_running(self):
        exited = self.exited()
        if exited:
            name, child = exited
            log = self.directory / f'{name}.log'
            raise ProcessExited(f'{name} exited {child.returncode}; inspect {log} locally.', name, log,
                                log_tail(log))

    def stop(self, grace=10):
        with uninterrupted_cleanup():
            self._stop(grace)

    def _stop(self, grace):
        # Each managed child has its own session. Never use global process-name kills.
        groups = {child.pid for _, child in self.children}
        for pid in groups:
            try:
                os.killpg(pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
        deadline = time.monotonic() + grace
        while any(group_exists(pid) for pid in groups) and time.monotonic() < deadline:
            for _, child in self.children:
                child.poll()  # Reap direct parents while independently tracking descendants.
            time.sleep(0.025)
        for pid in groups:
            if group_exists(pid):
                try:
                    os.killpg(pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
        for _, child in reversed(self.children):
            child.wait(timeout=5)
        deadline = time.monotonic() + 5
        while any(group_exists(pid) for pid in groups) and time.monotonic() < deadline:
            time.sleep(0.025)
        if any(group_exists(pid) for pid in groups):
            raise SetupError('An owned development process group survived shutdown; inspect local processes.')


def group_exists(pid):
    try:
        os.killpg(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def wait_for(condition, processes, label, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        processes.assert_running()
        if condition():
            return
        time.sleep(0.25)
    raise SetupError(f'Timed out waiting for {label}; inspect this run\'s local logs.')


def http_ready(url):
    try:
        with urlopen(url, timeout=2) as response:
            return response.status == 200
    except (OSError, URLError):
        return False


def docker_configuration(root, project, runtime):
    ports = port_settings('containers', root=root)
    environment = runtime.child_environment() | {'ROMD_DEV_DATA_MOUNT': 'romd-dev-data',
                                'ROMD_DEV_ADMIN_PORT': str(ports['admin_api']),
                                'ROMD_DEV_CONSUMER_PORT': str(ports['consumer_api']),
                                'ROMD_DEV_PLAYER_PORT': str(ports['player']),
                                'ROMD_DEV_PLAYER_URL': f'http://localhost:{ports["player"]}',
                                'ROMD_DEV_PLAYER_ALLOWED_PARENTS': f'http://localhost:{ports["consumer_web"]}'}
    content = call(runtime.compose_command('--project-directory', str(root), '-f',
                    str(root / 'compose.dev.yaml'), 'config', '--format', 'json'),
                   cwd=root, env=environment, capture=True, timeout=30)
    config = json.loads(content)
    config['name'] = project
    identity = checkout_id(root)
    for name, network in config.get('networks', {}).items():
        if network.get('external'):
            raise SetupError('Managed development refuses external networks; use the manual topology for shared networking.')
        network['name'] = f'{project}_{name}'
    for volume, declaration in config['volumes'].items():
        declaration.clear()
        declaration.update({'name': f'romd-dev-{identity}-{volume}', 'external': True})
    for name, service in config['services'].items():
        service['restart'] = 'no'
        service['labels'] = dict(service.get('labels', {})) | {'com.romd.dev.checkout': identity}
        if name != 'postgres':
            # One stable tag per checkout: rebuilds replace it and reuse the
            # build cache instead of accumulating per-run image copies.
            service['image'] = checkout_image(root, name)
            if isinstance(service.get('build'), dict):
                service['build']['labels'] = dict(service['build'].get('labels', {})) | {
                    'com.romd.dev.checkout': identity}
            service['init'] = True
        for binding in service.get('ports', []):
            binding['host_ip'] = '127.0.0.1'
        if name in ('romd-worker', 'romd-admin', 'romd-consumer'):
            service['environment'].update(origin_environment(ports))
    worker = config['services']['romd-worker']
    worker['environment']['Romd__ReadinessMarkerPath'] = '/run/romd/ready'
    worker['tmpfs'] = ['/run/romd:mode=1777']
    worker['healthcheck'] = {'test': ['CMD-SHELL', 'test -f /run/romd/ready'],
                           'interval': '2s', 'timeout': '3s', 'retries': 120}
    for name in ('romd-admin', 'romd-consumer'):
        service = config['services'][name]
        service['depends_on']['romd-worker']['condition'] = 'service_healthy'
        service['healthcheck'] = {'test': ['CMD', 'curl', '--fail', '--silent', '--max-time', '5',
                                          'http://localhost:8080/health/ready'],
                                  'interval': '2s', 'timeout': '7s', 'retries': 120}
    return config


def ensure_checkout_volumes(root, config, runtime):
    identity = checkout_id(root)
    environment = runtime.child_environment()
    existing = set(call(runtime.engine_command('volume', 'ls', '--format', '{{.Name}}'),
                        cwd=root, env=environment, capture=True).splitlines())
    for declaration in config['volumes'].values():
        name = declaration['name']
        if name not in existing:
            call(runtime.engine_command('volume', 'create', '--label', f'com.romd.dev.checkout={identity}', name),
                 cwd=root, env=environment, capture=True)
        label = call(runtime.engine_command('volume', 'inspect', '--format',
                      '{{index .Labels "com.romd.dev.checkout"}}', name), cwd=root, env=environment, capture=True).strip()
        if label != identity:
            raise SetupError('A selected development volume has a different owner; inspect the selected engine without resetting its data.')


def origin_environment(ports):
    environment = {}
    for name in ('admin', 'consumer'):
        origin = f'http://localhost:{ports[name + "_web"]}'
        environment[f'Romd__{name.title()}Host__CorsOrigins__0'] = origin
        environment[f'Romd__Auth__{name.title()}Spa__RedirectUris__0'] = origin + '/auth/callback'
        environment[f'Romd__Auth__{name.title()}Spa__PostLogoutRedirectUris__0'] = origin
    environment['Romd__BrowserPlayback__PlayerOrigins__0__Parent'] = f'http://localhost:{ports["consumer_web"]}'
    environment['Romd__BrowserPlayback__PlayerOrigins__0__Player'] = f'http://localhost:{ports["player"]}'
    return environment


def start_portals(processes, environment, origins, root=ROOT, player=False, ports=None):
    ports = port_settings('source' if player else 'containers', root=root) if ports is None else ports
    selected = ('admin', 'consumer') + (('player',) if player else ())
    # pnpm unions selectors and deduplicates overlapping workspace dependencies. Use tsc
    # directly so managed development never invokes generation or needs an API host.
    dependencies = ['pnpm', '--recursive']
    for name in selected:
        dependencies += ['--filter', PORTALS[name] + '^...']
    builder = Processes(processes.directory)
    log = builder.directory / 'build-web-dependencies.log'
    print(f'Building shared web dependencies (log: {log})...', flush=True)
    try:
        child = builder.start('build-web-dependencies', dependencies + ['exec', 'tsc'],
                              cwd=root / 'web', env=environment)
        if child.wait(timeout=300) != 0:
            raise HostFailure(f'Web dependency build failed; inspect {log} locally.', 'build-web-dependencies', log,
                              log_tail(log))
    except subprocess.TimeoutExpired as error:
        raise HostFailure(f'Web dependency build timed out; inspect {log} locally.', 'build-web-dependencies', log) from error
    finally:
        builder.stop()
    processes.start('web-dependencies', dependencies + ['--parallel', 'exec', 'tsc', '--watch', '--preserveWatchOutput'],
                    cwd=root / 'web', env=environment)
    for name in selected:
        port = ports['player' if name == 'player' else name + '_web']
        env = environment.copy()
        if name != 'player':
            env[f'VITE_ROMD_{name.upper()}_API_ORIGIN'] = origins[name]
        else:
            env['VITE_ROMD_PLAYER_ALLOWED_PARENTS'] = f'http://localhost:{ports["consumer_web"]}'
        processes.start(f'web-{name}', ['pnpm', '--filter', PORTALS[name], 'dev', '--port', str(port)],
                        cwd=root / 'web', env=env)
        wait_for(lambda port=port: http_ready(f'http://localhost:{port}/'), processes,
                 f'{name} development app', 180)


def source_environment(root, marker):
    text = call(['bash', str(root / 'scripts/db/env.sh')], cwd=root, capture=True)
    data_directory = source_data_directory(root)
    connection_keys = {'ConnectionStrings__Romd', 'ConnectionStrings__RomdProvisioning',
                       'ConnectionStrings__Hangfire', 'ConnectionStrings__HangfireProvisioning'}
    folded_keys = {key.lower().replace('__', ':') for key in connection_keys}
    folded_keys.add('romd:datadirectory')
    folded_keys.add('romd:readinessmarkerpath')
    folded_keys.update(key.lower().replace('__', ':')
                       for key in origin_environment(port_settings('source', root=root)))
    # .NET treats environment configuration keys case-insensitively. Remove
    # aliases before injecting this checkout's connection/data pair so an
    # inherited uppercase credential cannot win enumeration order.
    environment = {key: value for key, value in os.environ.items()
                   if key.lower().replace('__', ':') not in folded_keys}
    exported = set()
    for line in text.splitlines():
        words = shlex.split(line)
        if len(words) != 2 or words[0] != 'export':
            raise SetupError('Unexpected dev database exports; inspect scripts/db/env.sh.')
        key, value = words[1].split('=', 1)
        if key not in connection_keys:
            raise SetupError('Unexpected dev database export name.')
        environment[key] = value
        exported.add(key)
    if exported != connection_keys:
        raise SetupError('Incomplete checkout database exports; refusing inherited database credentials.')
    environment.update({'DOTNET_ENVIRONMENT': 'Development', 'ASPNETCORE_ENVIRONMENT': 'Development',
                        'Romd__DataDirectory': data_directory,
                        'Romd__ReadinessMarkerPath': str(marker)})
    environment.update(origin_environment(port_settings('source', root=root)))
    return environment


SOURCE_SUFFIXES = ('.cs', '.csproj', '.props', '.targets', '.json')
ASSET_SUFFIXES = ('.png', '.svg')


def source_snapshot(root):
    """Track build inputs, never output trees, database state, or application data."""
    paths = []
    assets = root / 'reference-data/assets'
    for base in (root / 'src', root / 'build', root / 'reference-data',
                 root / 'tools/Romd.ReferenceData.Generators'):
        for directory, names, files in os.walk(base):
            names[:] = [name for name in names if name not in ('bin', 'obj', 'wwwroot', 'openapi')
                        and not name.startswith('bin\\')]
            # Embedded presentation resources (Romd.Persistence.csproj) are build inputs too.
            suffixes = SOURCE_SUFFIXES + (ASSET_SUFFIXES if Path(directory).is_relative_to(assets) else ())
            paths.extend(Path(directory) / name for name in files if Path(name).suffix in suffixes)
    paths.extend(root / name for name in ('global.json', 'Directory.Packages.props', 'Directory.Build.props',
                                          'Directory.Build.targets', 'NuGet.Config', 'nuget.config', '.mise.toml'))
    result = {}
    for path in paths:
        try:
            result[str(path.relative_to(root))] = hashlib.sha256(path.read_bytes()).hexdigest()
        except FileNotFoundError:
            pass  # A save/delete race is observed by the next snapshot.
    return result


def build_source_hosts(root, directory, environment):
    for name in ('worker', 'admin', 'consumer'):
        builder = Processes(directory)
        log = directory / f'build-{name}.log'
        print(f'Building {name} (log: {log})...', flush=True)
        child = builder.start(f'build-{name}', ['dotnet', 'build', str(root / HOSTS[name])],
                              cwd=root, env=environment)
        try:
            if child.wait(timeout=300) != 0:
                raise HostFailure(f'{name} build failed; inspect {log} locally.', f'build-{name}', log,
                                  build_errors(log, root))
        except subprocess.TimeoutExpired as error:
            raise HostFailure(f'{name} build timed out; inspect {log} locally.', f'build-{name}', log) from error
        finally:
            builder.stop()


def start_source_hosts(root, directory, backend):
    marker = directory / 'worker-ready'
    environment = source_environment(root, marker)
    ports = port_settings('source', root=root)
    build_source_hosts(root, directory, environment)
    for name in ('worker', 'admin', 'consumer'):
        command = ['dotnet', 'run', '--no-build', '--no-launch-profile', '--project', str(root / HOSTS[name])]
        if name != 'worker':
            command += ['--urls', f'http://localhost:{ports[name + "_api"]}']
        host_environment = environment.copy()
        if name != 'worker':
            worker_only = {'connectionstrings:romdprovisioning',
                           'connectionstrings:hangfireprovisioning', 'romd:readinessmarkerpath'}
            host_environment = {key: value for key, value in host_environment.items()
                                if key.lower().replace('__', ':') not in worker_only}
        print(f'Starting {name}...', flush=True)
        backend.start(name, command, cwd=root, env=host_environment)
        if name == 'worker':
            print('Waiting for worker provisioning and seeders...', flush=True)
            wait_for(marker.exists, backend, 'worker provisioning and seeders', 240)
        else:
            port = ports[name + '_api']
            print(f'Waiting for {name} readiness...', flush=True)
            wait_for(lambda port=port: http_ready(f'http://localhost:{port}/health/ready'),
                     backend, f'{name} API readiness', 240)


def source_start(root, directory, processes, backend, runtime):
    source_data_directory(root)  # Resolve ambiguity before starting even the shared DB helper.
    call(['bash', str(root / 'scripts/db/up.sh')], cwd=root, env=runtime.child_environment())
    start_source_hosts(root, backend.directory, backend)
    ports = port_settings('source', root=root)
    print('Starting web development servers...', flush=True)
    start_portals(processes, os.environ.copy(), {name: f'http://localhost:{ports[name + "_api"]}'
                                               for name in ('admin', 'consumer')}, root, player=True, ports=ports)


def checkout_image(root, service):
    return f'localhost/romd-dev-{checkout_id(root)}-{service}:local'


def image_id(reference, root, runtime):
    try:
        return call(runtime.engine_command('image', 'inspect', '--format', '{{.Id}}', reference),
                    cwd=root, env=runtime.child_environment(), capture=True, timeout=60).strip() or None
    except SetupError:
        return None


def remove_replaced_images(root, previous, runtime):
    """Remove images this checkout's stable tags pointed to before a rebuild.

    Only an image that lost its last tag is removed, so an image someone else
    tagged (or a still-current one) is never deleted; in-use images are kept.
    """
    current = {image_id(checkout_image(root, service), root, runtime) for service in previous}
    for old in set(previous.values()) - current - {None}:
        try:
            tags = json.loads(call(runtime.engine_command('image', 'inspect', '--format', '{{json .RepoTags}}', old),
                                   cwd=root, env=runtime.child_environment(), capture=True, timeout=60) or 'null')
            if not tags:
                call(runtime.engine_command('rmi', old), cwd=root, env=runtime.child_environment(),
                     capture=True, timeout=120)
        except (SetupError, ValueError):
            pass  # Kept images are reported by dev:clean-images; never force removal.


def owned_images(root, runtime):
    """Return (legacy per-run tags, stable checkout tags, untagged labelled IDs) for this checkout only."""
    identity = re.escape(checkout_id(root))
    services = '|'.join(re.escape(name) for name in IMAGE_SERVICES)
    legacy_pattern = re.compile(rf'^localhost/romd-dev-{identity}-[0-9a-f]{{8}}-(?:{services}):local$')
    current_pattern = re.compile(rf'^localhost/romd-dev-{identity}-(?:{services}):local$')
    environment = runtime.child_environment()
    references = call(runtime.engine_command('images', '--format', '{{.Repository}}:{{.Tag}}'),
                      cwd=root, env=environment, capture=True, timeout=60).split()
    legacy = sorted({reference for reference in references if legacy_pattern.match(reference)})
    current = sorted({reference for reference in references if current_pattern.match(reference)})
    dangling = call(runtime.engine_command('images', '--quiet', '--no-trunc', '--filter', 'dangling=true',
                                           '--filter', f'label=com.romd.dev.checkout={checkout_id(root)}'),
                    cwd=root, env=environment, capture=True, timeout=60).split()
    return legacy, current, sorted(set(dangling))


def clean_images(root=ROOT, dry_run=False, include_current=False, runtime=None):
    """Remove this checkout's legacy/untagged development images; never prune globally."""
    if session_active(root):
        raise SetupError('A managed development run owns this checkout; stop it before cleaning images.')
    runtime = runtime or Runtime()
    legacy, current, dangling = owned_images(root, runtime)
    targets = legacy + (current if include_current else []) + dangling
    if not targets:
        print('No development images owned by this checkout need cleanup.')
        return True
    failed = False
    for reference in targets:
        if dry_run:
            print(f'would remove {reference}')
            continue
        try:
            # Without --force an image used by a container or another tag survives.
            call(runtime.engine_command('rmi', reference), cwd=root, env=runtime.child_environment(),
                 capture=True, timeout=120)
            print(f'removed {reference}')
        except SetupError:
            failed = True
            print(f'kept {reference}: in use or still referenced; inspect it with the selected engine')
    if current and not include_current:
        print(f'Kept {len(current)} current checkout image(s) for build caching; add --all to remove them.')
    return not failed


def build_container_images(root, directory, config_path, services, runtime):
    builder = Processes(directory)
    print(f'Building {", ".join(services)}; diagnostics: {directory}', flush=True)
    previous = {service: image_id(checkout_image(root, service), root, runtime) for service in services}
    try:
        command = runtime.compose_command('-f', str(config_path), 'build', *services)
        child = builder.start('build-' + services[0], command, cwd=root, env=runtime.child_environment())
        if child.wait(timeout=3600) != 0:
            log = directory / f'build-{services[0]}.log'
            raise HostFailure(f'Container image build failed; inspect {log} locally.', 'build-' + services[0],
                              log, build_errors(log, root))
    except subprocess.TimeoutExpired as error:
        raise SetupError('Container image build timed out; inspect this run\'s local build log.') from error
    finally:
        builder.stop()
    remove_replaced_images(root, previous, runtime)


def docker_start(root, directory, processes, project, runtime):
    environment = runtime.child_environment()
    existing = call(runtime.engine_command('ps', '--quiet', '--filter',
                     f'label=com.romd.dev.checkout={checkout_id(root)}'), cwd=root, env=environment, capture=True)
    if existing.strip():
        raise SetupError('Containers from an earlier managed run still own this checkout; inspect the selected engine before restarting.')
    config_path = directory / 'compose.json'
    config = docker_configuration(root, project, runtime)
    descriptor = os.open(config_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, 'w') as handle:
        json.dump(config, handle)
    ensure_checkout_volumes(root, config, runtime)
    command = runtime.compose_command('-p', project, '-f', str(config_path))
    try:
        build_container_images(root, directory, config_path, ['romd-worker'], runtime)
        call(command + ['up', '--no-build', '-d', '--wait', '--wait-timeout', '240',
                        'postgres', 'romd-worker'], cwd=root, env=environment, timeout=3600)
        build_container_images(root, directory, config_path, ['romd-admin', 'romd-consumer', 'romd-player'], runtime)
        call(command + ['up', '--no-build', '-d', '--wait', '--wait-timeout', '240',
                        'romd-admin', 'romd-consumer', 'romd-player'], cwd=root, env=environment, timeout=3600)
        ports = port_settings('containers', root=root)
        start_portals(processes, os.environ.copy(), {name: f'http://localhost:{ports[name + "_api"]}'
                                                    for name in ('admin', 'consumer')}, root, ports=ports)
        return command
    except BaseException:
        try:
            with uninterrupted_cleanup():
                call(command + ['down', '--timeout', '30'], cwd=root, env=environment, timeout=120, capture=True)
        except SetupError as error:
            print(f'Cleanup also failed: {error}', file=sys.stderr)
        raise


def restart_requested(root):
    """Consume a `dev:restart` request; content-identical touches do not change the snapshot."""
    try:
        (root / RESTART_TRIGGER).unlink()
        return True
    except FileNotFoundError:
        return False


def request_restart(root=ROOT):
    record = session_status(root)
    if not record['active'] or record.get('mode') != 'source':
        raise SetupError('No active managed source run owns this checkout; start it with `mise run dev`.')
    (root / RESTART_TRIGGER).touch()
    print(f'Requested a worker-first backend restart (current state: {record["state"]}).')


def development(mode, root=ROOT):
    check_tools(root)
    # Fail before starting PostgreSQL or building when dependencies are missing.
    check_web_dependencies(root)
    runtime = check_containers(root)
    ports = port_settings(mode, root=root)
    with session_lock(root):
        check_ports(mode, root)
        directory = root / '.data/dev/runs' / uuid.uuid4().hex
        directory.mkdir(parents=True, mode=0o700)
        restart_requested(root)  # Discard a stale request from an earlier run.
        processes = Processes(directory)
        backend = None
        revision = 0
        inputs = None
        project = f'romd-dev-{checkout_id(root)}-{directory.name[:8]}'
        compose = None
        record = {'schemaVersion': STATUS_SCHEMA_VERSION, 'mode': mode, 'state': 'starting', 'pid': os.getpid(),
                  'checkout': str(root.resolve()), 'runDirectory': str(directory),
                  'urls': {name: f'http://localhost:{port}' for name, port in ports.items()}}
        def report(state, failure=None):
            record['state'] = state
            record['updatedAt'] = time.time()
            record['backendDirectory'] = str(backend.directory) if backend else record.get('backendDirectory')
            record['pids'] = {name: child.pid for owned in (processes, backend) if owned
                              for name, child in owned.children if child.poll() is None}
            for key in ('error', 'failedComponent', 'log'):
                record.pop(key, None)
            record.update(failure or {})
            write_session(root, record)
        def interrupted(_signum, _frame):
            raise KeyboardInterrupt
        previous = {number: signal.signal(number, interrupted)
                    for number in SHUTDOWN_SIGNALS}
        recovery = 'Waiting for a backend source edit or `mise run dev:restart`.'
        try:
            report('starting')
            if mode == 'source':
                inputs = source_snapshot(root)
                backend = Processes(directory / f'backend-{revision:04d}')
                report('starting')
                source_start(root, directory, processes, backend, runtime)
            else:
                compose = docker_start(root, directory, processes, project, runtime)
            report('ready')
            print(f'{mode} development ready. Admin http://localhost:{ports["admin_web"]}; '
                  f'consumer http://localhost:{ports["consumer_web"]}; player http://localhost:{ports["player"]}', flush=True)
            print(f'Local process diagnostics: {directory}. Stop with Ctrl-C.', flush=True)
            while True:
                processes.assert_running()
                if mode == 'source':
                    current = source_snapshot(root)
                    restart = restart_requested(root)
                    changed = current != inputs
                    if changed or restart:
                        # Capture before building: edits during a build trigger the next iteration.
                        inputs = current
                        report('reloading')
                        if backend:
                            backend.stop()
                        revision += 1
                        backend = Processes(directory / f'backend-{revision:04d}')
                        report('reloading')
                        reason = 'Backend inputs changed' if changed else 'Backend restart requested'
                        print(f'{reason}; rebuilding and restarting worker first.', flush=True)
                        try:
                            start_source_hosts(root, backend.directory, backend)
                            report('ready')
                            print('Backend reload ready.', flush=True)
                        except SetupError as error:
                            backend.stop()
                            backend = None
                            report('reload-failed', failure_fields(error))
                            print_failure('Backend reload failed', error, recovery)
                    elif backend:
                        exited = backend.exited()
                        if exited:
                            # A crashed host is recoverable: stop its siblings, keep Vite, restart worker-first later.
                            name, child = exited
                            log = backend.directory / f'{name}.log'
                            error = ProcessExited(f'{name} exited {child.returncode} after readiness; log: {log}',
                                                  name, log, log_tail(log))
                            backend.stop()
                            backend = None
                            report('backend-exited', failure_fields(error))
                            print_failure('Backend host stopped', error,
                                          'Stopped the remaining backend hosts; web servers keep running. ' + recovery)
                if compose:
                    running = call(compose + ['ps', '--status', 'running', '--quiet'], cwd=root, env=runtime.child_environment(), capture=True)
                    if len(running.splitlines()) != 5:
                        raise SetupError('A development container stopped; inspect this run\'s container logs.')
                    for port in (ports['admin_api'], ports['consumer_api']):
                        if not http_ready(f'http://localhost:{port}/health/ready'):
                            raise SetupError('A development API lost readiness; inspect this run\'s container logs.')
                time.sleep(2)
        finally:
            active_error = sys.exc_info()[1]
            failure = None if active_error is None or isinstance(active_error, KeyboardInterrupt) \
                else failure_fields(active_error)
            if failure and getattr(active_error, 'details', None):
                print(f'Diagnostics from {active_error.component} ({active_error.log}):', flush=True)
                for line in active_error.details:
                    print(f'  {line}', flush=True)
            cleanup_errors = []
            for number in previous:
                signal.signal(number, signal.SIG_IGN)
            try:
                report('stopping', failure)
            except OSError:
                cleanup_errors.append('session status write failed')
            for owned in (processes, backend):
                if owned:
                    try:
                        owned.stop()
                    except (OSError, SetupError, subprocess.TimeoutExpired) as error:
                        cleanup_errors.append(type(error).__name__)
            if compose:
                try:
                    call(compose + ['down', '--timeout', '30'], cwd=root, env=runtime.child_environment(), timeout=120, capture=True)
                except SetupError:
                    cleanup_errors.append('Container cleanup failed')
            for number, handler in previous.items():
                signal.signal(number, handler)
            if cleanup_errors and not failure:
                failure = {'error': 'Owned resources did not all clean up: ' + ', '.join(cleanup_errors)}
            try:
                report('stopped' if not cleanup_errors and failure is None else 'failed', failure)
            except OSError:
                cleanup_errors.append('session status write failed')
            if cleanup_errors:
                error = SetupError('Owned resources did not all clean up: ' + ', '.join(cleanup_errors))
                if active_error:
                    print(f'Cleanup also failed: {error}', file=sys.stderr)
                else:
                    raise error


def main():
    # Keep controller progress, errors and child diagnostics ordered when piped.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(line_buffering=True)
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    for name in ('bootstrap', 'doctor'):
        command = commands.add_parser(name)
        command.add_argument('--console', action='store_true')
        if name == 'doctor':
            command.add_argument('--mode', choices=('containers', 'docker', 'source'), default='source')
            command.add_argument('--tests', action='store_true', help='Also check host prerequisites for backend tests')
    for name in ('containers', 'docker', 'source', 'status'):
        commands.add_parser(name)
    commands.add_parser('restart', help='Ask the active source run to rebuild and restart backend hosts worker-first')
    clean = commands.add_parser('clean-images', help="Remove this checkout's legacy and untagged development images")
    clean.add_argument('--dry-run', action='store_true', help='List the images without removing them')
    clean.add_argument('--all', action='store_true', help="Also remove this checkout's current cached images")
    args = parser.parse_args()
    try:
        if args.command == 'bootstrap':
            bootstrap(console=args.console)
        elif args.command == 'doctor':
            return 0 if doctor(mode=args.mode, console=args.console, test_tools=args.tests) else 1
        elif args.command == 'status':
            print(json.dumps(session_status(), indent=2))
        elif args.command == 'restart':
            request_restart()
        elif args.command == 'clean-images':
            return 0 if clean_images(dry_run=args.dry_run, include_current=args.all) else 1
        else:
            development(args.command)
    except KeyboardInterrupt:
        return 130
    except SetupError as error:
        print(f'error: {error}', file=sys.stderr)
        return 1
    return 0

if __name__ == '__main__':
    sys.exit(main())
