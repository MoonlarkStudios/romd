#!/usr/bin/env python3
"""Qualify committed source hosts in disposable linked worktrees and PostgreSQL."""
import argparse
import base64
import hashlib
import html
import re
import selectors
import subprocess
from http.cookiejar import CookieJar
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import sys
import tempfile
import time
from urllib.error import HTTPError
from urllib.parse import parse_qs, quote, urlencode, urlsplit
from urllib.request import HTTPRedirectHandler, HTTPCookieProcessor, Request, build_opener
import uuid

import manage
from containers.runtime import Runtime


def require(condition, message):
    if not condition:
        raise manage.SetupError(message)


def isolated_environment(environment):
    keep = {'ROMD_CONTAINER_ENGINE', 'ROMD_COMPOSE_PROVIDER', 'MISE_DATA_DIR',
            'MISE_CACHE_DIR', 'MISE_STATE_DIR', 'MISE_CONFIG_DIR'}
    result = {}
    for key, value in environment.items():
        folded = key.lower().replace('__', ':')
        if key not in keep and (key.upper().startswith(('ROMD_', 'ROMD:', 'COMPOSE_', 'VITE_', 'IGDB_', 'GIT_', 'MISE_'))
                                or folded.startswith(('romd:', 'connectionstrings:', 'providers:', 'aspnetcore:', 'datsubscriptions:'))
                                or key.upper().startswith('ASPNETCORE_') or folded == 'dotnet_environment'):
            continue
        result[key] = value
    return result


def unused_port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, file, code, message, headers, new_url):
        return None


def request(url, *, opener=None, form=None, headers=None):
    opener = opener or build_opener(NoRedirect)
    data = urlencode(form).encode() if form is not None else None
    try:
        response = opener.open(Request(url, data=data, headers=headers or {}), timeout=10)
    except HTTPError as error:
        response = error
    with response:
        return response.status, response.headers, response.read()


def authorize(origin, web, client, email, password):
    """Exercise PKCE without persisting cookies, credentials, codes or tokens."""
    browser = build_opener(NoRedirect, HTTPCookieProcessor(CookieJar()))
    verifier = base64.urlsafe_b64encode(os.urandom(32)).decode().rstrip('=')
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).decode().rstrip('=')
    callback = web + '/auth/callback'
    values = {'client_id': client, 'response_type': 'code', 'redirect_uri': callback,
              'scope': 'openid profile email roles', 'code_challenge': challenge,
              'code_challenge_method': 'S256', 'state': uuid.uuid4().hex}
    path = '/connect/authorize?' + urlencode(values)
    status, _, _ = request(origin + path, opener=browser)
    require(status == 302, 'Authorization did not reach the login flow.')
    status, _, body = request(origin + '/connect/login', opener=browser)
    require(status == 200, 'Login form did not load.')
    token = re.search(r'name="__RequestVerificationToken" value="([^"]+)"', body.decode())
    require(token is not None, 'Login form did not include an antiforgery token.')
    status, headers, _ = request(origin + '/connect/login', opener=browser,
                                 form={'returnUrl': path, 'login': email, 'password': password,
                                       '__RequestVerificationToken': html.unescape(token[1])})
    require(status == 302 and headers.get('Location', '').startswith('/connect/authorize'),
            'Development account login failed.')
    status, headers, _ = request(origin + headers['Location'], opener=browser)
    target = urlsplit(headers.get('Location', ''))
    expected = urlsplit(callback)
    query = parse_qs(target.query)
    require(status == 302 and (target.scheme, target.netloc, target.path) ==
            (expected.scheme, expected.netloc, expected.path) and query.get('state') == [values['state']],
            'Authorization callback did not match this checkout.')
    require('code' in query, 'Authorization callback did not include a code.')
    status, _, body = request(origin + '/connect/token', opener=browser, form={
        'grant_type': 'authorization_code', 'client_id': client, 'redirect_uri': callback,
        'code': query['code'][0], 'code_verifier': verifier})
    require(status == 200, 'PKCE token exchange failed.')
    token = json.loads(body).get('access_token')
    require(bool(token), 'PKCE token response had no access token.')
    return token


WEB_PROCESSES = ('web-dependencies', 'web-admin', 'web-consumer', 'web-player')
WEB_PROBE = Path('web/packages/romd-foundation/src/romdDevelopmentSmokeProbe.ts')


def web_probe_output(root):
    return root / WEB_PROBE.parent.parent / 'dist' / (WEB_PROBE.stem + '.js')


def wait_for_web_probe(stack, marker, timeout):
    """Wait for actual compiler output and every source-mode Vite server to serve its new value."""
    emitted = web_probe_output(stack.root)
    target = '/@fs' + quote(emitted.as_posix(), safe='/')
    expected = marker.encode()
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        stack.processes.assert_running()
        try:
            if expected in emitted.read_bytes():
                urls = stack.status()['urls']
                responses = [request(urls[name] + target, headers={'Cache-Control': 'no-cache'})
                             for name in ('admin_web', 'consumer_web', 'player')]
                if all(status == 200 and expected in body for status, _, body in responses):
                    return
        except OSError:
            pass  # File publication/network readiness can race this bounded poll.
        time.sleep(0.1)
    raise manage.SetupError('Timed out waiting for shared web dependency compilation and Vite update; inspect web-dependencies.log.')


class Stack:
    def __init__(self, root, environment, evidence, timeout):
        self.root, self.environment, self.timeout = root, environment, timeout
        self.processes = manage.Processes(evidence)
        self.child = None
        self.groups = set()

    def start(self):
        demo = (self.root / '.demo/manifest.json').exists()
        command = [sys.executable, str(self.root / 'scripts/dev' / ('demo.py' if demo else 'manage.py')),
                   '--serve' if demo else 'source']
        self.child = self.processes.start('controller', command,
                                           cwd=self.root, env=self.environment)
        self.groups.add(self.child.pid)

    def status(self):
        record = manage.session_status(self.root)
        if record.get('pid') == (self.child.pid if self.child else None):
            self.groups.update(record.get('pids', {}).values())
        return record

    def wait(self, state, previous=None):
        deadline = time.monotonic() + self.timeout
        progress = time.monotonic() + 30
        while time.monotonic() < deadline:
            record = self.status()
            self.processes.assert_running()
            if record['active'] and record['state'] == state and (
                    previous is None or record.get('backendDirectory') != previous):
                require(record.get('pid') == self.child.pid, 'Unexpected source controller owner.')
                return record
            if time.monotonic() >= progress:
                print(f'Waiting for {self.root.name}: {state} (currently {record["state"]}).', flush=True)
                progress = time.monotonic() + 30
            time.sleep(0.25)
        raise manage.SetupError(f'Timed out waiting for {self.root.name}: {state}; inspect retained diagnostics.')

    def endpoints(self):
        record = self.status()
        require(record['active'] and record['state'] == 'ready', 'A source stack lost readiness.')
        self.processes.assert_running()
        for name, origin in record['urls'].items():
            path = '/health/ready' if name.endswith('_api') else '/'
            require(request(origin + path)[0] == 200, f'{self.root.name} {name} endpoint failed.')
        return record

    def stop(self):
        if self.child is None:
            return
        if self.child.poll() is None:
            self.child.send_signal(signal.SIGINT)
            deadline = time.monotonic() + 60
            while self.child.poll() is None and time.monotonic() < deadline:
                time.sleep(0.1)
        record = self.status()
        require(self.child.poll() is not None and not record['active'] and
                record['state'] in ('stopped', 'failed') and not record.get('pids') and
                not any(manage.group_exists(pid) for pid in self.groups),
                'Cannot prove owned shutdown; preserving smoke worktrees and PostgreSQL for inspection.')
        for url in record.get('urls', {}).values():
            with socket.socket() as client:
                require(client.connect_ex(('127.0.0.1', urlsplit(url).port)) != 0,
                        'An owned development port is still listening.')


def bounded_capture(command, environment, *, limit=65536, timeout=10, include_stderr=False):
    """Read a hard byte/time bound from one owned diagnostic subprocess."""
    output = bytearray()
    deadline = time.monotonic() + timeout
    with subprocess.Popen(command, env=environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT if include_stderr else subprocess.DEVNULL) as child:
        with selectors.DefaultSelector() as ready:
            ready.register(child.stdout, selectors.EVENT_READ)
            truncated, timed_out = False, False
            while ready.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    timed_out = True
                    break
                if not ready.select(remaining):
                    timed_out = True
                    break
                chunk = os.read(child.stdout.fileno(), min(4096, limit + 1 - len(output)))
                if not chunk:
                    break
                output.extend(chunk)
                if len(output) > limit:
                    truncated = True
                    break
            if child.poll() is None and (truncated or timed_out):
                child.kill()
            try:
                child.wait(timeout=max(0.1, deadline - time.monotonic()))
            except subprocess.TimeoutExpired:
                timed_out = True
                child.kill()
                child.wait(timeout=5)
    return bytes(output[:limit]), {'exitCode': child.returncode, 'truncated': truncated, 'timedOut': timed_out}


def postgres_log_events(raw):
    """Keep diagnostic lifecycle events, never SQL, account data, paths or arbitrary message text."""
    events = []
    omitted = 0
    patterns = (
        r'database system is ready to accept connections',
        r'database system was shut down at',
        r'database system was interrupted',
        r'database system was not properly shut down; automatic recovery in progress',
        r'server process \(PID [0-9]+\) was terminated by signal [0-9]+',
        r'terminating any other active server processes',
        r'all server processes terminated; reinitializing',
        r'redo starts at [0-9A-F/]+', r'redo done at [0-9A-F/]+',
        r'invalid record length at [0-9A-F/]+: wanted [0-9]+, got [0-9]+',
        r'checkpoint (?:starting|complete)',
        r'database system is shut down',
        r'received (?:fast|smart|immediate) shutdown request',
        r'aborting any active transactions',
    )
    for line in raw.decode('utf-8', errors='replace').splitlines():
        # Drop complete statement/detail lines even if their SQL contains an event-like string.
        if re.search(r'\b(?:STATEMENT|DETAIL|HINT|CONTEXT|QUERY):', line):
            omitted += 1
            continue
        message = re.search(r'\b(?:LOG|WARNING|ERROR|FATAL|PANIC):\s*(.*)', line)
        if message:
            for pattern in patterns:
                event = re.match(pattern, message[1])
                if event:
                    # Preserve the engine's timestamp when present; exclude every other prefix.
                    timestamp = re.match(r'\d{4}-\d{2}-\d{2}T[0-9:.]+Z\s+', line)
                    events.append((timestamp[0] if timestamp else '') + event[0])
                    break
            else:
                severity = re.match(r'(?:ERROR|FATAL|PANIC):', message[0])
                if severity:
                    events.append('PostgreSQL reported ' + severity[0][:-1] + ' (message omitted)')
                else:
                    omitted += 1
        else:
            omitted += 1
    return '\n'.join(events) + ('\n' if events else ''), omitted


def private_text(path, value):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, 'w') as target:
        os.fchmod(target.fileno(), 0o600)
        target.write(value)


class Qualification:
    def __init__(self, root, evidence, parallel=False, timeout=900, demo=False, demo_workspace=None):
        self.root, self.evidence, self.parallel, self.timeout = root, evidence, parallel, timeout
        self.demo, self.demo_workspace = demo, demo_workspace
        environment = isolated_environment(os.environ)
        self.project = 'romd-source-smoke-' + uuid.uuid4().hex[:12]
        environment.update(COMPOSE_PROJECT_NAME=self.project, ROMD_DB_PORT=str(unused_port()))
        self.runtime = Runtime(environment)
        self.environment = self.runtime.child_environment()
        self.compose = self.runtime.compose_command('-p', self.project, '-f', str(root / 'compose.db.yaml'))
        info = self.runtime.info()
        self.workspace = Path(tempfile.mkdtemp(prefix='romd-source-smoke-')).resolve()
        self.worktrees, self.stacks = [], []
        self.database_started = False
        self.results = {'schemaVersion': 1, 'project': self.project, 'workspace': str(self.workspace),
                        'parallel': parallel, 'platform': sys.platform, 'runtime': info,
                        'startedAt': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), 'stages': []}

    def command(self, name, command, *, cwd=None, environment=None):
        processes = manage.Processes(self.evidence / 'commands')
        child = processes.start(name, command, cwd=cwd or self.root, env=environment or self.environment)
        deadline, progress = time.monotonic() + self.timeout, time.monotonic() + 30
        try:
            while child.poll() is None and time.monotonic() < deadline:
                if time.monotonic() >= progress:
                    print(f'Waiting for {name}; diagnostics: {self.evidence}.', flush=True)
                    progress = time.monotonic() + 30
                time.sleep(0.25)
            require(child.poll() == 0, f'{name} failed or timed out; inspect retained diagnostics.')
        finally:
            processes.stop()

    def git(self, *arguments):
        return manage.call(['git', '-C', str(self.root), *arguments], env=self.environment, capture=True)

    def sql(self, database, query):
        return manage.call(self.compose + ['exec', '-T', 'postgres', 'psql', '-U', 'romd', '-d', database,
                                          '-v', 'ON_ERROR_STOP=1', '-qtAc', query],
                           env=self.environment, capture=True).strip()

    def passed(self, name):
        self.results['stages'].append(name)
        print('PASS ' + name, flush=True)

    def prepare(self):
        require(not self.git('status', '--porcelain', '--untracked-files=normal').strip(),
                'Source smoke tests committed HEAD: commit intended changes first; ignored local configuration is not copied.')
        self.results['revision'] = self.git('rev-parse', 'HEAD').strip()
        manage.check_tools(self.root)
        ports = set()
        for index in range(2 if self.parallel else 1):
            checkout = self.workspace / f'checkout-{index + 1}'
            self.git('worktree', 'add', '--detach', str(checkout), self.results['revision'])
            self.worktrees.append(checkout)
            selected = manage.port_settings('source', self.environment, checkout)
            require(not ports.intersection(selected.values()), 'Derived smoke port ranges collided; rerun with a fresh workspace.')
            ports.update(selected.values())
            # The clean linked worktree has no host-local appsettings, .env or mise overrides.
            require(manage.source_data_directory(checkout, self.environment) == str(checkout / '.data/romd'),
                    'Smoke source data must belong to its disposable checkout.')
            self.command(f'bootstrap-{index + 1}', [sys.executable, str(checkout / 'scripts/dev/manage.py'), 'bootstrap'], cwd=checkout)
            if index == 0 and self.demo_workspace:
                # Use declared immutable inputs only; source collection paths,
                # another checkout's receipts and TUF caches are never copied.
                import demo_workspace
                demo_workspace.copy_workspace(self.demo_workspace.resolve(), checkout)
            environment = self.environment | {'Romd__DefaultAdminEmail': f'smoke-{index + 1}@localhost',
                                               'Romd__DefaultAdminPassword': 'SmokeQualification123!'}
            self.stacks.append(Stack(checkout, environment, self.evidence / f'stack-{index + 1}', self.timeout))
        self.database_started = True  # Also clean up a partially failed Compose up.
        self.command('postgres-up', self.compose + ['up', '-d', '--wait', '--wait-timeout', '120'])

    def exercise_web_dependencies(self, initial):
        probes = []
        for index, stack in enumerate(self.stacks):
            require('web-dependencies' in initial[index]['pids'], 'The shared dependency watcher is not owned by this checkout.')
            source = stack.root / WEB_PROBE
            require(not source.exists() and not web_probe_output(stack.root).exists(), 'Web smoke probe already exists in committed source/output.')
            marker = 'romd-web-smoke-' + uuid.uuid4().hex
            source.write_text('export const romdDevelopmentSmokeProbe = ' + json.dumps(marker) + ';\n')
            wait_for_web_probe(stack, marker, min(self.timeout, 60))
            probes.append((source, marker))
        changed = 'romd-web-smoke-' + uuid.uuid4().hex
        probes[0][0].write_text('export const romdDevelopmentSmokeProbe = ' + json.dumps(changed) + ';\n')
        wait_for_web_probe(self.stacks[0], changed, min(self.timeout, 60))
        for stack, (_, marker) in zip(self.stacks[1:], probes[1:]):
            wait_for_web_probe(stack, marker, min(self.timeout, 60))
        for index, stack in enumerate(self.stacks):
            current = stack.status()
            require(current['pids'] == initial[index]['pids'] and current['backendDirectory'] == initial[index]['backendDirectory'],
                    'A shared web dependency edit restarted an owned process or another checkout.')
        self.passed('shared-library edit compiles and all Vite servers serve the update without restarting processes or changing another checkout')

    def exercise(self):
        for stack in self.stacks:
            stack.start()
        initial = [stack.wait('ready') for stack in self.stacks]
        self.results['sessions'] = initial
        tokens, databases = [], []
        for index, stack in enumerate(self.stacks):
            record = stack.endpoints()
            database = manage.call(['bash', '-c', 'source "$1"; romd_db_name', 'smoke', str(stack.root / 'scripts/db/lib.sh')],
                                   cwd=stack.root, env=stack.environment, capture=True)
            databases.append(database)
            marker = uuid.uuid4().hex
            self.sql(database, f"CREATE TABLE public.smoke_owner(value text); INSERT INTO public.smoke_owner VALUES ('{marker}')")
            require(self.sql(database, 'SELECT value FROM public.smoke_owner') == marker, 'Checkout database ownership failed.')
            (stack.root / '.data/romd/smoke-sentinel').write_text(marker)
            for surface in ('admin', 'consumer'):
                origin, web = record['urls'][surface + '_api'], record['urls'][surface + '_web']
                token = authorize(origin, web, 'romd-' + surface + '-spa',
                                  stack.environment['Romd__DefaultAdminEmail'], stack.environment['Romd__DefaultAdminPassword'])
                account_path = '/api/me/sessions' if surface == 'admin' else '/api/me/settings'
                status = request(origin + account_path, headers={'Authorization': 'Bearer ' + token})[0]
                require(status == 200, f'{surface} token did not authenticate on its own host (HTTP {status}).')
                require(request(origin + '/health/ready', headers={'Origin': web})[1].get('Access-Control-Allow-Origin') == web,
                        'CORS did not match the checkout browser origin.')
                if surface == 'admin':
                    tokens.append(token)
        require(len(set(databases)) == len(self.stacks), 'Checkout databases were shared.')
        self.passed('worker-first startup, five endpoints per stack, distinct databases/data and PKCE/CORS origins')
        self.exercise_web_dependencies(initial)
        if self.demo:
            first = self.stacks[0]
            environment = first.environment | {
                'ROMD_DEMO_ADMIN_EMAIL': first.environment['Romd__DefaultAdminEmail'],
                'ROMD_DEMO_ADMIN_PASSWORD': first.environment['Romd__DefaultAdminPassword']}
            selections = [('synthetic', ['--synthetic'])]
            if self.demo_workspace:
                selections.append(('prepared', []))
            for name, arguments in selections:
                command = [sys.executable, str(first.root / 'scripts/dev/demo.py'), *arguments]
                self.command('demo-' + name, command, cwd=first.root, environment=environment)
                paths = list((first.root / '.demo/state').glob('*/state.json'))
                before = {str(p): json.loads(p.read_text()) for p in paths}
                self.command('demo-' + name + '-repeat', command, cwd=first.root, environment=environment)
                after = {str(p): json.loads(p.read_text())
                         for p in (first.root / '.demo/state').glob('*/state.json')}
                require(before.keys() == after.keys() and all(before[p]['jobs'] == after[p]['jobs'] and
                            before[p]['libraryId'] == after[p]['libraryId'] and
                            before[p]['titles'] == after[p]['titles'] for p in before),
                        'Repeating demo setup duplicated jobs, libraries or title selections.')
                self.passed(name + ' demo import, materialization, consumer login/browse and repeat without duplicate jobs')
            if self.demo_workspace:
                command = [sys.executable, str(first.root / 'scripts/dev/demo.py'), '--subscriptions']
                for attempt in ('first', 'repeat'):
                    self.command('demo-subscriptions-' + attempt, command, cwd=first.root, environment=environment)
                reviews = list((first.root / '.demo/state').glob('*/subscription-review.json'))
                require(len(reviews) == 1 and len(json.loads(reviews[0].read_text())) == 6,
                        'Prepared catalog subscription checks did not retain all six reviews.')
                self.passed('production-pinned native reader, six signed subscription checks/previews and repeat without auto-approval')
        if self.parallel:
            for index, stack in enumerate(self.stacks):
                foreign = self.stacks[1 - index].status()['urls']
                own = stack.status()['urls']
                require(request(own['admin_api'] + '/api/me/sessions',
                                headers={'Authorization': 'Bearer ' + tokens[1 - index]})[0] == 401,
                        'A token crossed checkout identity boundaries.')
                require(request(own['admin_api'] + '/health/ready', headers={'Origin': foreign['admin_web']})[1]
                        .get('Access-Control-Allow-Origin') is None, 'CORS allowed another checkout origin.')
                for surface in ('admin', 'consumer'):
                    query = urlencode({'client_id': 'romd-' + surface + '-spa', 'response_type': 'code',
                                       'redirect_uri': foreign[surface + '_web'] + '/auth/callback',
                                       'scope': 'openid', 'code_challenge': 'A' * 43, 'code_challenge_method': 'S256'})
                    require(request(own[surface + '_api'] + '/connect/authorize?' + query)[0] == 400,
                            'An authorization client accepted another checkout callback.')
            self.passed('two live stacks reject each other\'s tokens and browser origins')
        first = self.stacks[0]
        probe = first.root / 'src/Romd.Worker.Host/RomdDevelopmentSmokeProbe.cs'
        require(not probe.exists(), 'Smoke probe path already exists in committed source.')
        probe.write_text('namespace Romd.Worker.Host; internal static class RomdDevelopmentSmokeProbe { }\n')
        reloaded = first.wait('ready', initial[0]['backendDirectory'])
        first.endpoints()
        require(reloaded['pids']['worker'] != initial[0]['pids']['worker'] and all(
            reloaded['pids'][key] == initial[0]['pids'][key] for key in WEB_PROCESSES),
            'Backend reload did not preserve Vite ownership.')
        probe.write_text('#error ROMD_SOURCE_SMOKE_EXPECTED_FAILURE\n')
        failed = first.wait('reload-failed')
        require(not {'worker', 'admin', 'consumer'}.intersection(failed['pids']), 'Failed build left backend hosts running.')
        require('ROMD_SOURCE_SMOKE_EXPECTED_FAILURE' in (Path(failed['backendDirectory']) / 'build-worker.log').read_text(),
                'Failed-build compiler diagnostics were not discoverable.')
        require('error CS1029' in failed.get('error', '') and failed.get('failedComponent') == 'build-worker',
                'Failed-build status did not report the compiler error.')
        probe.unlink()
        recovered = first.wait('ready', failed['backendDirectory'])
        first.endpoints()
        self.passed('edit/reload, compiler-error diagnostics and recovery without restarting Vite')
        # An owned host crash after readiness is recoverable without restarting Vite.
        os.killpg(recovered['pids']['admin'], signal.SIGKILL)
        crashed = first.wait('backend-exited')
        require(crashed.get('failedComponent') == 'admin' and
                not {'worker', 'admin', 'consumer'}.intersection(crashed['pids']) and
                all(crashed['pids'].get(key) == recovered['pids'][key] for key in WEB_PROCESSES),
                'A crashed host did not stop its siblings while preserving Vite.')
        manage.request_restart(first.root)
        restarted = first.wait('ready', crashed['backendDirectory'])
        first.endpoints()
        require(all(restarted['pids'][key] == recovered['pids'][key] for key in WEB_PROCESSES),
                'Crash recovery restarted Vite.')
        self.passed('host crash after readiness stops siblings, keeps Vite and restarts worker-first on request')
        for index, stack in enumerate(self.stacks[1:], 1):
            record = stack.endpoints()
            require(record['pids'] == initial[index]['pids'] and record['backendDirectory'] == initial[index]['backendDirectory'],
                    'Editing one checkout restarted another checkout.')
        first.stop()
        for stack in self.stacks[1:]:
            stack.endpoints()
            stack.stop()
        for database, stack in zip(databases, self.stacks):
            require(self.sql(database, 'SELECT value FROM public.smoke_owner') ==
                    (stack.root / '.data/romd/smoke-sentinel').read_text(), 'Shutdown lost database/content state.')
        self.passed('independent owned shutdown, closed ports and preserved database/content state')

    def capture_postgres(self):
        """Best-effort private evidence from this smoke's database before destructive cleanup."""
        record = {'status': 'failed', 'errors': []}
        self.results['postgresDiagnostics'] = record
        try:
            destination = self.evidence / 'postgres'
            destination.mkdir(parents=True, exist_ok=True, mode=0o700)
            destination.chmod(0o700)
            raw, info = bounded_capture(self.compose + ['ps', '--all', '--quiet', 'postgres'], self.environment, limit=256)
            record['inventory'] = info
            container = raw.decode('ascii').strip()
            if info['exitCode'] or info['truncated'] or info['timedOut'] or not re.fullmatch(r'[a-f0-9]{12,64}', container):
                record['errors'].append('Owned PostgreSQL container could not be identified.')
                return
            health = 'Healthcheck' if self.runtime.engine == 'podman' else 'Health'
            template = ('{"status":{{json .State.Status}},"running":{{json .State.Running}},'
                        '"exitCode":{{json .State.ExitCode}},"oomKilled":{{json .State.OOMKilled}},'
                        '"startedAt":{{json .State.StartedAt}},"finishedAt":{{json .State.FinishedAt}},'
                        '"health":{{if .State.' + health + '}}{{json .State.' + health + '.Status}}{{else}}null{{end}}}')
            try:
                raw, info = bounded_capture(self.runtime.engine_command('inspect', '--format', template, container),
                                            self.environment, limit=4096)
                record['state'] = info
                if info['exitCode'] or info['truncated'] or info['timedOut']:
                    record['errors'].append('Selected PostgreSQL state could not be captured.')
                else:
                    state = json.loads(raw)
                    require(isinstance(state, dict), 'PostgreSQL state must be an object.')
                    selected = {}
                    for key in ('status', 'health'):
                        value = state.get(key)
                        require(value is None or (isinstance(value, str) and re.fullmatch(r'[a-zA-Z-]{1,32}', value)), 'Invalid state enum.')
                        selected[key] = value
                    for key in ('running', 'oomKilled'):
                        require(isinstance(state.get(key), bool), 'Invalid state flag.')
                        selected[key] = state[key]
                    require(type(state.get('exitCode')) is int and 0 <= state['exitCode'] <= 255, 'Invalid exit code.')
                    selected['exitCode'] = state['exitCode']
                    for key in ('startedAt', 'finishedAt'):
                        value = state.get(key)
                        require(isinstance(value, str) and re.fullmatch(r'[0-9T:.Z+ -]{1,64}', value), 'Invalid state timestamp.')
                        selected[key] = value
                    private_text(destination / 'state.json', json.dumps(selected, indent=2) + '\n')
            except (OSError, ValueError, RuntimeError, TypeError):
                record['errors'].append('Selected PostgreSQL state could not be captured.')
            raw, info = bounded_capture(self.runtime.engine_command('logs', '--timestamps', '--tail', '200', container),
                                        self.environment, include_stderr=True)
            record['logs'] = info
            if info['exitCode'] and not raw:
                record['errors'].append('PostgreSQL logs could not be captured.')
            else:
                if info['truncated'] or info['timedOut'] or info['exitCode']:
                    record['errors'].append('PostgreSQL lifecycle log capture is partial.')
                events, omitted = postgres_log_events(raw)
                record['omittedLogLines'] = omitted
                private_text(destination / 'events.log', events)
            record['status'] = 'partial' if record['errors'] else 'captured'
        except (OSError, ValueError, RuntimeError, TypeError, UnicodeError):
            # Raw engine errors and SQL do not belong in retained diagnostics.
            record['errors'].append('PostgreSQL diagnostic capture failed; test and cleanup results are unchanged.')

    def cleanup(self):
        errors = []
        with manage.uninterrupted_cleanup():
            for stack in reversed(self.stacks):
                try:
                    stack.stop()
                except (OSError, manage.SetupError) as error:
                    errors.append(str(error))
            if self.database_started:
                self.capture_postgres()
            # Preserve fixtures if shutdown cannot be established; never remove a live checkout.
            if not errors:
                if self.database_started:
                    try:
                        self.command('postgres-down', self.compose + ['down', '--volumes', '--timeout', '30'])
                    except manage.SetupError as error:
                        errors.append(str(error))
                for checkout in reversed(self.worktrees):
                    try:
                        logs = checkout / '.data/dev'
                        if logs.exists():
                            shutil.copytree(logs, self.evidence / checkout.name, dirs_exist_ok=True)
                        demo_logs = checkout / '.demo/state'
                        if demo_logs.exists():
                            shutil.copytree(demo_logs, self.evidence / (checkout.name + '-demo'), dirs_exist_ok=True)
                        self.git('worktree', 'remove', '--force', str(checkout))
                    except (OSError, manage.SetupError) as error:
                        errors.append(str(error))
                if not errors:
                    self.workspace.rmdir()
        self.results['cleanupErrors'] = errors
        require(not errors, 'Smoke cleanup incomplete; inspect the retained summary and owned workspace.')

    def run(self):
        self.results['status'] = 'failed'
        try:
            self.prepare()
            self.exercise()
            self.results['status'] = 'passed'
        except BaseException as error:
            self.results['failure'] = str(error)
            raise
        finally:
            try:
                self.cleanup()
            except BaseException:
                self.results['status'] = 'failed'
                raise
            finally:
                self.results['finishedAt'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
                private_text(self.evidence / 'summary.json', json.dumps(self.results, indent=2) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--parallel', action='store_true', help='Qualify two complete worktrees concurrently')
    parser.add_argument('--demo', action='store_true', help='Seed synthetic demo data twice and verify consumer access')
    parser.add_argument('--demo-workspace', type=Path, help='With --demo, copy and qualify a prepared .demo manifest twice')
    parser.add_argument('--timeout', type=int, default=900, help='Maximum seconds per setup/lifecycle phase')
    parser.add_argument('--evidence-parent', type=Path, default=manage.ROOT / '.artifacts/dev-smoke')
    args = parser.parse_args()
    require(args.timeout > 0, '--timeout must be positive.')
    require(not args.demo_workspace or args.demo, '--demo-workspace requires --demo.')
    args.evidence_parent.mkdir(parents=True, exist_ok=True)
    evidence = Path(tempfile.mkdtemp(prefix='run-', dir=args.evidence_parent)).resolve()
    print(f'Private source-smoke evidence: {evidence}', flush=True)
    def interrupted(_number, _frame):
        raise KeyboardInterrupt
    previous = {number: signal.signal(number, interrupted) for number in manage.SHUTDOWN_SIGNALS}
    try:
        Qualification(manage.ROOT, evidence, args.parallel, args.timeout, args.demo, args.demo_workspace).run()
    finally:
        for number, handler in previous.items():
            signal.signal(number, handler)


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(130)
    except (manage.SetupError, OSError, ValueError) as error:
        sys.exit(f'Source smoke failed: {error}')
