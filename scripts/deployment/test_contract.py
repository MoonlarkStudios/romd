#!/usr/bin/env python3
"""Exercise the merged Compose configuration, including regressions in network topology.

Compose rendering goes through the engine-neutral adapter, so it runs with Docker or
rootless Podman plus the pinned Compose client. Only the build-context export needs
Docker BuildKit; it skips with a reason when Docker is unavailable, but never on GitHub Actions.
"""
import copy
import contextlib
import hashlib
import json
import io
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import threading
import time
import unittest
import tempfile
import tarfile
import xml.etree.ElementTree as ET
from unittest.mock import patch, Mock
from urllib.parse import parse_qs, urlsplit

from validate import validate

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
from containers.runtime import ContainerError, Runtime
sys.path.insert(0, str(ROOT / 'scripts/agents'))
from doc_links import check_document

_COMPOSE = None


def compose(*arguments):
    """The pinned Compose client (or Docker's plugin), resolved from the caller's environment.

    test_environment() deliberately strips ROMD_* selection, so resolve before applying it.
    `config` needs no engine connection, so the engine endpoint is not probed.
    """
    global _COMPOSE
    if _COMPOSE is None:
        _COMPOSE = Runtime().compose_client()
    return [*_COMPOSE, *arguments]


def docker_build_unavailable():
    """Why Docker's BuildKit context export cannot run here, or None when it can."""
    if os.environ.get('GITHUB_ACTIONS') == 'true':
        return None  # Hosted CI must exercise the real export; a missing builder fails the test.
    engine = os.environ.get('ROMD_CONTAINER_ENGINE', '')
    if engine and engine != 'docker':
        return (f'ROMD_CONTAINER_ENGINE={engine}: .dockerignore matching is qualified only with Docker BuildKit '
                '(docker buildx build --output type=local); run under Docker or hosted CI for this coverage')
    if not shutil.which('docker'):
        return 'docker CLI not found: the .dockerignore context export needs Docker BuildKit (docker buildx)'
    try:
        probe = subprocess.run(['docker', 'buildx', 'version'], capture_output=True, timeout=30)
    except (OSError, subprocess.TimeoutExpired):
        probe = None
    if probe is None or probe.returncode:
        return 'docker buildx is unavailable: the .dockerignore context export needs Docker BuildKit'
    return None


def portable_environment(directory, commands):
    """Model a host with Python and the required utilities, without Perl shasum."""
    tools = Path(directory) / 'bin'
    tools.mkdir()
    for command in commands:
        (tools / command).symlink_to(sys.executable if command == 'python3' else shutil.which(command))
    return dict(os.environ, PATH=str(tools))


def test_environment():
    base_images = json.loads((ROOT / 'scripts/deployment/base-images.json').read_text())
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(('ROMD_', 'COMPOSE_', 'IGDB_', 'CLOUDFLARE_'))}
    for index, role in enumerate(('BOOTSTRAP', 'PROVISIONER', 'WORKER', 'ADMIN', 'CONSUMER'), 1):
        env[f'ROMD_POSTGRES_{role}_PASSWORD'] = str(index) * 64
    for name in ('ADMIN', 'CONSUMER', 'WORKER', 'PLAYER'):
        env[f'ROMD_{name}_IMAGE'] = f'romd-{name.lower()}:test'
    env.update(ROMD_POSTGRES_IMAGE=base_images['postgres'], ROMD_JWT_SECRET='a' * 64,
               ROMD_DEFAULT_ADMIN_PASSWORD='TestAdmin123!', ROMD_CONSUMER_DELIVERY_SIGNING_SECRET='b' * 64,
               ROMD_ADMIN_PUBLIC_URL='https://admin.example.test', ROMD_CONSUMER_PUBLIC_URL='https://console.example.test',
               ROMD_PLAYER_PUBLIC_URL='https://play.example.test', ROMD_TRUSTED_PROXY='172.28.0.2',
               ROMD_ADMIN_HOST='admin.example.test', ROMD_CONSUMER_HOST='console.example.test', ROMD_PLAYER_HOST='play.example.test',
               ROMD_NGINX_IMAGE=base_images['ingress'], ROMD_CLOUDFLARED_IMAGE='cloudflare/cloudflared:test',
               CLOUDFLARE_TUNNEL_TOKEN='test-only', ROMD_TLS_DIRECTORY='/tmp/romd-test-certs')
    return env


def render(*overlays, environment=None):
    args = compose('--env-file', '/dev/null', '-f', str(ROOT / 'compose.yaml'))
    for overlay in overlays:
        args += ['-f', str(ROOT / overlay)]
    args += ['config', '--format', 'json']
    return json.loads(subprocess.check_output(args, env=environment or test_environment()))


def dockerfile_stages():
    stages = {}
    for block in (ROOT / 'Dockerfile').read_text().split('\nFROM ')[1:]:
        header, _, body = block.partition('\n')
        stages[header.rsplit(' AS ', 1)[1]] = body
    return stages


def dockerfile_chain(target, arch):
    """The stages a target builds on for TARGETARCH=arch, ending with its external base image."""
    bases = {}
    for block in (ROOT / 'Dockerfile').read_text().split('\nFROM ')[1:]:
        header = block.partition('\n')[0].split()
        bases[header[-1]] = header[-3].replace('${TARGETARCH}', arch)
    chain = [target]
    while chain[-1] in bases:
        chain.append(bases[chain[-1]])
    return chain


def native_environment(chain):
    """The Romd__Native__ ENV values the stages of a chain leave in the image."""
    stages = dockerfile_stages()
    environment = {}
    for name in reversed(chain[:-1]):
        for line in stages[name].splitlines():
            if line.startswith('ENV ') and 'Romd__Native__' in line:
                environment.update(pair.split('=', 1) for pair in shlex.split(line[4:])
                                   if pair.startswith('Romd__Native__'))
    return environment


LIBCHDR_ENVIRONMENT = {'Romd__Native__LibchdrDirectory': '/app/native/libchdr'}
CHDMAN_ENVIRONMENT = {'Romd__Native__ChdmanDirectory': '/app/native/chdman'}
LIBCHDR_COPIES = [
    'COPY --from=libchdr-artifacts /build-manifest.json /app/native/libchdr/build-manifest.json',
    'COPY --from=libchdr-artifacts /native/libmoonlark_chdr.so /app/native/libchdr/native/libmoonlark_chdr.so',
]
CHDMAN_COPIES = [
    'COPY --from=chdman-artifacts /build-manifest.json /app/native/chdman/build-manifest.json',
    'COPY --from=chdman-artifacts /source-inputs.json /app/native/chdman/source-inputs.json',
    'COPY --from=chdman-artifacts /native/chdman /app/native/chdman/native/chdman',
]


class PipeEventProcess:
    def __init__(self):
        read_fd, self.write_fd = os.pipe()
        self.stdout = os.fdopen(read_fd, 'rb')
        self.returncode = None

    def emit(self, event):
        os.write(self.write_fd, json.dumps(event).encode() + b'\n')

    def poll(self):
        return self.returncode

    def terminate(self):
        if self.returncode is None:
            self.returncode = 0
            os.close(self.write_fd)

    kill = terminate

    def wait(self, timeout=None):
        return self.returncode


class FakeLifecycleDocker:
    """Controlled pipes exercise the real collector and barriers, not a stub gate."""
    def __init__(self):
        self.streams, self.containers, self.calls = {}, {}, []
        self.sequence = 0
        self.bad_probe = False
        self.delay_subscription = False
        self.pending_subscriptions, self.history = {}, []

    def popen(self, command, **kwargs):
        project = command[-1].split('=', 2)[-1]
        process = PipeEventProcess()
        self.streams[project] = process
        if self.delay_subscription:
            self.pending_subscriptions[project] = '--since' in command
        return process

    def event(self, project, identifier, action, attributes=None):
        labels = self.containers[identifier]['labels']
        event = {'Type': 'container', 'Action': action, 'timeNano': time.time_ns(), 'Actor': {'ID': identifier,
                 'Attributes': dict(labels, **(attributes or {}))}}
        self.history.append((project, event))
        self.history = self.history[-256:]
        if project in self.pending_subscriptions:
            # The first create reaches Docker before its events request subscribes.
            # Only an explicit history window can replay that already emitted event.
            if self.pending_subscriptions.pop(project):
                for recorded_project, recorded in self.history:
                    if recorded_project == project:
                        self.streams[project].emit(recorded)
            return
        self.streams[project].emit(event)

    def create(self, project, service=None, session=None, name=None):
        from container_lifecycle import PROJECT_LABEL, SESSION_LABEL
        self.sequence += 1
        identifier = f'{self.sequence:064x}'
        labels = {PROJECT_LABEL: project}
        labels.update({'com.docker.compose.service': service} if service else {SESSION_LABEL: session})
        self.containers[identifier] = {'labels': labels, 'running': False, 'name': name}
        self.event(project, identifier, 'create')
        return identifier

    def start(self, project, identifier):
        self.containers[identifier]['running'] = True
        self.event(project, identifier, 'start')

    def populate(self, project):
        from container_lifecycle import SERVICES
        for service in sorted(SERVICES):
            self.start(project, self.create(project, service))

    def stop(self, project, code=0):
        from container_lifecycle import PROJECT_LABEL
        for identifier, value in list(self.containers.items()):
            if value['labels'][PROJECT_LABEL] == project and value['running']:
                self.event(project, identifier, 'kill', {'signal': '15'})
                self.event(project, identifier, 'die', {'exitCode': str(code)})
                value['running'] = False
                self.event(project, identifier, 'stop')

    def down(self, project):
        from container_lifecycle import PROJECT_LABEL
        for identifier, value in list(self.containers.items()):
            if value['labels'][PROJECT_LABEL] == project:
                self.event(project, identifier, 'destroy')
                del self.containers[identifier]

    def command(self, command, env, **kwargs):
        from container_lifecycle import PROJECT_LABEL, SESSION_LABEL
        args = list(command[1:])
        self.calls.append(args)
        if args[:2] == ['image', 'inspect']:
            result = 'null'
        elif args[:2] in (['network', 'ls'], ['volume', 'ls']):
            result = ''
        elif args[0] == 'ps':
            filters = [args[index + 1] for index, value in enumerate(args) if value == '--filter']
            result = '\n'.join(identifier for identifier, value in self.containers.items()
                if all((value['labels'].get(item[6:].split('=', 1)[0]) == item[6:].split('=', 1)[1]
                        if item.startswith('label=') else value['name'] == item.removeprefix('name=^/').removesuffix('$'))
                       for item in filters))
        elif args[0] == 'create':
            labels = dict(args[index + 1].split('=', 1) for index, value in enumerate(args) if value == '--label')
            result = self.create(labels[PROJECT_LABEL], session=labels[SESSION_LABEL], name=args[args.index('--name') + 1])
            if self.bad_probe:
                return 1, b'private daemon failure', None
        elif args[0] == 'rm':
            identifier = args[-1]
            project = self.containers[identifier]['labels'][PROJECT_LABEL]
            self.event(project, identifier, 'destroy')
            del self.containers[identifier]
            result = identifier
        elif args[0] == 'inspect':
            value = self.containers[args[-1]]
            result = json.dumps(value['labels'] if '.Config.Labels' in args[2] else {'Running': value['running']})
        elif args[0] == 'logs':
            result = 'PRIVATE-LOG-SENTINEL'
        else:
            raise AssertionError(args)
        return 0, result.encode(), None


class ContainerLifecycleTests(unittest.TestCase):
    def test_shared_recovery_keeps_source_identity_and_worker_first_restore(self):
        from recovery import RecoverySequence
        source = {'COMPOSE_PROJECT_NAME': 'source'}
        commands = []
        gate = Mock()
        gate.stopping.side_effect = lambda *args: contextlib.nullcontext()
        recovery = RecoverySequence(lambda argv, stage, env: commands.append((list(argv), dict(env))),
                                    Path('/fixture/compose.sh'), gate, source)
        # Callers reuse their environment while switching to the restored project.
        source['COMPOSE_PROJECT_NAME'] = 'restored'
        recovery.restart()
        self.assertTrue(all(env['COMPOSE_PROJECT_NAME'] == 'source' for _, env in commands))
        self.assertEqual(['postgres', 'romd-worker'], commands[1][0][-2:])
        commands.clear()
        recovery.restore(Path('/fixture/restore.sh'), Path('/fixture/backup'), source)
        self.assertTrue(all(env['COMPOSE_PROJECT_NAME'] == 'restored' for _, env in commands))
        self.assertEqual(['postgres', 'romd-worker'], commands[-2][0][-2:])
        gate.checkpoint.assert_has_calls([unittest.mock.call('source'), unittest.mock.call('restored')])

    def setUp(self):
        from container_lifecycle import ContainerLifecycle
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.projects = ('romd-smoke-1234567890', 'romd-smoke-1234567890-restore')
        self.gate = ContainerLifecycle({}, self.projects, 'nginx@sha256:' + 'f' * 64,
            self.directory / 'public/lifecycle.json', private_root=self.directory / 'private', timeout=.5)
        self.engine = FakeLifecycleDocker()

    def environment(self):
        from contextlib import ExitStack
        stack = ExitStack()
        stack.enter_context(patch('container_lifecycle.bounded_command', side_effect=self.engine.command))
        stack.enter_context(patch('container_lifecycle.subprocess.Popen', side_effect=self.engine.popen))
        return stack

    def cleanups(self):
        return [(project, lambda selected=project: self.engine.stop(selected),
                 lambda selected=project: self.engine.down(selected)) for project in self.projects]

    def test_delayed_event_subscription_replays_initial_probe_before_workload(self):
        self.engine.delay_subscription = True
        with self.environment(), self.gate.monitor(self.cleanups()):
            for project in self.projects:
                self.engine.populate(project)
                self.gate.checkpoint(project)
        self.assertEqual('passed', self.gate.receipt['result'])
        self.assertTrue(all(ledger.ready and ledger.drained for ledger in self.gate.ledgers.values()))
        self.assertEqual({}, self.engine.containers)

    def test_missing_startup_replay_still_fails_before_any_workload(self):
        from container_lifecycle import LifecycleError
        self.engine.delay_subscription = True
        popen = self.engine.popen
        def without_history(command, **kwargs):
            command = list(command)
            if '--since' in command:
                index = command.index('--since')
                del command[index:index + 2]
            return popen(command, **kwargs)
        with self.environment(), patch('container_lifecycle.subprocess.Popen', side_effect=without_history):
            with self.assertRaisesRegex(LifecycleError, 'collector-probe-timeout'), self.gate.monitor(self.cleanups()):
                self.fail('an unacknowledged initial probe cannot admit the workload')
        self.assertEqual('failed', self.gate.receipt['result'])
        self.assertFalse(self.gate.receipt['complete'])
        self.assertEqual({}, self.engine.containers)

    def test_continuous_pipes_cover_both_projects_manual_restart_backup_and_final_cleanup(self):
        from container_lifecycle import HOSTS
        with self.environment(), self.gate.monitor(self.cleanups()):
            for project in self.projects:
                self.engine.populate(project)
                self.gate.checkpoint(project)
                # More health/exec noise than Docker's history buffer must not erase a death.
                identifier = next(key for key, value in self.engine.containers.items() if value['running'])
                for _ in range(300):
                    self.engine.event(project, identifier, 'exec_start')
                with self.gate.stopping(project, HOSTS):
                    for identifier, value in list(self.engine.containers.items()):
                        if value['labels'].get('com.docker.compose.project') == project and value['labels'].get('com.docker.compose.service') in HOSTS:
                            self.engine.event(project, identifier, 'die', {'exitCode': '0'})
                            value['running'] = False
                            self.engine.event(project, identifier, 'stop')
                            self.engine.start(project, identifier)
                self.gate.checkpoint(project)
        receipt = json.loads(self.gate.evidence.read_text())
        self.assertEqual('passed', receipt['result'])
        self.assertTrue(receipt['complete'])
        self.assertEqual({}, self.engine.containers)
        self.assertNotIn('PRIVATE-LOG-SENTINEL', self.gate.evidence.read_text())
        self.assertEqual(0o600, self.gate.evidence.stat().st_mode & 0o777)
        self.assertTrue(any('PRIVATE-LOG-SENTINEL' in file.read_text() for file in self.gate.private.glob('*.logs')))
        self.assertTrue(all(file.stat().st_mode & 0o777 == 0o600 for file in self.gate.private.iterdir()))
        streams = [call for call in self.engine.calls if call[0] == 'create']
        self.assertTrue(streams)
        self.assertTrue(all('--entrypoint' in call and '/bin/true' in call for call in streams))

    def test_nonzero_exit_is_not_hidden_by_later_clean_manual_restart_or_compose_success(self):
        from container_lifecycle import LifecycleError
        with self.environment():
            with self.assertRaises(LifecycleError), self.gate.monitor(self.cleanups()):
                for project in self.projects:
                    self.engine.populate(project)
                    self.gate.checkpoint(project)
                project = self.projects[0]
                with self.gate.stopping(project):
                    self.engine.stop(project, 139)
                    for identifier, value in list(self.engine.containers.items()):
                        if value['labels'].get('com.docker.compose.project') == project and not value['running']:
                            self.engine.start(project, identifier)
        self.assertIn('nonzero-container-exit', self.gate.receipt['projects']['source']['errors'])
        self.assertEqual({}, self.engine.containers)

    def test_source_backup_waits_and_checkpoints_restarted_hosts_before_final_stop(self):
        import smoke
        from container_lifecycle import HOSTS, PROJECT_LABEL
        source_project = self.projects[0]
        compose = ROOT / 'scripts/deployment/compose.sh'
        backup_root = self.directory / 'backups'
        backup = backup_root / 'captured-set'
        phases = []
        state = {'starting': False, 'checkpointed': False}
        checkpoint = self.gate.checkpoint

        def command(arguments, env, capture=False):
            self.assertEqual(source_project, env['COMPOSE_PROJECT_NAME'])
            if arguments[0] == ROOT / 'scripts/backup/backup.sh':
                self.assertEqual([ROOT / 'scripts/backup/backup.sh', backup_root], arguments)
                phases.append('backup')
                backup.mkdir(parents=True)
                (backup / 'manifest.json').write_text('{}')
                state['starting'] = True
                for identifier, value in list(self.engine.containers.items()):
                    if value['labels'].get(PROJECT_LABEL) == source_project and value['labels'].get('com.docker.compose.service') in HOSTS:
                        self.engine.event(source_project, identifier, 'die', {'exitCode': '0'})
                        value['running'] = False
                        self.engine.event(source_project, identifier, 'stop')
                        self.engine.start(source_project, identifier)
            elif arguments[1] == 'up':
                self.assertEqual([compose, 'up', '-d', '--wait', '--wait-timeout', '240'], arguments)
                phases.append('wait')
                state['starting'] = False
            elif arguments == [compose, 'stop']:
                phases.append('stop')
                # A stop during startup reproduces the observed nonzero exit. The real
                # lifecycle collector must reject it if either required barrier is missing.
                code = 143 if state['starting'] or not state['checkpointed'] else 0
                self.engine.stop(source_project, code)
            else:
                self.fail(arguments)

        def checked(project):
            self.assertEqual(source_project, project)
            phases.append('checkpoint')
            self.assertFalse(state['starting'])
            checkpoint(project)
            state['checkpointed'] = True

        with self.environment(), self.gate.monitor(self.cleanups()):
            for project in self.projects:
                self.engine.populate(project)
                self.gate.checkpoint(project)
            with patch.object(smoke, 'run', side_effect=command), patch.object(self.gate, 'checkpoint', side_effect=checked):
                self.assertEqual(backup, smoke.backup_and_stop_source(
                    compose, {'COMPOSE_PROJECT_NAME': source_project}, self.gate, backup_root))
        self.assertEqual(['backup', 'wait', 'checkpoint', 'stop'], phases)
        self.assertEqual('passed', self.gate.receipt['result'])
        self.assertTrue(self.gate.receipt['complete'])
        self.assertEqual({}, self.engine.containers)

    def test_source_backup_readiness_or_checkpoint_failure_prevents_final_stop(self):
        import smoke
        from container_lifecycle import HOSTS, LifecycleError
        compose = ROOT / 'scripts/deployment/compose.sh'
        for failure in ('readiness', 'checkpoint'):
            with self.subTest(failure=failure):
                backup_root = self.directory / 'backups' / failure
                gate = Mock()
                gate.stopping.return_value = contextlib.nullcontext()
                if failure == 'checkpoint':
                    gate.checkpoint.side_effect = LifecycleError('failed lifecycle checkpoint')
                calls = []

                def command(arguments, env, capture=False):
                    calls.append(arguments)
                    if arguments[0] == ROOT / 'scripts/backup/backup.sh':
                        (backup_root / 'captured-set').mkdir(parents=True)
                        (backup_root / 'captured-set/manifest.json').write_text('{}')
                    elif arguments[1] == 'up':
                        if failure == 'readiness':
                            raise subprocess.CalledProcessError(1, arguments)
                    else:
                        self.fail('source stop must not run after a failed readiness barrier')

                expected = subprocess.CalledProcessError if failure == 'readiness' else LifecycleError
                with patch.object(smoke, 'run', side_effect=command), self.assertRaises(expected):
                    smoke.backup_and_stop_source(compose, {'COMPOSE_PROJECT_NAME': self.projects[0]}, gate, backup_root)
                if failure == 'readiness':
                    gate.checkpoint.assert_not_called()
                else:
                    gate.checkpoint.assert_called_once_with(self.projects[0])
                gate.stopping.assert_called_once_with(self.projects[0], HOSTS)
                self.assertEqual([
                    [ROOT / 'scripts/backup/backup.sh', backup_root],
                    [compose, 'up', '-d', '--wait', '--wait-timeout', '240'],
                ], calls)

    def test_partial_create_error_still_removes_exact_probe_and_writes_failed_receipt(self):
        from container_lifecycle import LifecycleError
        self.engine.bad_probe = True
        with self.environment(), self.assertRaises(LifecycleError):
            with self.gate.monitor(self.cleanups()):
                self.fail('failed collector startup reached workload')
        self.assertEqual({}, self.engine.containers)
        self.assertFalse(self.gate.receipt['complete'])
        self.assertTrue(any(call[:2] == ['rm', '--volumes'] for call in self.engine.calls))

    def test_preexisting_namespace_is_never_stopped_logged_or_removed(self):
        from container_lifecycle import LifecycleError, PROJECT_LABEL
        self.engine.containers['a' * 64] = {'labels': {PROJECT_LABEL: self.projects[1]}, 'name': 'foreign', 'running': False}
        with self.environment(), self.assertRaisesRegex(LifecycleError, 'already-exists'):
            with self.gate.monitor(self.cleanups()):
                self.fail('colliding project reached workload')
        self.assertFalse(self.gate.owned)
        self.assertEqual(['a' * 64], list(self.engine.containers))
        self.assertFalse(any(call[0] in ('logs', 'rm', 'create') for call in self.engine.calls))

    def test_primary_failure_survives_both_cleanup_failures_and_collector_errors(self):
        def failed(which):
            raise RuntimeError(which)
        cleanups = [(project, lambda: failed('stop'), lambda: failed('down')) for project in self.projects]
        with self.environment(), self.assertRaisesRegex(ValueError, 'original functional failure'):
            with self.gate.monitor(cleanups):
                for project in self.projects:
                    self.engine.populate(project)
                raise ValueError('original functional failure')
        self.assertEqual(4, sum('stop-failed' in item or 'down-failed' in item for item in self.gate.cleanup_errors))
        self.assertTrue(all(project['errors'] for project in self.gate.receipt['projects'].values()))

    def test_create_only_probes_cannot_substitute_for_service_coverage(self):
        from container_lifecycle import LifecycleError
        with self.environment(), self.assertRaises(LifecycleError):
            with self.gate.monitor(self.cleanups()):
                pass
        self.assertIn('required-service-coverage-missing', self.gate.receipt['projects']['source']['errors'])

    def ledger(self):
        from container_lifecycle import Ledger
        return Ledger(self.projects[0], 'source', 'session')

    def accept(self, ledger, action, **attributes):
        with ledger.condition:
            ledger.accept({'Type': 'container', 'Action': action, 'timeNano': 1, 'Actor': {'ID': 'a' * 64,
                'Attributes': {'com.docker.compose.project': ledger.project,
                               'com.docker.compose.service': 'romd-admin', **attributes}}})

    def test_zero_exit_automatic_restart_and_unrequested_clean_exit_fail(self):
        for action in ('restart', 'exit'):
            ledger = self.ledger()
            self.accept(ledger, 'create')
            self.accept(ledger, 'start')
            if action == 'restart':
                ledger.allowed_stops.add('romd-admin')
            self.accept(ledger, 'die', exitCode='0')
            if action == 'restart':
                self.accept(ledger, 'start')
            self.assertIn('unexpected-restart' if action == 'restart' else 'unrequested-container-exit', ledger.errors)

    def test_oom_sigkill_missing_exit_code_and_incomplete_order_fail_closed(self):
        for action, attributes, expected in (
                ('oom', {}, 'container-oom'), ('kill', {'signal': '9'}, 'container-sigkill'),
                ('die', {}, 'missing-exit-code'), ('destroy', {}, 'destroy-with-incomplete-lifecycle')):
            with self.subTest(action=action):
                ledger = self.ledger()
                self.accept(ledger, 'create')
                self.accept(ledger, 'start')
                self.accept(ledger, action, **attributes)
                self.assertIn(expected, ledger.errors)
        ledger = self.ledger()
        self.accept(ledger, 'start')
        self.assertIn('missing-create-or-invalid-transition', ledger.errors)

    def test_reader_rejects_eof_malformed_truncated_and_overlong_events(self):
        for data, expected in ((b'', 'collector-premature-eof'), (b'not json\n', 'collector-malformed-event'),
                (b'{"Type":"container"}', 'collector-truncated-event'),
                (b'x' * (128 * 1024 + 1), 'collector-truncated-event')):
            with self.subTest(expected=expected):
                ledger = self.ledger()
                ledger.process = Mock(stdout=io.BytesIO(data))
                ledger.read()
                self.assertIn(expected, ledger.errors)

    def test_deep_json_and_decoder_recursion_failure_are_recorded_and_end_reader(self):
        ledger = self.ledger()
        ledger.process = Mock(stdout=io.BytesIO(b'[' * 2000 + b'0' + b']' * 2000 + b'\n'))
        ledger.read()
        # Python versions differ in whether their JSON decoder uses a recursion
        # limit. Either outcome must leave fixed failure evidence and end safely.
        self.assertTrue(any(code in ledger.errors for code in ('invalid-event-schema', 'collector-malformed-event')))
        self.assertTrue(ledger.reader_done)
        ledger = self.ledger()
        ledger.process = Mock(stdout=io.BytesIO(b'{}\n'))
        with patch('container_lifecycle.json.loads', side_effect=RecursionError):
            ledger.read()
        self.assertIn('collector-malformed-event', ledger.errors)
        self.assertTrue(ledger.reader_done)

    def test_relevant_event_timestamp_must_be_a_positive_integer(self):
        for timestamp in (None, 0, -1, True, '123', 2 ** 63):
            with self.subTest(timestamp=timestamp):
                ledger = self.ledger()
                with ledger.condition:
                    ledger.accept({'Type': 'container', 'Action': 'create', 'timeNano': timestamp,
                        'Actor': {'ID': 'a' * 64, 'Attributes': {'com.docker.compose.project': ledger.project,
                                  'com.docker.compose.service': 'romd-admin'}}})
                self.assertIn('invalid-event-timestamp', ledger.errors)
                self.assertEqual([], ledger.events)

    def test_foreign_project_events_and_exec_noise_cannot_add_coverage(self):
        ledger = self.ledger()
        self.accept(ledger, 'create', **{'com.docker.compose.project': 'foreign'})
        self.accept(ledger, 'exec_start')
        self.assertEqual([], ledger.events)
        self.assertEqual({}, ledger.containers)
        self.assertEqual([], ledger.errors)

    def test_manual_stop_may_arrive_before_or_after_die_but_is_required_each_run(self):
        for actions in (('stop', 'die'), ('die', 'stop')):
            with self.subTest(actions=actions):
                ledger = self.ledger()
                ledger.allowed_stops.add('romd-admin')
                self.accept(ledger, 'create')
                for _ in range(2):
                    self.accept(ledger, 'start')
                    for action in actions:
                        self.accept(ledger, action, **({'exitCode': '0'} if action == 'die' else {}))
                self.accept(ledger, 'destroy')
                self.assertEqual([], ledger.errors)

    def test_delayed_death_keeps_validated_stop_intent_and_unrequested_stop_fails(self):
        ledger = self.ledger()
        self.accept(ledger, 'create')
        self.accept(ledger, 'start')
        ledger.allowed_stops.add('romd-admin')
        self.accept(ledger, 'stop')
        ledger.allowed_stops.clear()
        self.accept(ledger, 'die', exitCode='0')
        self.accept(ledger, 'destroy')
        self.assertEqual([], ledger.errors)
        ledger = self.ledger()
        self.accept(ledger, 'create')
        self.accept(ledger, 'start')
        self.accept(ledger, 'stop')
        self.assertIn('unrequested-container-stop', ledger.errors)

    def test_destroy_after_zero_exit_requires_observed_stop_even_inside_shutdown_intent(self):
        ledger = self.ledger()
        ledger.allowed_stops.add('romd-admin')
        for action, attributes in (('create', {}), ('start', {}), ('die', {'exitCode': '0'}), ('destroy', {})):
            self.accept(ledger, action, **attributes)
        self.assertIn('destroy-with-incomplete-lifecycle', ledger.errors)

    def test_malformed_service_label_fails_without_crashing_reader(self):
        ledger = self.ledger()
        self.accept(ledger, 'create', **{'com.docker.compose.service': ['romd-admin']})
        self.assertIn('unexpected-project-service', ledger.errors)

    def test_receipt_write_failure_does_not_replace_original_failure_or_leak_collectors(self):
        write = Path.write_text
        def fail_receipt(path, *args, **kwargs):
            if path == self.gate.evidence:
                raise OSError('disk full')
            return write(path, *args, **kwargs)
        with self.environment(), patch.object(Path, 'write_text', new=fail_receipt):
            with self.assertRaisesRegex(ValueError, 'original functional failure'):
                with self.gate.monitor(self.cleanups()):
                    for project in self.projects:
                        self.engine.populate(project)
                    raise ValueError('original functional failure')
        self.assertIn('lifecycle-finalization-failed', self.gate.errors)
        self.assertEqual('failed', self.gate.receipt['result'])
        self.assertEqual({}, self.engine.containers)
        for ledger in self.gate.ledgers.values():
            self.assertIsNotNone(ledger.process.poll())
            self.assertFalse(ledger.thread.is_alive())
            self.assertTrue(ledger.process.stdout.closed)

    def test_source_runner_monitors_before_start_and_cleans_both_after_startup_failure(self):
        import smoke
        created = []
        cleanup_projects = []

        def factory(env, projects, image, evidence):
            from container_lifecycle import ContainerLifecycle
            gate = ContainerLifecycle(env, projects, image, evidence,
                private_root=self.directory / 'source-private', timeout=.5)
            created.append(gate)
            return gate

        def command(arguments, env, capture=False):
            if arguments[0] == 'openssl':
                return ''
            project = env['COMPOSE_PROJECT_NAME']
            self.assertEqual('127.0.0.2', env['ROMD_HTTPS_BIND'])
            if arguments[1] == 'up':
                self.assertTrue(all(ledger.ready for ledger in created[0].ledgers.values()))
                self.engine.populate(project)
                raise RuntimeError('source startup fixture failed')
            if arguments[1] == 'stop':
                self.engine.stop(project)
            elif arguments[1] == 'down':
                cleanup_projects.append(project)
                self.engine.down(project)
            else:
                self.fail(arguments)

        with self.environment(), patch.object(smoke, 'run', side_effect=command), \
                patch.object(smoke, 'ContainerLifecycle', side_effect=factory), \
                patch.object(sys, 'argv', ['smoke.py', '--https-bind', '127.0.0.2',
                                         '--evidence-dir', str(self.directory / 'source-evidence')]):
            with self.assertRaisesRegex(RuntimeError, 'source startup fixture failed'):
                smoke.main()
        self.assertEqual(2, len(cleanup_projects))
        self.assertEqual('passed', created[0].receipt['result'])
        self.assertEqual({}, self.engine.containers)

    def test_source_runner_rejects_non_loopback_ingress_before_startup(self):
        import smoke
        for address in ('0.0.0.0', '192.168.1.2', '::1', 'hostname'):
            with self.subTest(address=address), patch.object(sys, 'argv', ['smoke.py', '--https-bind', address]), \
                    patch.object(smoke, 'run') as command, contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    smoke.main()
                command.assert_not_called()

    def test_event_memory_and_container_limits_fail_closed(self):
        from container_lifecycle import MAX_EVENTS
        ledger = self.ledger()
        ledger.events = [{}] * MAX_EVENTS
        self.accept(ledger, 'create')
        self.assertIn('event-limit', ledger.errors)
        self.assertEqual(MAX_EVENTS, len(ledger.events))

    def test_container_count_limit_is_independent_of_event_limit(self):
        ledger = self.ledger()
        ledger.containers = {f'{number:064x}': {} for number in range(256)}
        self.accept(ledger, 'create')
        self.assertIn('event-limit', ledger.errors)
        self.assertEqual(256, len(ledger.containers))
        self.assertEqual([], ledger.events)

    def test_bounded_command_kills_timeout_even_after_child_closes_stdout(self):
        from container_lifecycle import bounded_command
        for script in ('import time; time.sleep(10)', 'import os,time; os.close(1); os.close(2); time.sleep(10)'):
            with self.subTest(script=script):
                try:
                    code, output, reason = bounded_command([sys.executable, '-c', script], dict(os.environ), timeout=.1)
                except subprocess.TimeoutExpired:
                    pass
                else:
                    self.assertEqual('timeout', reason)
                    self.assertNotEqual(0, code)
                    self.assertEqual(b'', output)

    def test_leftover_owned_probe_does_not_skip_real_stack_cleanup(self):
        with self.environment(), self.gate.monitor(self.cleanups()):
            for project in self.projects:
                self.engine.populate(project)
                self.gate.checkpoint(project)
            # A prior failed destroy acknowledgment may leave a never-started probe.
            self.engine.create(self.projects[0], session=self.gate.session, name='leftover-owned-probe')
        self.assertEqual('passed', self.gate.receipt['result'])
        self.assertEqual({}, self.engine.containers)

    def test_process_exit_after_final_acknowledgment_still_fails(self):
        from container_lifecycle import LifecycleError
        barrier = self.gate.barrier
        def observed(project):
            barrier(project)
            ledger = self.gate.ledgers[project]
            if all(state['destroyed'] for state in ledger.containers.values()):
                ledger.process.terminate()
        with self.environment(), self.assertRaises(LifecycleError):
            with self.gate.monitor(self.cleanups()):
                for project in self.projects:
                    self.engine.populate(project)
                    self.gate.checkpoint(project)
                # Trigger only the true final drain, after cleanup callbacks have run.
                original_cleanup = self.gate.cleanup
                count = []
                def cleanup(*arguments):
                    original_cleanup(*arguments)
                    count.append(1)
                    if len(count) == 2:
                        self.gate.barrier = observed
                self.gate.cleanup = cleanup
        self.assertTrue(any('collector-' in code for value in self.gate.receipt['projects'].values() for code in value['errors']))

    def test_reader_failure_after_final_ack_cannot_pass_with_cli_still_alive(self):
        from container_lifecycle import LifecycleError
        barrier = self.gate.barrier
        def corrupted(project):
            barrier(project)
            ledger = self.gate.ledgers[project]
            os.write(ledger.process.write_fd, b'not-json\n')
            deadline = time.monotonic() + 1
            with ledger.condition:
                while not ledger.reader_done and time.monotonic() < deadline:
                    ledger.condition.wait(.05)
            self.assertTrue(ledger.reader_done)
            self.assertIsNone(ledger.process.poll())
        with self.environment(), self.assertRaises(LifecycleError):
            with self.gate.monitor(self.cleanups()):
                for project in self.projects:
                    self.engine.populate(project)
                    self.gate.checkpoint(project)
                original_cleanup = self.gate.cleanup
                count = []
                def cleanup(*arguments):
                    original_cleanup(*arguments)
                    count.append(1)
                    if len(count) == 2:
                        self.gate.barrier = corrupted
                self.gate.cleanup = cleanup
        self.assertTrue(all('collector-malformed-event' in value['errors'] for value in self.gate.receipt['projects'].values()))

    def test_stuck_reader_never_enters_blocking_buffered_close_and_both_collectors_are_stopped(self):
        for ledger in self.gate.ledgers.values():
            ledger.process = Mock()
            ledger.process.poll.return_value = None
            ledger.thread = Mock()
            ledger.thread.is_alive.return_value = True
        receipt = self.gate.finish()
        self.assertEqual('failed', receipt['result'])
        for ledger in self.gate.ledgers.values():
            ledger.process.terminate.assert_called_once()
            ledger.thread.join.assert_called_once_with(timeout=5)
            ledger.process.stdout.close.assert_not_called()
            self.assertIn(ledger.role + '-collector-did-not-stop', receipt['errors'])

    def test_diagnostic_output_is_bounded_during_reading(self):
        from container_lifecycle import bounded_command
        # Real child emits a large single line; the reader kills at the cap.
        code, output, reason = bounded_command([sys.executable, '-c',
            'import os; os.write(1, b"x" * 200000)'], dict(os.environ), timeout=5, limit=4096)
        self.assertEqual('output-limit', reason)
        self.assertEqual(4096, len(output))


class DockerBuildCapabilityTests(unittest.TestCase):
    def test_context_export_skips_only_with_a_reason_and_never_on_hosted_ci(self):
        with patch.dict(os.environ, {'GITHUB_ACTIONS': 'true', 'ROMD_CONTAINER_ENGINE': 'podman'}):
            self.assertIsNone(docker_build_unavailable())
        with patch.dict(os.environ, {'ROMD_CONTAINER_ENGINE': 'podman'}), patch.dict(os.environ):
            os.environ.pop('GITHUB_ACTIONS', None)
            self.assertIn('Docker BuildKit', docker_build_unavailable())
        with patch.dict(os.environ, {'ROMD_CONTAINER_ENGINE': 'docker'}), patch.dict(os.environ), \
                patch('shutil.which', return_value=None):
            os.environ.pop('GITHUB_ACTIONS', None)
            self.assertIn('docker CLI not found', docker_build_unavailable())


class HostedBuildCacheTests(unittest.TestCase):
    def run_cleanup(self, *, actions='true', runner='github-hosted', changed=False):
        with tempfile.TemporaryDirectory() as directory:
            scratch = Path(directory)
            log = scratch / 'docker.jsonl'
            docker = scratch / 'docker'
            docker.write_text(f'#!{sys.executable}\n' + """
import json
import os
from pathlib import Path
import sys
log = Path(os.environ['CACHE_TEST_LOG'])
calls = log.read_text().splitlines() if log.exists() else []
args = sys.argv[1:]
with log.open('a') as output:
    output.write(json.dumps(args) + '\\n')
if args[:2] == ['image', 'inspect']:
    changed = os.environ['CACHE_TEST_CHANGED'] == '1' and bool(calls)
    for index in range(4):
        print('sha256:' + str(index + int(changed)) * 64)
elif args != ['builder', 'prune', '--all', '--force']:
    raise SystemExit('Unexpected Docker operation')
""")
            docker.chmod(0o700)
            result = subprocess.run(['bash', str(ROOT / 'scripts/ci/reclaim_build_cache.sh')],
                                    capture_output=True, text=True, timeout=10,
                                    env=dict(os.environ, PATH=str(scratch) + os.pathsep + os.environ['PATH'],
                                             GITHUB_ACTIONS=actions, RUNNER_ENVIRONMENT=runner,
                                             RUNNER_TEMP=directory, CACHE_TEST_LOG=str(log),
                                             CACHE_TEST_CHANGED=str(int(changed))))
            calls = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
            return result, calls

    def test_local_and_self_hosted_engines_are_never_accessed(self):
        for actions, runner in (('', ''), ('', 'github-hosted'), ('true', 'self-hosted')):
            with self.subTest(actions=actions, runner=runner):
                result, calls = self.run_cleanup(actions=actions, runner=runner)
                self.assertNotEqual(0, result.returncode)
                self.assertIn('outside GitHub-hosted Actions', result.stderr)
                self.assertEqual([], calls)

    def test_hosted_cleanup_only_prunes_build_cache_and_retains_all_four_images(self):
        result, calls = self.run_cleanup()
        self.assertEqual(0, result.returncode, result.stderr)
        images = ['romd-worker:test', 'romd-admin:test', 'romd-consumer:test', 'romd-player:test']
        inspect = ['image', 'inspect', '--format', '{{.Id}}', *images]
        self.assertEqual([inspect, ['builder', 'prune', '--all', '--force'], inspect], calls)

    def test_changed_image_ids_fail_the_step(self):
        result, _ = self.run_cleanup(changed=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Runtime image IDs changed', result.stderr)

    def test_production_job_reclaims_after_the_build_and_before_the_space_guard(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        self.assertLess(workflow.index('python3 scripts/deployment/smoke.py --build --vfs'),
                        workflow.index('bash scripts/ci/reclaim_build_cache.sh'))
        self.assertLess(workflow.index('bash scripts/ci/reclaim_build_cache.sh'),
                        workflow.index('python3 scripts/e2e/chd.py --synthetic'))


class ProductionContractTests(unittest.TestCase):
    def test_frontend_dependencies_include_every_workspace_manifest_before_frozen_install(self):
        before_install = dockerfile_stages()['frontend-deps'].split('RUN pnpm install --frozen-lockfile', 1)[0]
        copied_sources = set()
        for line in before_install.splitlines():
            if line.startswith('COPY '):
                copied_sources.update(shlex.split(line)[1:-1])
        manifests = {path.relative_to(ROOT).as_posix()
                     for path in (ROOT / 'web/packages').glob('*/package.json')}
        self.assertTrue(manifests, 'The frontend workspace inventory is empty')
        self.assertEqual([], sorted(manifests - copied_sources),
                         'Frozen workspace installation needs every package manifest before source copying')

    def test_postgres_remains_a_digest_pinned_official_image(self):
        image = json.loads((ROOT / 'scripts/deployment/base-images.json').read_text())['postgres']
        self.assertRegex(image, r'^postgres:18\.[0-9]+-bookworm@sha256:[a-f0-9]{64}$')
        self.assertEqual(image, test_environment()['ROMD_POSTGRES_IMAGE'])
        config = render('compose.build.yaml')
        self.assertEqual(image, config['services']['postgres']['image'])
        self.assertNotIn('build', config['services']['postgres'])
        self.assertNotIn('romd-postgres', dockerfile_stages())

    def test_player_and_qualification_ingress_use_the_same_reviewed_image(self):
        image = json.loads((ROOT / 'scripts/deployment/base-images.json').read_text())['ingress']
        self.assertRegex(image, r'^nginx:[A-Za-z0-9._-]+@sha256:[a-f0-9]{64}$')
        self.assertEqual(image, test_environment()['ROMD_NGINX_IMAGE'])
        self.assertEqual(image, dockerfile_chain('romd-player', 'amd64')[-1])
        self.assertEqual(image, dockerfile_chain('romd-player', 'arm64')[-1])

    def test_catalog_reader_compiler_assertion_and_receipt_match_the_pinned_builder(self):
        text = (ROOT / 'Dockerfile').read_text()
        version = re.search(r'^FROM --platform=\$BUILDPLATFORM golang:([0-9.]+)-bookworm@sha256:[a-f0-9]{64} AS catalog-reader-build$',
                            text, re.MULTILINE)[1]
        assertion = re.search(r'test "\$\(go env GOVERSION\)" = go([0-9.]+)', text)[1]
        receipt = re.search(r'"goVersion":"go([0-9.]+)"', text)[1]
        self.assertEqual(version, assertion)
        self.assertEqual(version, receipt)

    def test_docker_context_excludes_private_host_configuration(self):
        reason = docker_build_unavailable()
        if reason:
            self.skipTest(reason)
        private = ('appsettings.ProductionSecret.json',
                   'src/Romd.Admin.Host/appsettings.Development.json',
                   'src/Romd.Admin.Host/appsettings.Production.json',
                   'src/Romd.Consumer.Host/appsettings.Production.json',
                   'src/Romd.Worker.Host/appsettings.Production.json',
                   'nested/config/appsettings.ProductionSecret.json')
        public = ('src/Romd.Admin.Host/appsettings.json',
                  'src/Romd.Consumer.Host/appsettings.Development.json')

        def export_context(ignore, directory):
            context, output = directory / 'context', directory / 'output'
            context.mkdir(parents=True)
            (context / '.dockerignore').write_text(ignore)
            (context / 'Dockerfile').write_text('FROM scratch\nCOPY . /\n')
            for name in private:
                path = context / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('Harmless private-configuration sentinel.\n')
            for name in public:
                path = context / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes((ROOT / name).read_bytes())
            command = ['docker', 'buildx', 'build', '--progress=plain', '--output',
                       f'type=local,dest={output}', str(context)]
            result = subprocess.run(command, capture_output=True, text=True, timeout=90)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            return output

        def assert_safe(output):
            for name in private:
                self.assertFalse((output / name).exists(), f'Private host configuration entered Docker context: {name}')
            for name in public:
                self.assertEqual((output / name).read_bytes(), (ROOT / name).read_bytes())

        ignore = (ROOT / '.dockerignore').read_text()
        with tempfile.TemporaryDirectory(prefix='romd-context-contract-') as temporary:
            directory = Path(temporary).resolve()
            assert_safe(export_context(ignore, directory / 'protected'))
            # Exercise Docker's actual matching rules: removing the deny rule
            # or broadening the Development exception must make this gate fail.
            mutations = (ignore.replace('**/appsettings.*.json\n', ''),
                         ignore + '\n!**/appsettings.Development.json\n')
            for index, mutation in enumerate(mutations):
                with self.subTest(mutation=index):
                    output = export_context(mutation, directory / f'unsafe-{index}')
                    with self.assertRaises(AssertionError):
                        assert_safe(output)

    def test_server_images_carry_only_verified_chd_binaries_and_chdman_is_worker_only(self):
        stages = dockerfile_stages()
        image_stages = ('runtime', 'catalog-runtime', 'romd-admin', 'romd-consumer', 'romd-worker',
                        'romd-admin-dev', 'romd-consumer-dev')
        native_copies = {name: [line for line in stages[name].splitlines()
                                if '--from=libchdr-artifacts' in line or '--from=chdman-artifacts' in line]
                         for name in image_stages}
        self.assertEqual(LIBCHDR_COPIES, native_copies['runtime'])
        self.assertEqual(CHDMAN_COPIES, native_copies['romd-worker'])
        for name in set(image_stages) - {'runtime', 'romd-worker'}:
            self.assertEqual([], native_copies[name], name)
        self.assertIn('ldd /app/native/libchdr/native/libmoonlark_chdr.so', stages['runtime'])
        self.assertIn('ldd /app/native/chdman/native/chdman', stages['romd-worker'])
        self.assertIn("grep -q 'not found'", stages['runtime'])
        self.assertIn("grep -q 'not found'", stages['romd-worker'])
        self.assertIn('chdman help', stages['romd-worker'])
        root_owned_read_only = 'test -z "$(find /app/native \\( ! -user root -o -perm /022 \\) -print)"'
        self.assertIn(root_owned_read_only, stages['runtime'])
        self.assertIn(root_owned_read_only, stages['romd-worker'])
        self.assertIn('chown -R "${APP_UID}:0" /var/lib/romd\n', stages['runtime'])
        self.assertNotRegex(stages['runtime'], r'chown[^\n]* /app\b')
        self.assertIn('COPY --from=libchdr-notices / /usr/share/doc/romd/', stages['runtime'])
        self.assertIn('COPY --from=chdman-notices / /usr/share/doc/romd/', stages['romd-worker'])
        self.assertNotIn('chdman', stages['romd-player'])

    def test_image_nuget_restores_are_locked_and_keep_portable_graphs_separate(self):
        stages = dockerfile_stages()
        restores = [line for line in stages['backend-build'].splitlines() if 'dotnet restore ' in line]
        self.assertEqual(4, len(restores))
        for line in restores:
            self.assertIn('--runtime "$rid"', line)
            self.assertIn('--locked-mode', line)
            self.assertIn('--lock-file-path "packages.$rid.lock.json"', line)
        self.assertIn('COPY Directory.Packages.props ./', stages['native-builder'])
        self.assertIn('-p:RestoreLockedMode=true', stages['native-builder'])
        props = ET.parse(ROOT / 'Directory.Packages.props')
        self.assertEqual('true', props.findtext('.//RestorePackagesWithLockFile'))
        locked = props.find('.//RestoreLockedMode')
        self.assertEqual('true', locked.text)
        self.assertEqual("'$(CI)' == 'true'", locked.attrib['Condition'])

    def test_chd_is_on_in_both_linux_images_and_feed_plumbing_is_gone(self):
        instructions = [line for line in (ROOT / 'Dockerfile').read_text().splitlines()
                        if not line.lstrip().startswith('#')]
        for retired in ('chd-assets', 'COPY artifacts/', '--configfile', 'ChdAssetsPath'):
            self.assertFalse([line for line in instructions if retired in line], retired)
        # Every Linux server host reads CHD; only the worker encodes. Qualification remains per RID.
        expected_hosts = {
            'romd-admin': LIBCHDR_ENVIRONMENT, 'romd-consumer': LIBCHDR_ENVIRONMENT,
            'romd-worker': LIBCHDR_ENVIRONMENT | CHDMAN_ENVIRONMENT,
            'romd-admin-dev': LIBCHDR_ENVIRONMENT, 'romd-consumer-dev': LIBCHDR_ENVIRONMENT, 'romd-player': {}}
        for arch in ('amd64', 'arm64'):
            for target, environment in expected_hosts.items():
                chain = dockerfile_chain(target, arch)
                if target != 'romd-player':
                    self.assertIn('runtime', chain, (target, arch))
                self.assertEqual(environment, native_environment(chain), (target, arch))
        for name, body in dockerfile_stages().items():
            for line in body.splitlines():
                if 'Romd__Native__' in line and not line.lstrip().startswith('#'):
                    self.assertTrue(name in ('runtime-amd64', 'runtime-arm64', 'worker-runtime-amd64', 'worker-runtime-arm64')
                                    and line.startswith('ENV '), (name, line))
        self.assertFalse([line for line in (ROOT / '.dockerignore').read_text().splitlines()
                          if line.startswith('!artifacts')])
        for name, service in render()['services'].items():
            self.assertFalse([key for key in service.get('environment', {})
                              if key.startswith(('Romd__Native__', 'Romd__ChdAssetsPath'))], name)
        for path in [*ROOT.glob('compose*.yaml'), *(ROOT / 'deploy').rglob('*.yaml'),
                     ROOT / '.env.example', ROOT / 'deploy' / '.env.example']:
            self.assertNotIn('Romd__Native__', path.read_text(), path)

    def test_chdman_source_pin_matches_build_and_notices(self):
        pin = json.loads((ROOT / 'native/chdman/mame.json').read_text())
        dockerfile = (ROOT / 'Dockerfile').read_text()
        self.assertIn(f"ADD --checksum=sha256:{pin['sourceSha256']} \\\n    {pin['sourceUrl']} ", dockerfile)
        self.assertIn('native verify --manifest', dockerfile)
        notices = (ROOT / 'native/chdman/notices/THIRD_PARTY_NOTICES.md').read_text()
        for value in (pin['sourceUrl'], pin['sourceSha256'], f"{pin['sourceBytes']:,}"):
            self.assertIn(value, notices)
        sdl = re.search(r'ADD --checksum=sha256:([0-9a-f]{64}) \\\n    (https://github\.com/libsdl-org/SDL/releases/download/release-2\.32\.10/SDL2-2\.32\.10\.tar\.gz) ', dockerfile)
        self.assertIsNotNone(sdl)
        for value in (sdl[2], sdl[1]):
            self.assertIn(value, notices)

    def test_release_attaches_verified_chdman_source_before_publishing_images(self):
        workflow = (ROOT / '.github/workflows/release.yml').read_text()
        self.assertLess(workflow.index('jq -er .sourceUrl native/chdman/mame.json'),
                        workflow.index('python3 scripts/release/publication.py'))
        for check in ('jq -er .sourceBytes native/chdman/mame.json', 'jq -er .sourceSha256 native/chdman/mame.json',
                      'archive="release-output/${url##*/}"', 'test "$(stat -c %s "$archive")" = "$bytes"',
                      'sha256sum --check --strict -', '--source-archive "release-output/${source_url##*/}"'):
            self.assertIn(check, workflow)

    def test_media_child_has_no_host_or_external_tool_dependencies(self):
        project = ET.parse(ROOT / 'src/Romd.Media.Worker/Romd.Media.Worker.csproj').getroot()
        self.assertEqual(['../Romd.Media/Romd.Media.csproj'],
                         [item.attrib['Include'] for item in project.findall('.//ProjectReference')])
        self.assertEqual([], project.findall('.//PackageReference'))
        self.assertEqual('Exe', project.findtext('.//OutputType'))
        dockerfile = (ROOT / 'Dockerfile').read_text()
        worker_image = dockerfile.split(' AS romd-worker\n', 1)[1].split('\nFROM ', 1)[0]
        self.assertIn('test -x /usr/bin/prlimit', worker_image)
        self.assertIn('test -x /usr/bin/nice', worker_image)
        worker = ET.parse(ROOT / 'src/Romd.Worker.Host/Romd.Worker.Host.csproj').getroot()
        self.assertIn('../../build/Romd.Media.Worker.targets',
                      [item.attrib['Project'] for item in worker.findall('.//Import')])
        for host in ('Romd.Admin.Host', 'Romd.Consumer.Host'):
            api = ET.parse(ROOT / f'src/{host}/{host}.csproj').getroot()
            self.assertFalse(any('Media.Worker' in item.attrib['Project'] for item in api.findall('.//Import')))

    def test_smoke_fixture_container_copies_are_readable_under_private_umask_without_changing_sources(self):
        from smoke import copy_readable_fixture
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            old_mask = os.umask(0o077)
            try:
                container = work / 'container'
                container.mkdir()
                # Model Docker cp's root-owned copy preserving the host mode, then
                # execute the requested chmod against a separate real filesystem.
                # This does not impersonate container uid 1654; the real smoke
                # drill validates that user can actually read the fixture.
                compose = work / 'compose'
                compose.write_text('#!' + sys.executable + r'''
import os, pathlib, shutil, sys
args = sys.argv[1:]
root = pathlib.Path(os.environ['SMOKE_TEST_CONTAINER'])
if args[0] == 'cp':
    source, target = pathlib.Path(args[1]), args[2]
    assert target.startswith('romd-admin:/tmp/')
    shutil.copy2(source, root / pathlib.Path(target).name)
elif args[:6] == ['exec', '-T', '--user', 'root', 'romd-admin', 'chmod']:
    os.chmod(root / pathlib.Path(args[7]).name, int(args[6], 8))
else:
    raise SystemExit('unexpected fixture command')
''')
                compose.chmod(0o700)
                env = dict(os.environ, SMOKE_TEST_CONTAINER=str(container))
                private_key = work / 'privkey.pem'
                private_key.write_bytes(b'private fixture key')
                sources = []
                for name, destination in (('fullchain.pem', '/tmp/smoke-ca.pem'),
                                          ('fixture.dat', '/tmp/vfs-smoke.dat'),
                                          ('read-only-input.iso', '/tmp/vfs-smoke.iso')):
                    source = work / name
                    source.write_bytes(b'read-only fixture bytes')
                    self.assertEqual(0o600, source.stat().st_mode & 0o777)
                    original = source.stat()
                    copy_readable_fixture(compose, env, source, destination)
                    copied = container / Path(destination).name
                    self.assertEqual(source.read_bytes(), copied.read_bytes())
                    self.assertEqual(0o644, copied.stat().st_mode & 0o777)
                    self.assertEqual(original.st_mode, source.stat().st_mode)
                    self.assertEqual(original.st_mtime_ns, source.stat().st_mtime_ns)
                    sources.append(source)
                self.assertEqual(0o600, private_key.stat().st_mode & 0o777)
                self.assertEqual(b'private fixture key', private_key.read_bytes())
                self.assertEqual(0o700, work.stat().st_mode & 0o777)
                self.assertEqual({'smoke-ca.pem', 'vfs-smoke.dat', 'vfs-smoke.iso'},
                                 {path.name for path in container.iterdir()})
                for destination in ('/tmp/privkey.pem', '/etc/ssl/private/key.pem', '/tmp/../tmp/smoke-ca.pem'):
                    with self.subTest(destination=destination), self.assertRaises(ValueError):
                        copy_readable_fixture(compose, env, sources[0], destination)
                self.assertEqual(3, len(list(container.iterdir())))
            finally:
                os.umask(old_mask)

    def test_smoke_system_lookup_follows_reference_pages(self):
        from smoke import find_system_key
        pages = {None: {'items': [{'key': '3do', 'name': '3DO Interactive Multiplayer'}],
                        'nextCursor': 'opaque+/=', 'hasNextPage': True},
                 'opaque+/=': {'items': [{'key': 'gc', 'name': 'Nintendo GameCube'}],
                               'nextCursor': None, 'hasNextPage': False}}
        paths = []

        def fetch(path):
            paths.append(path)
            query = parse_qs(urlsplit(path).query)
            self.assertEqual(['100'], query['limit'])
            return pages[query.get('cursor', [None])[0]]

        self.assertEqual('gc', find_system_key(fetch, 'GameCube'))
        self.assertEqual(['/api/systems', '/api/systems'], [urlsplit(path).path for path in paths])
        with self.assertRaisesRegex(AssertionError, 'Dreamcast system is missing'):
            find_system_key(fetch, 'Dreamcast')

        empty = {'key': '3do', 'name': '3DO Interactive Multiplayer'}
        for name, cursors in (('repeated', ['again', 'again']), ('null', [None]), ('blank', [''])):
            with self.subTest(cursor=name):
                pending = list(cursors)
                with self.assertRaisesRegex(AssertionError, 'invalid or repeated next cursor'):
                    find_system_key(lambda path: {'items': [empty], 'nextCursor': pending.pop(0) if pending else 'end',
                                                  'hasNextPage': True}, 'GameCube')
        with self.assertRaisesRegex(AssertionError, 'cursor after its final page'):
            find_system_key(lambda path: {'items': [empty], 'nextCursor': 'unexpected', 'hasNextPage': False}, 'GameCube')

    def test_volume_helpers_use_explicit_root_and_export_preserves_binary_stdout(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = Path(directory) / 'docker-arguments'
            env = test_environment()
            env.update(ROMD_BACKUP_MODE='compose', ROMD_BACKUP_HELPER_IMAGE='fixture-image',
                       ROMD_TEST_DOCKER_TRACE=str(trace))
            script = r'''
source "$1"
romd_backup_worker_container() { printf '%s\n' fixture-worker; }
docker() {
    printf '%s\n' "$@" > "$ROMD_TEST_DOCKER_TRACE"
    if [[ "$ROMD_TEST_ACTION" == export ]]; then printf 'archive\000bytes'; fi
    return 0
}
"$2"
'''
            for action, function in [('export', 'romd_backup_data_export'),
                                     ('import', 'romd_backup_data_import'),
                                     ('empty', 'romd_backup_data_is_empty')]:
                with self.subTest(action=action):
                    result = subprocess.run(['bash', '-c', script, 'backup-contract',
                                             str(ROOT / 'scripts/backup/lib.sh'), function],
                                            env=dict(env, ROMD_TEST_ACTION=action),
                                            input=b'', capture_output=True, check=True)
                    self.assertEqual(b'archive\x00bytes' if action == 'export' else b'', result.stdout)
                    arguments = trace.read_text().splitlines()
                    self.assertEqual('0:0', arguments[arguments.index('--user') + 1])
                    self.assertEqual('fixture-worker', arguments[arguments.index('--volumes-from') + 1])
                    self.assertIn('--rm', arguments)
                    if action == 'export':
                        self.assertEqual('none', arguments[arguments.index('--log-driver') + 1])

    def test_vfs_is_opt_in_consumer_only_and_limits_are_validated(self):
        base = render()
        self.assertEqual('false', base['services']['romd-consumer']['environment']['Romd__Vfs__Enabled'])
        env = test_environment()
        env.update(ROMD_VFS_ENABLED='true', ROMD_VFS_MAX_REQUESTS='16',
                   ROMD_VFS_MAX_REQUESTS_PER_USER='2', ROMD_VFS_MAX_DIRECTORY_ENTRIES='500')
        enabled = render(environment=env)
        self.assertEqual([], validate(enabled))
        consumer = enabled['services']['romd-consumer']['environment']
        self.assertEqual('true', consumer['Romd__Vfs__Enabled'])
        self.assertEqual('16', consumer['Romd__Vfs__MaximumConcurrentRequests'])
        self.assertEqual('2', consumer['Romd__Vfs__MaximumConcurrentRequestsPerUser'])
        self.assertEqual('500', consumer['Romd__Vfs__MaximumDirectoryEntries'])
        for name in ('romd-admin', 'romd-worker', 'romd-player'):
            self.assertFalse(any(key.startswith('Romd__Vfs__') for key in enabled['services'][name]['environment']))
        for key, value in [('Romd__Vfs__Enabled', 'yes'), ('Romd__Vfs__MaximumConcurrentRequests', '0'),
                           ('Romd__Vfs__MaximumConcurrentRequestsPerUser', '129'),
                           ('Romd__Vfs__MaximumDirectoryEntries', '100001')]:
            invalid = copy.deepcopy(enabled)
            invalid['services']['romd-consumer']['environment'][key] = value
            self.assertTrue(validate(invalid))

    def test_admin_buffers_uploads_on_the_data_volume(self):
        config = render()
        self.assertEqual('/var/lib/romd/temp/requests',
                         config['services']['romd-admin']['environment']['ASPNETCORE_TEMP'])
        for value in (None, '/tmp'):
            invalid = copy.deepcopy(config)
            environment = invalid['services']['romd-admin']['environment']
            if value is None:
                del environment['ASPNETCORE_TEMP']
            else:
                environment['ASPNETCORE_TEMP'] = value
            self.assertIn('romd-admin: ASPNETCORE_TEMP must buffer uploads under /var/lib/romd/temp/', validate(invalid))

    def test_worker_owns_namespace_init_for_media_extinction_recovery(self):
        config = render()
        self.assertIs(config['services']['romd-worker']['init'], False)
        self.assertNotIn('pid', config['services']['romd-worker'])
        for override in ({'init': True}, {'init': None}, {'pid': 'host'}, {'pid': 'service:romd-admin'}):
            invalid = copy.deepcopy(config)
            invalid['services']['romd-worker'].update(override)
            self.assertIn('romd-worker: run as namespace PID 1 with init: false and a private PID namespace for media recovery',
                          validate(invalid))

    def test_base_has_no_builds_ports_or_personal_container_names(self):
        config = render()
        self.assertEqual([], validate(config))
        for service in config['services'].values():
            for key in ('build', 'ports', 'container_name'):
                self.assertNotIn(key, service)
            self.assertIn('max-size', service['logging']['options'])

    def test_rejects_disconnected_database_regression(self):
        config = render()
        config['services']['romd-consumer']['networks'] = {'edge': None}
        self.assertIn('romd-consumer: no shared network with PostgreSQL', validate(config))

    def test_ingress_examples_preserve_backend_connectivity_and_isolation(self):
        for example in ('https', 'cloudflare'):
            config = render(f'deploy/examples/{example}/compose.yaml')
            self.assertEqual([], validate(config))
            self.assertEqual({'edge'}, set(config['services']['ingress']['networks']))
            addresses = [config['services'][name]['networks']['edge']['ipv4_address']
                         for name in ('ingress', 'romd-admin', 'romd-consumer', 'romd-player')]
            self.assertEqual(4, len(set(addresses)))
            if example == 'cloudflare':
                self.assertFalse(any(s.get('ports') for s in config['services'].values()))
                address = config['services']['cloudflared']['networks']['edge']['ipv4_address']
                self.assertNotIn(address, addresses)
                self.assertEqual(address, config['services']['ingress']['environment']['ROMD_CLOUDFLARED_EDGE_IP'])
            template = (ROOT / f'deploy/examples/{example}/templates/default.conf.template').read_text()
            self.assertIn('return 421', template)
            self.assertNotIn('$http_x_forwarded_proto', template)

    def test_vfs_ingress_streams_responses_without_changing_other_routes(self):
        for example in ('https', 'cloudflare'):
            with self.subTest(example=example):
                template = (ROOT / f'deploy/examples/{example}/templates/default.conf.template').read_text()
                # VFS must inherit the server's headers/timeouts and preserve the
                # original escaped URI; rewriting it can change ROM filenames.
                locations = re.findall(r'location\s+(\S+)\s*\{([^{}]*)\}', template)
                self.assertEqual({'/', '/vfs/'}, {path for path, _ in locations})
                directives = {path: [line.strip() for line in body.split(';') if line.strip()]
                              for path, body in locations}
                self.assertEqual(['proxy_buffering off', 'proxy_pass $romd_upstream$request_uri'],
                                 directives['/vfs/'])
                self.assertEqual(['proxy_pass $romd_upstream$request_uri'], directives['/'])
                self.assertEqual(1, template.count('proxy_buffering'))

    def test_secrets_origins_and_player_boundary_are_validated(self):
        original = render()
        config = copy.deepcopy(original)
        config['services']['postgres']['environment']['ROMD_POSTGRES_ADMIN_PASSWORD'] = 'unsafe;password'
        self.assertTrue(validate(config))
        config = copy.deepcopy(original)
        config['services']['romd-worker']['environment']['Romd__Auth__ConsumerSpa__PostLogoutRedirectUris__0'] = 'http://console.example.test'
        self.assertTrue(validate(config))
        config = copy.deepcopy(original)
        config['services']['romd-player']['networks']['backend'] = None
        self.assertTrue(validate(config))

    def test_ingress_host_and_address_mismatch_are_rejected(self):
        config = render('deploy/examples/https/compose.yaml')
        config['services']['ingress']['environment']['ROMD_ADMIN_HOST'] = 'wrong.example.test'
        self.assertTrue(validate(config))
        config = render('deploy/examples/https/compose.yaml')
        config['services']['ingress']['networks']['edge']['ipv4_address'] = '172.28.0.3'
        self.assertTrue(validate(config))

    def test_cloudflare_connector_requires_a_pinned_usable_nonoverlapping_ipv4_address(self):
        original = render('deploy/examples/cloudflare/compose.yaml')
        for address in ('not-an-ip', '172.28.0.6/32', '2001:db8::6', '172.29.0.6',
                        '172.28.0.0', '172.28.0.1', '172.28.0.255', '172.28.0.128',
                        '172.28.0.2', '172.28.0.3', '172.28.0.4', '172.28.0.5'):
            with self.subTest(address=address):
                config = copy.deepcopy(original)
                config['services']['ingress']['environment']['ROMD_CLOUDFLARED_EDGE_IP'] = address
                config['services']['cloudflared']['networks']['edge']['ipv4_address'] = address
                self.assertTrue(validate(config), address)
        config = copy.deepcopy(original)
        config['services']['ingress']['environment']['ROMD_CLOUDFLARED_EDGE_IP'] = '172.28.0.7'
        self.assertIn('Cloudflared static IP must match the ingress trusted connector', validate(config))
        config = copy.deepcopy(original)
        del config['services']['ingress']['environment']['ROMD_CLOUDFLARED_EDGE_IP']
        self.assertTrue(validate(config))
        config = copy.deepcopy(original)
        config['networks']['edge']['ipam']['config'][0]['gateway'] = '172.28.0.6'
        self.assertIn('Cloudflared needs a usable static IP inside the edge subnet', validate(config))

    def test_cloudflare_rejects_published_ports_and_connector_backend_access(self):
        original = render('deploy/examples/cloudflare/compose.yaml')
        for ports in ([{'target': 80, 'published': '8080'}], [{'target': 80}]):
            with self.subTest(ports=ports):
                config = copy.deepcopy(original)
                config['services']['ingress']['ports'] = ports
                self.assertIn('Cloudflare tunnel listener must not publish port 80', validate(config))
        for ports in ([{'target': 8443, 'published': '8443'}], {}, None, '8443:8443'):
            with self.subTest(connector_ports=ports):
                config = copy.deepcopy(original)
                config['services']['cloudflared']['ports'] = ports
                self.assertIn('Cloudflared must not publish ports', validate(config))
        config = copy.deepcopy(original)
        config['services']['cloudflared']['networks']['backend'] = None
        self.assertIn('Cloudflared must join only the edge network', validate(config))
        config = copy.deepcopy(original)
        config['services']['romd-consumer']['environment']['Romd__ForwardedHeaders__KnownProxies__0'] = '172.28.0.6'
        self.assertIn('Ingress static IP must match the API trusted proxy', validate(config))

    def test_cloudflare_allows_separate_custom_listener_without_exposing_tunnel_port(self):
        config = render('deploy/examples/cloudflare/compose.yaml')
        config['services']['ingress']['ports'] = [{'target': 8443, 'published': '8443',
                                                 'host_ip': '127.0.0.1', 'protocol': 'tcp'}]
        self.assertEqual([], validate(config))

    def test_cloudflare_rejects_malformed_ingress_port_targets(self):
        original = render('deploy/examples/cloudflare/compose.yaml')
        for ports in ({}, None, '8443:8443', [None], ['8443:8443'], [{}],
                      [{'target': True}], [{'target': '80'}], [{'target': '8000-8080'}],
                      [{'target': 0}], [{'target': 65536}]):
            with self.subTest(ports=ports):
                config = copy.deepcopy(original)
                config['services']['ingress']['ports'] = ports
                self.assertIn('Ingress published ports need normalized valid target mappings', validate(config))

    def test_release_bundle_is_source_free_and_contains_restore_dependencies(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            images = work / 'images.env'
            images.write_text(''.join(f'ROMD_{name}_IMAGE=example/{name.lower()}@sha256:{"a" * 64}\n'
                                      for name in ('ADMIN', 'CONSUMER', 'WORKER', 'PLAYER', 'POSTGRES')))
            archive = work / 'release.tar.gz'
            environment = portable_environment(work, ('bash', 'python3', 'dirname', 'mktemp', 'mkdir',
                                                       'cp', 'tar', 'gzip', 'rm'))
            self.assertIsNone(shutil.which('shasum', path=environment['PATH']))
            subprocess.run([str(ROOT / 'scripts/deployment/bundle.sh'), str(images), str(archive), 'a' * 40, 'v0.0.0-test'],
                           env=environment, check=True, umask=0o077)
            with tarfile.open(archive) as bundle:
                for member in bundle.getmembers():
                    if member.isdir() and member.name not in ('.', './'):
                        self.assertEqual(0o755, member.mode & 0o777, member.name)
                    elif member.isfile():
                        self.assertIn(member.mode & 0o777, (0o644, 0o755), member.name)
                for name in ('compose.yaml', 'images.env', 'manifest.json'):
                    self.assertEqual(0o644, bundle.getmember('./' + name).mode & 0o777, name)
                self.assertEqual(0o755, bundle.getmember('./deploy/postgres/init/10-romd-roles.sh').mode & 0o777)
                names = {name.removeprefix('./') for name in bundle.getnames()}
                for required in ('compose.yaml', 'images.env', '.env.example',
                                 'scripts/backup/restore.sh', 'scripts/deployment/compose.sh',
                                 'deploy/examples/cloudflare/compose.yaml',
                                 'deploy/examples/cloudflare/templates/default.conf.template',
                                 'docs/media-processing.md', 'docs/configuration.md',
                                 'deploy/postgres/init/10-romd-roles.sh',
                                 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'TRADEMARKS.md',
                                 'brand/fonts/Fraunces-OFL.txt',
                                 'web/packages/romd-foundation/src/fonts/Archivo-OFL.txt',
                                 'web/packages/romd-foundation/src/fonts/DMSans-OFL.txt',
                                 'web/packages/romd-foundation/src/fonts/IBMPlexMono-OFL.txt',
                                 'reference-data/assets/presentation/platforms/ATTRIBUTION.md',
                                 'reference-data/assets/presentation/ratings/ATTRIBUTION.md'):
                    self.assertIn(required, names)
                self.assertIn('ROMD_CLOUDFLARED_EDGE_IP=172.28.0.6',
                              bundle.extractfile('./.env.example').read().decode())
                self.assertIn('manifest.json', names)
                self.assertIn('notices/Moonlark.Libchdr/LICENSE', names)
                self.assertIn('native/libchdr/notices/LICENSE.txt', names)
                self.assertIn('native/chdman/notices/THIRD_PARTY_NOTICES.md', names)
                manifest = json.load(bundle.extractfile('./manifest.json'))
                self.assertEqual('a' * 40, manifest['sourceCommit'])
                self.assertEqual('v0.0.0-test', manifest['releaseTag'])
                self.assertEqual('linux/amd64', manifest['platform'])
                self.assertNotIn('.env', names)
                self.assertFalse(any(name.startswith(('src/', '.git/')) for name in names))
                web_files = {member.name.removeprefix('./') for member in bundle.getmembers()
                             if member.isfile() and member.name.removeprefix('./').startswith('web/')}
                self.assertEqual({
                    'web/packages/romd-foundation/src/fonts/Archivo-OFL.txt',
                    'web/packages/romd-foundation/src/fonts/DMSans-OFL.txt',
                    'web/packages/romd-foundation/src/fonts/IBMPlexMono-OFL.txt',
                }, web_files)
                documentation = work / 'documentation'
                documentation.mkdir()
                for member in bundle.getmembers():
                    if member.isfile() and member.name.endswith('.md'):
                        target = documentation / member.name.removeprefix('./')
                        target.parent.mkdir(parents=True, exist_ok=True)
                        target.write_bytes(bundle.extractfile(member).read())
                # Include retained non-Markdown link targets without extracting arbitrary paths.
                for member in bundle.getmembers():
                    if member.isfile() and not member.name.endswith('.md'):
                        target = documentation / member.name.removeprefix('./')
                        target.parent.mkdir(parents=True, exist_ok=True)
                        target.touch()
                for document in documentation.rglob('*.md'):
                    self.assertEqual([], check_document(document, documentation))
                guide = (documentation / 'docs/production-deployment.md').read_text()
                self.assertIn('(configuration.md#media-processing)', guide)
                self.assertIn('/blob/' + 'a' * 40 + '/docs/architecture/media-status.md', guide)
                self.assertNotIn('github.com/MoonlarkStudios/romd/blob/main/', guide)
            self.assertEqual(f'{hashlib.sha256(archive.read_bytes()).hexdigest()}  {archive.name}\n',
                             Path(str(archive) + '.sha256').read_text())

    def test_packaged_backup_hashes_binary_files_without_shasum_or_checkout_dependencies(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            library = work / 'installation/scripts/backup/lib.sh'
            library.parent.mkdir(parents=True)
            shutil.copy2(ROOT / 'scripts/backup/lib.sh', library)
            artifact = work / 'binary backup.dump'
            artifact.write_bytes(bytes(range(256)) * 8193)
            environment = portable_environment(work, ('python3', 'dirname'))
            command = ['/bin/bash', '-c', 'source "$1"; romd_backup_sha256 "$2"',
                       'backup-hash-contract', str(library), str(artifact)]
            result = subprocess.run(command, env=environment, capture_output=True, check=True)
            self.assertEqual(hashlib.sha256(artifact.read_bytes()).hexdigest().encode() + b'\n', result.stdout)
            artifact.unlink()
            missing = subprocess.run(command, env=environment, capture_output=True)
            self.assertNotEqual(0, missing.returncode)
            self.assertEqual(b'', missing.stdout)

    def test_native_recipe_cache_identity_matches_sorted_listing_without_shasum(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            script = work / 'scripts/native/build.sh'
            script.parent.mkdir(parents=True)
            shutil.copy2(ROOT / 'scripts/native/build.sh', script)
            helper = work / 'scripts/containers/runtime.py'
            helper.parent.mkdir(parents=True)
            shutil.copy2(ROOT / 'scripts/containers/runtime.py', helper)
            recipe = work / 'tools/Moonlark.Native.Engineering/Chdman'
            (recipe / 'nested').mkdir(parents=True)
            (recipe / 'ChdmanBuild.cs').write_text('internal const int RecipeVersion = 7;\n')
            (recipe / 'A.cs').write_text('first recipe source\n')
            changed = recipe / 'nested/B space.cs'
            changed.write_text('nested recipe source\n')
            listing = ''.join(hashlib.sha256(path.read_bytes()).hexdigest() + '  '
                              + path.relative_to(work).as_posix() + '\n'
                              for path in sorted(recipe.rglob('*.cs')))
            recipe_hash = hashlib.sha256(listing.encode()).hexdigest()
            pin = work / 'native/chdman/mame.json'
            pin.parent.mkdir(parents=True)
            pin.write_text(json.dumps(dict(tag='fixture', sourceUrl='https://example.test/mame.tar.gz',
                                           sourceBytes=123, sourceSha256='a' * 64)))
            output = work / 'artifacts/tools/chdman/linux-x64'
            (output / 'native').mkdir(parents=True)
            binary = output / 'native/chdman'
            binary.write_bytes(b'fixture-native-binary')
            (output / 'build-manifest.json').write_text(json.dumps(dict(rid='linux-x64', recipeVersion=7,
                recipeSha256=recipe_hash, pin={'sourceSha256': 'a' * 64},
                binary={'sha256': hashlib.sha256(binary.read_bytes()).hexdigest()})))
            environment = portable_environment(work, ('bash', 'python3', 'dirname', 'sed', 'rm'))
            environment['ROMD_CONTAINER_ENGINE'] = 'docker'  # This isolated fixture supplies only a fake Docker CLI.
            tools = Path(environment['PATH'])
            # No actual .NET or Docker build is needed to exercise the cache decision.
            for name, body in {'dotnet': 'exit 0', 'uname': 'case "$1" in -s) echo Linux;; -m) echo x86_64;; esac',
                               'nproc': 'echo 1', 'docker': 'exit 42'}.items():
                (tools / name).write_text('#!/bin/bash\n' + body + '\n')
                (tools / name).chmod(0o755)
            cached = subprocess.run([str(script), 'chdman'], env=environment, capture_output=True, text=True)
            self.assertEqual(0, cached.returncode, cached.stdout + cached.stderr)
            self.assertIn('chdman linux-x64 is current', cached.stdout)
            changed.write_text('changed nested recipe source\n')
            stale = subprocess.run([str(script), 'chdman'], env=environment, capture_output=True, text=True)
            self.assertEqual(42, stale.returncode, stale.stdout + stale.stderr)
            self.assertNotIn('is current', stale.stdout)

    def test_missing_secret_fails_compose_interpolation(self):
        env = test_environment()
        env['ROMD_POSTGRES_ADMIN_PASSWORD'] = ''
        result = subprocess.run(compose('--env-file', '/dev/null', '-f', str(ROOT / 'compose.yaml'), 'config', '--quiet'),
                                env=env, capture_output=True)
        self.assertNotEqual(0, result.returncode)


if __name__ == '__main__':
    try:
        compose()
    except ContainerError as error:
        print(f'Container setup: {error}', file=sys.stderr)
        sys.exit(1)
    unittest.main(verbosity=2)
