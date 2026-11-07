"""Fail-closed, continuously observed lifecycle for disposable Docker drills.

This is runner tooling, never part of an installation bundle. Raw container output
stays in a separate private directory; the public receipt contains selected fields.
"""
from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import secrets
import selectors
import subprocess
import threading
import time


ROOT = Path(__file__).resolve().parents[2]
SERVICES = frozenset(('postgres', 'romd-worker', 'romd-admin', 'romd-consumer', 'romd-player', 'ingress'))
HOSTS = frozenset(('romd-worker', 'romd-admin', 'romd-consumer'))
PROJECT_LABEL = 'com.docker.compose.project'
SESSION_LABEL = 'io.romd.lifecycle-session'
ID = re.compile('[0-9a-f]{64}')
IMAGE = re.compile(r'[a-z0-9][a-z0-9./:_-]*@sha256:[0-9a-f]{64}')
MAX_OUTPUT = 128 * 1024
MAX_EVENTS = 10000


class LifecycleError(RuntimeError):
    pass


def bounded_command(command, env, *, timeout=30, limit=MAX_OUTPUT):
    """Bound while reading, including a single enormous Docker log line."""
    process = subprocess.Popen([str(item) for item in command], env=env, stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    output = bytearray()
    reason = None
    deadline = time.monotonic() + timeout
    try:
        with selectors.DefaultSelector() as selected:
            selected.register(process.stdout, selectors.EVENT_READ)
            while selected.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    reason = 'timeout'
                    break
                for key, _ in selected.select(min(remaining, .2)):
                    chunk = os.read(key.fileobj.fileno(), min(16384, limit + 1 - len(output)))
                    if not chunk:
                        selected.unregister(key.fileobj)
                    else:
                        output.extend(chunk)
                        if len(output) > limit:
                            reason = 'output-limit'
                            break
                if reason:
                    break
        if reason:
            process.kill()
        result = process.wait(timeout=max(.1, deadline - time.monotonic()) if not reason else 5)
    except BaseException:
        process.kill()
        process.wait(timeout=5)
        raise
    finally:
        process.stdout.close()
    return result, bytes(output[:limit]), reason


class Ledger:
    """Small sanitized state machine; exec/health noise never accumulates."""
    def __init__(self, project, role, session):
        self.project, self.role, self.session = project, role, session
        self.containers, self.events, self.errors = {}, [], []
        self.allowed_stops = set()
        self.ready = self.drained = False
        self.condition = threading.Condition()
        self.closing = self.reader_done = False
        self.process = self.thread = None

    def fail(self, code):
        if code not in self.errors and len(self.errors) < 32:
            self.errors.append(code)
        self.condition.notify_all()

    def accept(self, event):
        """Caller holds condition. Foreign events cannot provide coverage."""
        if not isinstance(event, dict) or event.get('Type') != 'container':
            self.fail('invalid-event-schema')
            return
        actor = event.get('Actor')
        if not isinstance(actor, dict) or not isinstance(actor.get('Attributes'), dict):
            self.fail('invalid-event-schema')
            return
        attributes = actor['Attributes']
        if attributes.get(PROJECT_LABEL) != self.project:
            return
        action, identifier = event.get('Action'), actor.get('ID')
        if not isinstance(action, str) or not isinstance(identifier, str) or not ID.fullmatch(identifier):
            self.fail('invalid-event-schema')
            return
        if action not in ('create', 'start', 'die', 'stop', 'destroy', 'oom', 'kill', 'restart'):
            return
        probe = attributes.get(SESSION_LABEL) == self.session
        service = 'probe' if probe else attributes.get('com.docker.compose.service')
        if not isinstance(service, str) or service not in SERVICES and not probe:
            self.fail('unexpected-project-service')
            return
        if len(self.events) >= MAX_EVENTS or len(self.containers) >= 256 and identifier not in self.containers:
            self.fail('event-limit')
            return
        timestamp = event.get('timeNano')
        if type(timestamp) is not int or not 0 < timestamp < 2 ** 63:
            self.fail('invalid-event-timestamp')
            return
        record = {'action': action, 'container': identifier, 'service': service, 'timeNano': timestamp}
        self.events.append(record)
        state = self.containers.get(identifier)
        if action == 'create':
            if state is not None:
                self.fail('duplicate-create')
                return
            state = {'service': service, 'probe': probe, 'running': False, 'destroyed': False,
                     'starts': 0, 'deaths': 0, 'stopped': False}
            self.containers[identifier] = state
        elif state is None or state['destroyed'] or state['service'] != service:
            self.fail('missing-create-or-invalid-transition')
            return
        elif action == 'start':
            if probe:
                self.fail('probe-started')
            if state['running'] or state['starts'] and not state['stopped']:
                self.fail('unexpected-restart')
            state.update(running=True, starts=state['starts'] + 1, stopped=False)
        elif action == 'die':
            if not state['running']:
                self.fail('death-without-start')
            code = attributes.get('exitCode')
            if not isinstance(code, str) or not re.fullmatch(r'[0-9]{1,3}', code) or int(code) > 255:
                self.fail('missing-exit-code')
            else:
                record['exitCode'] = int(code)
                if int(code) != 0:
                    self.fail('nonzero-container-exit')
            if service not in self.allowed_stops and not state['stopped']:
                self.fail('unrequested-container-exit')
            state.update(running=False, deaths=state['deaths'] + 1)
        elif action == 'stop':
            # Docker's stop and runtime-exit goroutines can publish stop either
            # before or after die. It must belong to this started lifecycle.
            if not state['starts']:
                self.fail('stop-without-start')
            if service not in self.allowed_stops:
                self.fail('unrequested-container-stop')
            state['stopped'] = True
        elif action == 'destroy':
            if (state['running'] or state['starts'] != state['deaths']
                    or not probe and state['starts'] and not state['stopped']):
                self.fail('destroy-with-incomplete-lifecycle')
            state['destroyed'] = True
        elif action == 'oom':
            self.fail('container-oom')
        elif action == 'kill':
            signal = attributes.get('signal')
            if not isinstance(signal, str) or not re.fullmatch('[0-9]{1,2}', signal):
                self.fail('invalid-kill-signal')
            else:
                record['signal'] = int(signal)
                if int(signal) == 9:
                    self.fail('container-sigkill')
        self.condition.notify_all()

    def read(self):
        try:
            while True:
                line = self.process.stdout.readline(MAX_OUTPUT + 1)
                with self.condition:
                    if not line:
                        if not self.closing:
                            self.fail('collector-premature-eof')
                        break
                    if len(line) > MAX_OUTPUT or not line.endswith(b'\n'):
                        self.fail('collector-truncated-event')
                        break
                    try:
                        self.accept(json.loads(line))
                    except (ValueError, UnicodeError, RecursionError, TypeError):
                        self.fail('collector-malformed-event')
                        break
        except OSError:
            with self.condition:
                self.fail('collector-read-error')
        finally:
            with self.condition:
                self.reader_done = True
                self.condition.notify_all()


class ContainerLifecycle:
    def __init__(self, env, projects, image, evidence, *, private_root=None, timeout=20):
        if len(projects) != 2 or projects[0] == projects[1] or any(
                not re.fullmatch('romd-(?:smoke|qualify)-[a-f0-9]{10,12}(?:-restore)?', value) for value in projects):
            raise ValueError('Lifecycle requires two unique disposable project names')
        if not IMAGE.fullmatch(image or ''):
            raise ValueError('Lifecycle probe image must be immutable')
        self.env, self.image, self.evidence = dict(env), image, Path(evidence)
        self.timeout, self.session, self.owned = timeout, secrets.token_hex(12), False
        self.ledgers = {project: Ledger(project, role, self.session)
                        for role, project in zip(('source', 'restore'), projects)}
        self.errors, self.cleanup_errors = [], []
        private_root = private_root or ROOT / '.artifacts/container-lifecycle-private'
        self.private = Path(private_root) / self.session
        self.private.mkdir(parents=True, mode=0o700)
        self.private.chmod(0o700)
        self.receipt = None

    def command(self, *args, limit=MAX_OUTPUT, timeout=None):
        try:
            code, output, reason = bounded_command(['docker', *args], self.env,
                                                   timeout=timeout or self.timeout, limit=limit)
        except (OSError, subprocess.SubprocessError):
            raise LifecycleError('docker-command-unavailable') from None
        if code or reason:
            raise LifecycleError('docker-command-' + (reason or 'failed'))
        return output.decode('utf-8')

    def resources(self, project, kind):
        arguments = {'container': ['ps', '-aq'], 'network': ['network', 'ls', '-q'],
                     'volume': ['volume', 'ls', '-q']}[kind]
        return self.command(*arguments, '--filter', f'label={PROJECT_LABEL}={project}').split()

    def start(self):
        # Claim the fresh namespaces only after ALL resource types in BOTH are empty.
        for project in self.ledgers:
            for kind in ('container', 'network', 'volume'):
                if self.resources(project, kind):
                    raise LifecycleError('disposable-project-already-exists')
        self.owned = True
        try:
            self.command('image', 'inspect', self.image, '--format', '{{json .Config.Volumes}}')
        except LifecycleError:
            self.command('pull', self.image, timeout=180)
        if json.loads(self.command('image', 'inspect', self.image, '--format', '{{json .Config.Volumes}}')):
            raise LifecycleError('probe-image-declares-volumes')
        for project, ledger in self.ledgers.items():
            # Separate streams: repeated Docker label filters use AND, not OR.
            # Replay Docker's bounded retained history for these fresh namespaces:
            # the first probe can otherwise precede the events CLI's subscription.
            # The probe acknowledgment remains mandatory before any host starts.
            ledger.process = subprocess.Popen(['docker', 'events', '--since', '1970-01-01T00:00:00Z', '--format', '{{json .}}',
                '--filter', 'type=container', '--filter', f'label={PROJECT_LABEL}={project}'],
                env=self.env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
            ledger.thread = threading.Thread(target=ledger.read, daemon=True)
            ledger.thread.start()
            self.barrier(project)
            ledger.ready = True

    def wait_event(self, ledger, identifier, action):
        deadline = time.monotonic() + self.timeout
        with ledger.condition:
            while not any(record['container'] == identifier and record['action'] == action for record in ledger.events):
                if ledger.reader_done or ledger.process is None or ledger.process.poll() is not None:
                    raise LifecycleError('collector-cannot-acknowledge-probe')
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise LifecycleError('collector-probe-timeout')
                ledger.condition.wait(min(.2, remaining))

    def probe_ids(self, project, name):
        values = self.command('ps', '-aq', '--no-trunc', '--filter', f'label={PROJECT_LABEL}={project}',
                              '--filter', f'label={SESSION_LABEL}={self.session}', '--filter', 'name=^/' + name + '$').split()
        if any(not ID.fullmatch(value) for value in values):
            raise LifecycleError('invalid-probe-id')
        return values

    def verify_labels(self, identifier, project, probe=False):
        data = json.loads(self.command('inspect', '--format', '{{json .Config.Labels}}', identifier))
        if (not isinstance(data, dict) or data.get(PROJECT_LABEL) != project
                or probe and data.get(SESSION_LABEL) != self.session
                or not probe and data.get(SESSION_LABEL) != self.session
                    and (not isinstance(data.get('com.docker.compose.service'), str)
                         or data.get('com.docker.compose.service') not in SERVICES)):
            raise LifecycleError('resource-ownership-mismatch')

    def verify_resources(self, project):
        for kind in ('container', 'network', 'volume'):
            for identifier in self.resources(project, kind):
                if kind == 'container':
                    self.verify_labels(identifier, project)
                else:
                    labels = json.loads(self.command(kind, 'inspect', '--format', '{{json .Labels}}', identifier))
                    if not isinstance(labels, dict) or labels.get(PROJECT_LABEL) != project:
                        raise LifecycleError('resource-ownership-mismatch')

    def barrier(self, project):
        ledger = self.ledgers[project]
        name = 'romd-lifecycle-' + self.session + '-' + secrets.token_hex(4)
        identifiers = []
        try:
            result = self.command('create', '--name', name, '--label', f'{PROJECT_LABEL}={project}',
                '--label', f'{SESSION_LABEL}={self.session}', '--network', 'none', '--read-only',
                '--entrypoint', '/bin/true', self.image).strip()
            if not ID.fullmatch(result):
                raise LifecycleError('invalid-probe-create-result')
            identifiers.append(result)
            self.wait_event(ledger, result, 'create')
        finally:
            # A timed-out/nonzero CLI create may still have committed on the daemon.
            # Lookup is exact project + random session + exact unique name only.
            try:
                identifiers.extend(self.probe_ids(project, name))
                for identifier in set(identifiers):
                    self.verify_labels(identifier, project, probe=True)
                    self.command('rm', '--volumes', identifier)
                    self.wait_event(ledger, identifier, 'destroy')
                if self.probe_ids(project, name):
                    raise LifecycleError('probe-cleanup-incomplete')
            except (LifecycleError, ValueError):
                self.errors.append('probe-cleanup-failed')
                raise LifecycleError('probe-cleanup-failed') from None

    def healthy(self):
        if self.errors or any(ledger.errors or ledger.reader_done or ledger.process is None or ledger.process.poll() is not None
                              for ledger in self.ledgers.values()):
            raise LifecycleError('container-lifecycle-failed')

    @contextmanager
    def stopping(self, project, services=SERVICES):
        ledger = self.ledgers[project]
        with ledger.condition:
            ledger.allowed_stops.update(services)
        try:
            yield
        finally:
            # Synchronize while the intent is still active, including nested backup.
            try:
                if ledger.process is None:
                    raise LifecycleError('stop-phase-no-collector')
                self.barrier(project)
            except (LifecycleError, OSError, ValueError):
                self.errors.append('stop-phase-drain-failed')
            with ledger.condition:
                ledger.allowed_stops.difference_update(services)

    def checkpoint(self, project):
        self.barrier(project)
        ledger = self.ledgers[project]
        with ledger.condition:
            running = {state['service'] for state in ledger.containers.values()
                       if not state['probe'] and state['running'] and not state['destroyed']}
            if running != SERVICES:
                ledger.fail('required-services-not-running')
        self.healthy()

    def diagnostics(self, project):
        """Selected state and bounded raw logs; no full inspect/environment payload."""
        for identifier in self.resources(project, 'container'):
            self.verify_labels(identifier, project)
            prefix = self.private / (self.ledgers[project].role + '-' + identifier)
            for kind, arguments in (('state', ['inspect', '--format', '{{json .State}}', identifier]),
                                    ('logs', ['logs', '--tail', '80', identifier])):
                code, output, reason = bounded_command(['docker', *arguments], self.env,
                                                        timeout=self.timeout, limit=64 * 1024)
                path = prefix.with_suffix('.' + kind)
                descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                with os.fdopen(descriptor, 'wb') as target:
                    target.write(output)
                # Log truncation is recorded privately; it cannot exhaust memory/disk.
                metadata = prefix.with_suffix('.' + kind + '.json')
                metadata.write_text(json.dumps({'exitCode': code, 'collectionLimit': reason}) + '\n')
                metadata.chmod(0o600)

    def cleanup(self, project, stop, down):
        if not self.owned:
            return
        # The namespaces were empty when claimed. Recheck every current container's
        # project/service labels before any logs, stop, or removal operation.
        try:
            self.verify_resources(project)
        except (LifecycleError, OSError, ValueError):
            self.cleanup_errors.append(self.ledgers[project].role + '-ownership-failed')
            return
        with self.stopping(project):
            try:
                stop()
            except (OSError, RuntimeError, subprocess.SubprocessError):
                self.cleanup_errors.append(self.ledgers[project].role + '-stop-failed')
            try:
                self.diagnostics(project)
            except (OSError, RuntimeError, ValueError, subprocess.SubprocessError):
                self.cleanup_errors.append(self.ledgers[project].role + '-diagnostics-failed')
            try:
                down()
            except (OSError, RuntimeError, subprocess.SubprocessError):
                self.cleanup_errors.append(self.ledgers[project].role + '-down-failed')
            try:
                if any(self.resources(project, kind) for kind in ('container', 'network', 'volume')):
                    self.cleanup_errors.append(self.ledgers[project].role + '-resources-remain')
            except LifecycleError:
                self.cleanup_errors.append(self.ledgers[project].role + '-cleanup-unverified')

    def finish(self, require_restore=True):
        for project, ledger in self.ledgers.items():
            if ledger.ready:
                try:
                    self.barrier(project)
                    ledger.drained = True
                except (OSError, RuntimeError, ValueError):
                    self.errors.append(ledger.role + '-final-drain-failed')
            with ledger.condition:
                if not ledger.ready or not ledger.drained:
                    ledger.fail('collector-incomplete-coverage')
                if any(not state['destroyed'] for state in ledger.containers.values()):
                    ledger.fail('container-not-destroyed')
                real = [state for state in ledger.containers.values() if not state['probe']]
                if ledger.role == 'source' or require_restore or real:
                    if {state['service'] for state in real if state['starts']} != SERVICES:
                        ledger.fail('required-service-coverage-missing')
                if ledger.reader_done or ledger.process is not None and ledger.process.poll() is not None:
                    ledger.fail('collector-exited-before-final-drain')
                ledger.closing = True
            if ledger.process is not None:
                try:
                    ledger.process.terminate()
                    try:
                        ledger.process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        ledger.process.kill()
                        ledger.process.wait(timeout=5)
                    ledger.thread.join(timeout=5)
                    if ledger.thread.is_alive():
                        # BufferedReader.close waits for an in-flight read lock.
                        # Keep at most two owned pipes until this failed runner
                        # exits, rather than blocking cleanup indefinitely.
                        self.errors.append(ledger.role + '-collector-did-not-stop')
                    else:
                        ledger.process.stdout.close()
                except (OSError, RuntimeError, subprocess.SubprocessError):
                    self.errors.append(ledger.role + '-collector-stop-failed')
        projects = {ledger.role: {'ready': ledger.ready, 'drained': ledger.drained,
            'errors': ledger.errors, 'containers': len([state for state in ledger.containers.values() if not state['probe']]),
            'events': ledger.events} for ledger in self.ledgers.values()}
        complete = all(ledger.ready and ledger.drained for ledger in self.ledgers.values())
        passed = complete and not self.errors and not self.cleanup_errors and all(not ledger.errors for ledger in self.ledgers.values())
        self.receipt = {'schemaVersion': 1, 'result': 'passed' if passed else 'failed', 'complete': complete,
                        'errors': self.errors, 'cleanupErrors': self.cleanup_errors, 'projects': projects}
        self.evidence.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        self.evidence.write_text(json.dumps(self.receipt, indent=2) + '\n')
        self.evidence.chmod(0o600)
        return self.receipt

    @contextmanager
    def monitor(self, cleanups):
        """Always attempt both claimed cleanups; never replace a primary failure."""
        primary = None
        try:
            self.start()
            yield self
        except BaseException as error:
            primary = error
            raise
        finally:
            for project, stop, down in cleanups:
                try:
                    self.cleanup(project, stop, down)
                except (OSError, RuntimeError, ValueError, subprocess.SubprocessError):
                    self.cleanup_errors.append(self.ledgers[project].role + '-cleanup-exception')
            try:
                receipt = self.finish(require_restore=primary is None)
            except (OSError, RuntimeError, ValueError, subprocess.SubprocessError):
                self.errors.append('lifecycle-finalization-failed')
                if self.receipt is not None:
                    self.receipt['result'] = 'failed'
                if primary is None:
                    raise LifecycleError('Container lifecycle finalization failed') from None
                primary.add_note('Container lifecycle finalization also failed')
            else:
                if receipt['result'] != 'passed':
                    if primary is not None:
                        primary.add_note('Container lifecycle/cleanup also failed; inspect lifecycle.json')
                    else:
                        raise LifecycleError('Container lifecycle/cleanup failed; inspect lifecycle.json')
