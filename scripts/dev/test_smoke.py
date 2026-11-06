import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

import smoke


class SmokeTests(unittest.TestCase):
    def test_source_authentication_and_cross_checkout_rejection_use_canonical_self_routes(self):
        class CheckedAuthentication(Exception):
            pass
        with tempfile.TemporaryDirectory() as directory:
            run = object.__new__(smoke.Qualification)
            run.results, run.parallel, run.demo, run.stacks = {}, True, False, []
            run.exercise_web_dependencies = Mock()
            markers, requests = {}, []
            for index in range(2):
                root = Path(directory) / str(index)
                (root / '.data/romd').mkdir(parents=True)
                urls = {surface + '_' + kind: f'http://localhost:{31000 + index * 10 + offset * 2 + (kind == "web")}'
                        for offset, surface in enumerate(('admin', 'consumer')) for kind in ('api', 'web')}
                stack = Mock(root=root, environment={'Romd__DefaultAdminEmail': 'fixture', 'Romd__DefaultAdminPassword': 'fixture'})
                stack.endpoints.return_value = stack.wait.return_value = stack.status.return_value = {'urls': urls}
                run.stacks.append(stack)
            def sql(database, statement):
                if statement.startswith('CREATE TABLE'):
                    markers[database] = statement.split("VALUES ('", 1)[1].split("')", 1)[0]
                return markers[database]
            run.sql = Mock(side_effect=sql)
            def passed(message):
                if message.startswith('two live stacks'):
                    raise CheckedAuthentication
            run.passed = Mock(side_effect=passed)
            def authorize(origin, *_args):
                return 'token:' + origin
            def request(url, headers=None):
                requests.append((url, headers))
                resource = smoke.urlsplit(url)
                origin = resource.scheme + '://' + resource.netloc
                if resource.path == '/health/ready':
                    own = next(stack.status()['urls'] for stack in run.stacks if stack.status()['urls']['admin_api'] == origin
                               or stack.status()['urls']['consumer_api'] == origin)
                    allowed = own['admin_web'] if origin == own['admin_api'] else own['consumer_web']
                    return 200, {'Access-Control-Allow-Origin': allowed} if headers['Origin'] == allowed else {}, b''
                if resource.path == '/connect/authorize':
                    return 400, {}, b''
                self.assertIn(resource.path, ('/api/me/sessions', '/api/me/settings'))
                token = headers['Authorization'].removeprefix('Bearer ')
                return (200 if token == 'token:' + origin else 401), {}, b''
            with patch.object(smoke.manage, 'call', side_effect=lambda *_args, **options: str(options['cwd'])), \
                    patch.object(smoke, 'authorize', side_effect=authorize) as authorize_call, \
                    patch.object(smoke, 'request', side_effect=request), self.assertRaises(CheckedAuthentication):
                run.exercise()
            self.assertEqual(4, authorize_call.call_count)
            paths = [smoke.urlsplit(url).path for url, _ in requests]
            self.assertEqual(4, paths.count('/api/me/sessions'))
            self.assertEqual(2, paths.count('/api/me/settings'))
            for stack in run.stacks:
                stack.start.assert_called_once()

    def test_fixture_environment_cannot_inherit_application_data_credentials_or_project(self):
        environment = {'PATH': '/tools', 'HOME': '/home/test', 'DOCKER_CONTEXT': 'desktop',
                       'DOCKER_TLS_VERIFY': '1', 'ROMD_CONTAINER_ENGINE': 'docker',
                       'ROMD_COMPOSE_PROVIDER': '/tools/compose', 'MISE_DATA_DIR': '/tools/mise',
                       'Romd__DataDirectory': '/private/library', 'ROMD:DataDirectory': '/other/library',
                       'CONNECTIONSTRINGS__ROMD': 'private-credential', 'Providers__Igdb__ClientSecret': 'private',
                       'VITE_ROMD_ADMIN_API_ORIGIN': 'https://private', 'IGDB_CLIENT_SECRET': 'private',
                       'COMPOSE_PROJECT_NAME': 'romd-db', 'COMPOSE_FILE': '/private/compose.yaml',
                       'ROMD_DB_PORT': '15432', 'ROMD_DEV_PORT_BASE': '30000',
                       'GIT_DIR': '/other/.git', 'MISE_PROJECT_ROOT': '/other', 'DOTNET_ENVIRONMENT': 'Production'}
        selected = smoke.isolated_environment(environment)
        self.assertEqual(selected, {key: environment[key] for key in (
            'PATH', 'HOME', 'DOCKER_CONTEXT', 'DOCKER_TLS_VERIFY', 'ROMD_CONTAINER_ENGINE',
            'ROMD_COMPOSE_PROVIDER', 'MISE_DATA_DIR')})
        self.assertEqual(environment['Romd__DataDirectory'], '/private/library')

    def repository(self, root):
        root.mkdir()
        def git(*args):
            return subprocess.check_output(['git', '-C', str(root), *args], text=True, stderr=subprocess.DEVNULL)
        git('init', '-q')
        (root / '.gitignore').write_text('.env\n.mise.local.toml\n.data/\n')
        (root / 'tracked.txt').write_text('source')
        git('add', '.')
        git('-c', 'user.name=Smoke Test', '-c', 'user.email=test@example.invalid',
            '-c', 'commit.gpgsign=false', 'commit', '-qm', 'fixture')
        (root / '.env').write_text('private fixture')
        (root / '.mise.local.toml').write_text('private tool settings')
        (root / '.data').mkdir()
        (root / '.data/sentinel').write_text('private developer data')
        return git

    def qualification(self, root, evidence, parallel=False):
        selected = Mock()
        selected.info.return_value = {'engine': 'fixture', 'version': 'test'}
        selected.child_environment.return_value = smoke.isolated_environment(os.environ)
        selected.compose_command.return_value = ['compose']
        with patch.object(smoke, 'Runtime', return_value=selected), patch.object(smoke, 'unused_port', return_value=34567):
            qualification = smoke.Qualification(root, evidence, parallel)
        qualification.command = Mock()
        return qualification

    def test_disposable_linked_worktrees_exclude_local_state_and_cleanup_preserves_main(self):
        with tempfile.TemporaryDirectory() as directory:
            root, evidence = Path(directory) / 'main', Path(directory) / 'evidence'
            git = self.repository(root)
            qualification = self.qualification(root, evidence, parallel=True)
            try:
                with patch.object(smoke.manage, 'check_tools'):
                    qualification.prepare()
                self.assertEqual(len(qualification.worktrees), 2)
                for checkout in qualification.worktrees:
                    self.assertTrue(smoke.manage.linked_worktree(checkout))
                    self.assertEqual((checkout / 'tracked.txt').read_text(), 'source')
                    self.assertFalse((checkout / '.env').exists())
                    self.assertFalse((checkout / '.mise.local.toml').exists())
                    self.assertFalse((checkout / '.data').exists())
            finally:
                qualification.cleanup()
            self.assertEqual((root / '.data/sentinel').read_text(), 'private developer data')
            self.assertEqual((root / '.env').read_text(), 'private fixture')
            self.assertEqual(git('worktree', 'list', '--porcelain').count('worktree '), 1)
            self.assertFalse(qualification.workspace.exists())

    def test_dirty_source_fails_before_bootstrap_or_database_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'main'
            self.repository(root)
            (root / 'tracked.txt').write_text('uncommitted change')
            qualification = self.qualification(root, Path(directory) / 'evidence')
            try:
                with self.assertRaisesRegex(smoke.manage.SetupError, 'commit intended changes'):
                    qualification.prepare()
                qualification.command.assert_not_called()
                self.assertEqual(qualification.worktrees, [])
            finally:
                qualification.cleanup()
            self.assertEqual((root / 'tracked.txt').read_text(), 'uncommitted change')

    def test_bootstrap_failure_removes_owned_worktree_and_retains_failure_evidence(self):
        with tempfile.TemporaryDirectory() as directory:
            root, evidence = Path(directory) / 'main', Path(directory) / 'evidence'
            git = self.repository(root)
            evidence.mkdir()
            qualification = self.qualification(root, evidence)
            qualification.command.side_effect = smoke.manage.SetupError('bootstrap failed')
            with patch.object(smoke.manage, 'check_tools'), self.assertRaisesRegex(smoke.manage.SetupError, 'bootstrap failed'):
                qualification.run()
            summary = json.loads((evidence / 'summary.json').read_text())
            self.assertEqual(summary['status'], 'failed')
            self.assertEqual(summary['failure'], 'bootstrap failed')
            self.assertEqual(git('worktree', 'list', '--porcelain').count('worktree '), 1)
            self.assertFalse(qualification.workspace.exists())
            self.assertEqual((root / '.data/sentinel').read_text(), 'private developer data')

    def test_unproven_shutdown_preserves_fixtures_without_database_or_worktree_removal(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'main'
            self.repository(root)
            qualification = self.qualification(root, Path(directory) / 'evidence')
            workspace = qualification.workspace
            try:
                stack = Mock()
                stack.stop.side_effect = smoke.manage.SetupError('controller still active')
                qualification.stacks = [stack]
                qualification.database_started = True
                qualification.git = Mock()
                with self.assertRaisesRegex(smoke.manage.SetupError, 'cleanup incomplete'):
                    qualification.cleanup()
                qualification.command.assert_not_called()
                qualification.git.assert_not_called()
                self.assertTrue(workspace.exists())
            finally:
                workspace.rmdir()

    def test_authorization_refuses_a_foreign_callback_before_token_exchange(self):
        responses = [(302, {}, b''), (200, {}, b'<input name="__RequestVerificationToken" value="fixture&amp;token">'),
                     (302, {'Location': '/connect/authorize?client_id=romd-admin-spa'}, b''),
                     (302, {'Location': 'http://foreign:1234/auth/callback?code=private&state=wrong'}, b'')]
        with patch.object(smoke, 'request', side_effect=responses) as request:
            with self.assertRaisesRegex(smoke.manage.SetupError, 'callback did not match'):
                smoke.authorize('http://localhost:30000', 'http://localhost:30002', 'romd-admin-spa', 'fixture', 'fixture')
            self.assertEqual(request.call_count, 4)
            self.assertEqual(request.call_args_list[2].kwargs['form']['__RequestVerificationToken'], 'fixture&token')
            self.assertTrue(all('/connect/token' not in args[0] for args, _ in request.call_args_list))

    def test_authorization_requires_a_form_token_before_posting_credentials(self):
        with patch.object(smoke, 'request', side_effect=[(302, {}, b''), (200, {}, b'<form></form>')]) as request:
            with self.assertRaisesRegex(smoke.manage.SetupError, 'antiforgery token'):
                smoke.authorize('http://localhost:30000', 'http://localhost:30002', 'romd-admin-spa', 'fixture', 'fixture')
            self.assertEqual(request.call_count, 2)
            self.assertTrue(all(kwargs.get('form') is None for _, kwargs in request.call_args_list))

    def postgres_capture(self, raw_state=None, raw_logs=None):
        state = {'status': 'running', 'running': True, 'exitCode': 0, 'oomKilled': False,
                 'startedAt': '2026-10-19T10:00:00Z', 'finishedAt': '0001-01-01T00:00:00Z', 'health': 'healthy',
                 'Env': ['PASSWORD=private-credential']}
        success = {'exitCode': 0, 'truncated': False, 'timedOut': False}
        return [(b'a' * 64, success), (raw_state or json.dumps(state).encode(), success),
                (raw_logs or b'2026-10-19T10:00:00Z LOG: database system is ready to accept connections\n', success)]

    def test_postgres_diagnostics_are_selected_bounded_private_and_captured_before_removal(self):
        with tempfile.TemporaryDirectory() as directory:
            root, evidence = Path(directory) / 'main', Path(directory) / 'evidence'
            self.repository(root)
            qualification = self.qualification(root, evidence)
            qualification.database_started = True
            qualification.runtime.engine = 'podman'
            qualification.runtime.engine_command.side_effect = lambda *args: ['podman', *args]
            order = []
            def removal(name, _command):
                order.append(name)
                self.assertTrue((evidence / 'postgres/state.json').is_file())
                self.assertTrue((evidence / 'postgres/events.log').is_file())
            qualification.command.side_effect = removal
            logs = (b'2026-10-19T10:00:00Z LOG: server process (PID 321) was terminated by signal 13: Broken pipe private-token\n'
                    b'2026-10-19T10:00:01Z LOG: all server processes terminated; reinitializing\n'
                    b'STATEMENT: SELECT private_password, \'database system is ready to accept connections\'\n'
                    b'DETAIL: private-account@example.test\nFATAL: password=private-credential\n')
            with patch.object(smoke, 'bounded_capture', side_effect=self.postgres_capture(raw_logs=logs)) as capture:
                qualification.cleanup()
            self.assertEqual(order, ['postgres-down'])
            calls = capture.call_args_list
            self.assertEqual(calls[0].args[0][-4:], ['ps', '--all', '--quiet', 'postgres'])
            self.assertEqual(calls[0].kwargs['limit'], 256)
            self.assertEqual(calls[1].kwargs['limit'], 4096)
            template = calls[1].args[0][3]
            self.assertIn('.State.OOMKilled', template)
            self.assertIn('.State.Healthcheck.Status', template)
            self.assertNotIn('.Config', template)
            self.assertNotIn('{{json .}}', template)
            self.assertEqual(calls[2].args[0][1:5], ['logs', '--timestamps', '--tail', '200'])
            self.assertTrue(calls[2].kwargs['include_stderr'])
            state = json.loads((evidence / 'postgres/state.json').read_text())
            self.assertEqual(set(state), {'status', 'running', 'exitCode', 'oomKilled', 'startedAt', 'finishedAt', 'health'})
            events = (evidence / 'postgres/events.log').read_text()
            self.assertIn('server process (PID 321) was terminated by signal 13', events)
            self.assertIn('all server processes terminated; reinitializing', events)
            self.assertNotIn('private', events)
            self.assertNotIn('private', json.dumps(qualification.results).replace(str(qualification.workspace), ''))
            self.assertEqual(qualification.results['postgresDiagnostics']['status'], 'captured')
            self.assertEqual((evidence / 'postgres').stat().st_mode & 0o777, 0o700)
            for path in (evidence / 'postgres').iterdir():
                self.assertEqual(path.stat().st_mode & 0o777, 0o600)
                self.assertLess(path.stat().st_size, 65536)

    def test_postgres_capture_failure_does_not_mask_original_failure_or_prevent_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            root, evidence = Path(directory) / 'main', Path(directory) / 'evidence'
            self.repository(root)
            evidence.mkdir()
            qualification = self.qualification(root, evidence)
            def prepare():
                qualification.database_started = True
            qualification.prepare = prepare
            qualification.exercise = Mock(side_effect=smoke.manage.SetupError('original startup failure'))
            with patch.object(smoke, 'bounded_capture', side_effect=FileNotFoundError('private engine credentials')):
                with self.assertRaisesRegex(smoke.manage.SetupError, 'original startup failure'):
                    qualification.run()
            qualification.command.assert_called_once()
            self.assertEqual(qualification.command.call_args.args[0], 'postgres-down')
            summary = json.loads((evidence / 'summary.json').read_text())
            self.assertEqual(summary['failure'], 'original startup failure')
            self.assertEqual(summary['postgresDiagnostics']['status'], 'failed')
            self.assertTrue(summary['postgresDiagnostics']['errors'])
            self.assertNotIn('private engine', json.dumps(summary))
            self.assertEqual((evidence / 'summary.json').stat().st_mode & 0o777, 0o600)

    def test_non_object_postgres_state_preserves_original_failure_and_cleanup(self):
        for raw_state in (b'[]', b'null', b'123', b'"text"'):
            with self.subTest(raw_state=raw_state), tempfile.TemporaryDirectory() as directory:
                root, evidence = Path(directory) / 'main', Path(directory) / 'evidence'
                self.repository(root)
                evidence.mkdir()
                qualification = self.qualification(root, evidence)
                qualification.runtime.engine = 'docker'
                qualification.runtime.engine_command.side_effect = lambda *args: ['docker', *args]
                def prepare():
                    qualification.database_started = True
                qualification.prepare = prepare
                qualification.exercise = Mock(side_effect=smoke.manage.SetupError('original startup failure'))
                try:
                    with patch.object(smoke, 'bounded_capture', side_effect=self.postgres_capture(raw_state=raw_state)):
                        with self.assertRaisesRegex(smoke.manage.SetupError, 'original startup failure'):
                            qualification.run()
                    qualification.command.assert_called_once()
                    self.assertEqual(qualification.command.call_args.args[0], 'postgres-down')
                    summary = json.loads((evidence / 'summary.json').read_text())
                    self.assertEqual(summary['failure'], 'original startup failure')
                    self.assertEqual(summary['cleanupErrors'], [])
                    self.assertEqual(summary['postgresDiagnostics']['status'], 'partial')
                    self.assertTrue((evidence / 'postgres/events.log').is_file())
                    self.assertFalse((evidence / 'postgres/state.json').exists())
                finally:
                    if qualification.workspace.exists():
                        qualification.workspace.rmdir()

    def test_bad_state_does_not_prevent_safe_lifecycle_log_capture(self):
        with tempfile.TemporaryDirectory() as directory:
            root, evidence = Path(directory) / 'main', Path(directory) / 'evidence'
            self.repository(root)
            qualification = self.qualification(root, evidence)
            qualification.database_started = True
            qualification.runtime.engine = 'docker'
            qualification.runtime.engine_command.side_effect = lambda *args: ['docker', *args]
            with patch.object(smoke, 'bounded_capture', side_effect=self.postgres_capture(raw_state=b'{malformed}')):
                qualification.cleanup()
            self.assertTrue((evidence / 'postgres/events.log').is_file())
            self.assertFalse((evidence / 'postgres/state.json').exists())
            self.assertEqual(qualification.results['postgresDiagnostics']['status'], 'partial')

    def test_diagnostic_subprocess_output_and_time_are_hard_bounded(self):
        raw, info = smoke.bounded_capture([smoke.sys.executable, '-c', 'print("x" * 100000)'], os.environ, limit=1024)
        self.assertEqual(len(raw), 1024)
        self.assertTrue(info['truncated'])
        raw, info = smoke.bounded_capture([smoke.sys.executable, '-c', 'import sys; print("private", file=sys.stderr)'], os.environ)
        self.assertEqual(raw, b'')
        self.assertEqual(info['exitCode'], 0)
        raw, info = smoke.bounded_capture([smoke.sys.executable, '-c', 'import time; time.sleep(5)'], os.environ, timeout=0.05)
        self.assertEqual(raw, b'')
        self.assertTrue(info['timedOut'])


    def test_web_probe_waits_for_compiler_output_and_updated_module_from_every_vite_server(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            emitted = smoke.web_probe_output(root)
            emitted.parent.mkdir(parents=True)
            emitted.write_text('old output')
            stack = Mock(root=root, processes=Mock(spec=smoke.manage.Processes))
            stack.status.return_value = {'urls': {'admin_web': 'http://localhost:31002',
                'consumer_web': 'http://localhost:31003', 'player': 'http://localhost:31004'}}
            sleeps = []
            def compile_after_poll(_seconds):
                sleeps.append(True)
                emitted.write_text('export const probe = "updated-marker";')
            stale_then_current = [(200, {}, b'stale Vite module')] + [(200, {}, b'updated-marker')] * 5
            with patch.object(smoke.time, 'sleep', side_effect=compile_after_poll), \
                    patch.object(smoke, 'request', side_effect=stale_then_current) as request:
                smoke.wait_for_web_probe(stack, 'updated-marker', 1)
            self.assertEqual(len(sleeps), 2)
            self.assertEqual(request.call_count, 6)
            urls = [call.args[0] for call in request.call_args_list]
            for origin in stack.status.return_value['urls'].values():
                self.assertEqual(urls.count(origin + '/@fs' + str(emitted)), 2)
            self.assertGreaterEqual(stack.processes.assert_running.call_count, 3)

    def test_web_probe_times_out_when_compiler_or_vite_never_emits_expected_value(self):
        for compiler_updated in (False, True):
            with self.subTest(compiler_updated=compiler_updated), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                if compiler_updated:
                    emitted = smoke.web_probe_output(root)
                    emitted.parent.mkdir(parents=True)
                    emitted.write_text('expected-marker')
                stack = Mock(root=root, processes=Mock(spec=smoke.manage.Processes))
                stack.status.return_value = {'urls': {name: 'http://localhost:31002'
                    for name in ('admin_web', 'consumer_web', 'player')}}
                with patch.object(smoke.time, 'monotonic', side_effect=[0, 0, 2]), \
                        patch.object(smoke.time, 'sleep'), \
                        patch.object(smoke, 'request', return_value=(200, {}, b'foreign-or-stale-marker')) as request:
                    with self.assertRaisesRegex(smoke.manage.SetupError, 'shared web dependency compilation and Vite update'):
                        smoke.wait_for_web_probe(stack, 'expected-marker', 1)
                self.assertEqual(request.call_count, 3 if compiler_updated else 0)

    def test_shared_library_smoke_edits_only_first_checkout_and_preserves_owned_processes(self):
        with tempfile.TemporaryDirectory() as directory:
            root, evidence = Path(directory) / 'main', Path(directory) / 'evidence'
            self.repository(root)
            qualification = self.qualification(root, evidence, parallel=True)
            initial, stacks = [], []
            for index in range(2):
                checkout = Path(directory) / ('checkout-' + str(index))
                (checkout / smoke.WEB_PROBE.parent).mkdir(parents=True)
                state = {'pids': {name: 100 * (index + 1) + offset for offset, name in enumerate(smoke.WEB_PROCESSES)},
                         'backendDirectory': str(checkout / 'backend'),
                         'urls': {name: 'http://localhost:' + str(31000 + 10 * index + offset)
                                  for offset, name in enumerate(('admin_web', 'consumer_web', 'player'))}}
                stack = Mock(root=checkout, processes=Mock(spec=smoke.manage.Processes))
                stack.status.return_value = state
                stacks.append(stack)
                initial.append(state)
            qualification.stacks = stacks
            compiled = []
            def compile_after_poll(_seconds):
                for stack in stacks:
                    source, emitted = stack.root / smoke.WEB_PROBE, smoke.web_probe_output(stack.root)
                    if source.exists() and (not emitted.exists() or emitted.read_bytes() != source.read_bytes()):
                        emitted.parent.mkdir(parents=True, exist_ok=True)
                        emitted.write_bytes(source.read_bytes())
                        compiled.append((stack.root, source.read_bytes()))
            def serve_module(url, **_kwargs):
                for stack in stacks:
                    if any(url.startswith(origin + '/') for origin in stack.status.return_value['urls'].values()):
                        return 200, {}, smoke.web_probe_output(stack.root).read_bytes()
                self.fail('Unexpected Vite origin: ' + url)
            try:
                with patch.object(smoke.time, 'sleep', side_effect=compile_after_poll), \
                        patch.object(smoke, 'request', side_effect=serve_module), patch('sys.stdout', new=io.StringIO()):
                    qualification.exercise_web_dependencies(initial)
                self.assertEqual([item[0] for item in compiled].count(stacks[0].root), 2)
                self.assertEqual([item[0] for item in compiled].count(stacks[1].root), 1)
                for stack in stacks:
                    self.assertEqual(smoke.web_probe_output(stack.root).read_bytes(), (stack.root / smoke.WEB_PROBE).read_bytes())
                self.assertNotEqual(smoke.web_probe_output(stacks[0].root).read_bytes(), smoke.web_probe_output(stacks[1].root).read_bytes())
                self.assertEqual(len(qualification.results['stages']), 1)
            finally:
                qualification.workspace.rmdir()


if __name__ == '__main__':
    unittest.main()
