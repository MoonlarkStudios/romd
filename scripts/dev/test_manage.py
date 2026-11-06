import errno
import io
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import Mock, patch

import manage


def selected_runtime():
    runtime = Mock(spec=manage.Runtime)
    runtime.child_environment.return_value = os.environ.copy()
    runtime.engine_command.side_effect = lambda *arguments: ['podman', *arguments]
    runtime.compose_command.side_effect = lambda *arguments: ['docker-compose', *arguments]
    return runtime


class BootstrapTests(unittest.TestCase):
    def test_bootstrap_twice_preserves_existing_data_and_user_configuration(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'web').mkdir()
            config = root / 'appsettings.Development.json'
            data = root / 'database.db'
            config.write_text('{"existing":"private-value"}')
            data.write_bytes(b'existing database sentinel')
            before = {path.name: path.read_bytes() for path in (config, data)}
            commands = []
            with patch.object(manage, 'check_tools'), \
                    patch.object(manage, 'tool_versions', return_value={'node': '24.21.0'}), \
                    patch.object(manage, 'call') as call:
                call.side_effect = lambda command, **kwargs: commands.append((command, kwargs))
                with patch('sys.stdout', new=io.StringIO()):
                    manage.bootstrap(root)
                    manage.bootstrap(root)
            self.assertEqual(before, {path.name: path.read_bytes() for path in (config, data)})
            self.assertEqual([command for command, _ in commands], [
                ['mise', 'install', 'node@24.21.0'], ['dotnet', 'tool', 'restore'], ['dotnet', 'restore'],
                ['pnpm', 'install', '--frozen-lockfile'],
            ] * 2)
            self.assertEqual(commands[3][1]['cwd'], root / 'web')
            self.assertEqual(commands[3][1]['env']['CI'], 'true')


class DoctorTests(unittest.TestCase):
    def test_backend_compiler_is_required_only_for_explicit_test_preflight(self):
        with patch.object(manage, 'check_tools'), patch.object(manage, 'check_containers'), \
                patch.object(manage, 'check_dependencies'), \
                patch.object(manage, 'check_ports'), patch.object(manage, 'check_build_access'), \
                patch.object(manage, 'check_test_tools', side_effect=manage.SetupError('host clang missing')) as compiler, \
                patch('sys.stdout', new=io.StringIO()) as output:
            self.assertTrue(manage.doctor())
            compiler.assert_not_called()
            self.assertFalse(manage.doctor(test_tools=True))
            self.assertIn('host clang missing', output.getvalue())

    def test_build_access_uses_resolved_dockerfile_urls_without_shell_quotes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'Dockerfile').write_text('cmake=x86_64; cmake=aarch64;\n'
                'RUN curl "https://github.com/Kitware/CMake/linux-$cmake.tar.gz"\n'
                'RUN git clone https://github.com/example/companion.git\n')
            self.assertEqual(manage.build_urls(root), [
                'https://github.com/Kitware/CMake/linux-aarch64.tar.gz',
                'https://github.com/Kitware/CMake/linux-x86_64.tar.gz',
                'https://github.com/example/companion',
            ])

    def test_missing_engine_fails_actionably_while_other_checks_continue(self):
        output = io.StringIO()
        with patch.object(manage, 'check_tools'), patch.object(manage, 'check_ports') as ports, \
                patch.object(manage, 'check_dependencies'), \
                patch.object(manage, 'check_build_access') as access, \
                patch.object(manage.shutil, 'which', return_value=None), patch('sys.stdout', new=output):
            self.assertFalse(manage.doctor(mode='source'))
        self.assertIn('container engine', output.getvalue())
        ports.assert_called_once_with('source', manage.ROOT)
        access.assert_not_called()

    def test_only_container_mode_requires_pinned_image_source_access(self):
        with patch.object(manage, 'check_tools'), patch.object(manage, 'check_containers'), \
                patch.object(manage, 'check_dependencies'), patch.object(manage, 'image_residue_note'), \
                patch.object(manage, 'check_ports'), patch.object(manage, 'check_build_access') as access, \
                patch('sys.stdout', new=io.StringIO()):
            self.assertTrue(manage.doctor(mode='source'))
            access.assert_not_called()
            self.assertTrue(manage.doctor(mode='containers'))
            access.assert_called_once_with(manage.ROOT)

    def test_tool_errors_never_include_captured_secret_output(self):
        result = subprocess.CompletedProcess(['tool'], 1, stdout='secret-value', stderr='password=secret-value')
        with patch.object(manage.subprocess, 'run', return_value=result):
            with self.assertRaises(manage.SetupError) as error:
                manage.call(['tool'], capture=True)
        self.assertNotIn('secret-value', str(error.exception))

    def test_an_occupied_port_is_reported_without_stopping_its_owner(self):
        with socket.socket() as listener:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind(('127.0.0.1', 0))
            listener.listen()
            port = listener.getsockname()[1]
            with patch.object(manage, 'required_ports', return_value=[port]):
                with self.assertRaisesRegex(manage.SetupError, f'Port {port} is unavailable'):
                    manage.check_ports('source')
            self.assertEqual(listener.getsockname()[1], port)

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux TCP TIME_WAIT regression')
    def test_a_closed_server_port_in_time_wait_can_be_restarted(self):
        with socket.socket() as server, socket.socket() as client:
            server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            server.settimeout(2)
            client.settimeout(2)
            server.bind(('127.0.0.1', 0))
            server.listen()
            port = server.getsockname()[1]
            client.connect(('127.0.0.1', port))
            connection, _ = server.accept()
            with connection:
                connection.shutdown(socket.SHUT_RDWR)
            self.assertEqual(b'', client.recv(1))
        # The server actively closed the connection. Its port is no longer
        # listening, but Linux retains TCP state that rejects a plain bind.
        with socket.socket() as plain_probe:
            with self.assertRaises(OSError) as error:
                plain_probe.bind(('127.0.0.1', port))
            self.assertEqual(errno.EADDRINUSE, error.exception.errno)
        with patch.object(manage, 'required_ports', return_value=[port]):
            manage.check_ports('source')


class ConfigurationTests(unittest.TestCase):
    def setUp(self):
        # A temporary non-worktree root: port defaults must not depend on
        # whether the suite itself runs in an ordinary or linked checkout.
        checkout = tempfile.TemporaryDirectory()
        self.addCleanup(checkout.cleanup)
        self.root = Path(checkout.name)

    def test_linked_worktrees_receive_stable_checkout_ports_and_can_override_them(self):
        with tempfile.TemporaryDirectory() as directory, patch.dict(os.environ, {}, clear=True):
            root = Path(directory) / 'main'
            root.mkdir()
            def git(*args):
                subprocess.run(['git', '-C', str(root), *args], check=True,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            git('init', '-q')
            git('-c', 'user.name=ROMD Test', '-c', 'user.email=test@example.invalid',
                '-c', 'commit.gpgsign=false', 'commit', '-q', '--allow-empty', '-m', 'fixture')
            worktree = Path(directory) / 'agent'
            git('worktree', 'add', '-q', '--detach', str(worktree))
            self.assertFalse(manage.linked_worktree(root))
            self.assertTrue(manage.linked_worktree(worktree))
            primary = manage.port_settings('source', {}, root)
            selected = manage.port_settings('source', {}, worktree)
            self.assertEqual(primary['admin_api'], 5000)
            self.assertEqual(primary['admin_web'], 5137)
            self.assertFalse(set(primary.values()) & set(selected.values()))
            self.assertEqual(selected, manage.port_settings('source', {}, worktree))
            overrides = manage.port_settings('source', {'ROMD_DEV_PORT_BASE': '32000',
                                              'ROMD_DEV_PLAYER_PORT': '32100'}, worktree)
            self.assertEqual(list(overrides.values()), [32000, 32001, 32002, 32003, 32100])

    def test_port_base_rejects_invalid_range_and_overlapping_individual_override(self):
        for values in ({'ROMD_DEV_PORT_BASE': '65532'}, {'ROMD_DEV_PORT_BASE': 'bad'},
                       {'ROMD_DEV_PORT_BASE': '30000', 'ROMD_DEV_PLAYER_PORT': '30000'}):
            with self.subTest(values=values), self.assertRaises(manage.SetupError):
                manage.port_settings('source', values, self.root)

    def test_compose_is_worker_first_loopback_and_checkout_scoped(self):
        services = {name: {'environment': {}, 'restart': 'unless-stopped',
                           **({'build': {'context': '.', 'target': name}} if name != 'postgres' else {})}
                    for name in ('postgres', 'romd-worker', 'romd-admin', 'romd-consumer', 'romd-player')}
        for name in ('romd-admin', 'romd-consumer'):
            services[name]['depends_on'] = {'romd-worker': {'condition': 'service_started'}}
            services[name]['ports'] = [{'target': 8080, 'published': '11337'}]
        original = {'name': 'romd-dev', 'services': services,
                    'volumes': {'romd-dev-data': {}, 'romd-dev-postgres': {}},
                    'networks': {'default': {'name': 'romd-dev_default'}}}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(manage, 'call', return_value=json.dumps(original)):
                config = manage.docker_configuration(root, 'owned-run', selected_runtime())
            worker = config['services']['romd-worker']
            self.assertEqual(worker['environment']['Romd__ReadinessMarkerPath'], '/run/romd/ready')
            self.assertEqual(worker['healthcheck']['test'], ['CMD-SHELL', 'test -f /run/romd/ready'])
            for name in ('romd-admin', 'romd-consumer'):
                service = config['services'][name]
                self.assertEqual(service['depends_on']['romd-worker']['condition'], 'service_healthy')
                self.assertEqual(service['ports'][0]['host_ip'], '127.0.0.1')
                self.assertIn('/health/ready', service['healthcheck']['test'][-1])
                self.assertEqual(service['restart'], 'no')
            for name in ('romd-worker', 'romd-admin', 'romd-consumer', 'romd-player'):
                service = config['services'][name]
                # Stable per checkout (not per run) so rebuilds replace the tag and reuse cache.
                self.assertEqual(service['image'], f'localhost/romd-dev-{manage.checkout_id(root)}-{name}:local')
                self.assertEqual(service['build']['labels'], {'com.romd.dev.checkout': manage.checkout_id(root)})
            self.assertNotIn('image', config['services']['postgres'])
            self.assertTrue(all(manage.checkout_id(root) in volume['name']
                                for volume in config['volumes'].values()))
            self.assertTrue(all(volume['external'] for volume in config['volumes'].values()))
            self.assertNotEqual(manage.checkout_id(root), manage.checkout_id(root / 'other-checkout'))
            self.assertEqual(config['networks']['default']['name'], 'owned-run_default')

    def test_port_overrides_configure_authentication_and_player_boundaries_consistently(self):
        ports = manage.port_settings('source', {'ROMD_DEV_ADMIN_PORT': '15000',
            'ROMD_DEV_CONSUMER_PORT': '15002', 'ROMD_DEV_ADMIN_WEB_PORT': '15137',
            'ROMD_DEV_CONSUMER_WEB_PORT': '15174', 'ROMD_DEV_PLAYER_PORT': '15175'}, self.root)
        env = manage.origin_environment(ports)
        self.assertEqual(env['Romd__Auth__AdminSpa__RedirectUris__0'], 'http://localhost:15137/auth/callback')
        self.assertEqual(env['Romd__Auth__ConsumerSpa__PostLogoutRedirectUris__0'], 'http://localhost:15174')
        self.assertEqual(env['Romd__BrowserPlayback__PlayerOrigins__0__Parent'], 'http://localhost:15174')
        self.assertEqual(env['Romd__BrowserPlayback__PlayerOrigins__0__Player'], 'http://localhost:15175')

    def test_invalid_or_duplicate_port_overrides_fail_before_startup(self):
        # Collide with the consumer web port this root actually selects.
        consumer_web = str(manage.port_settings('source', {}, self.root)['consumer_web'])
        for values in ({'ROMD_DEV_ADMIN_PORT': '80'}, {'ROMD_DEV_ADMIN_PORT': 'secret-value'},
                       {'ROMD_DEV_ADMIN_WEB_PORT': consumer_web}):
            with self.subTest(values=values), self.assertRaises(manage.SetupError):
                manage.port_settings('source', values, self.root)

    def test_source_uses_only_checkout_database_exports_and_one_data_directory(self):
        exports = '\n'.join(f'export {key}=Host=127.0.0.1\\;Database=owned'
                            for key in ('ConnectionStrings__Romd', 'ConnectionStrings__RomdProvisioning',
                                        'ConnectionStrings__Hangfire', 'ConnectionStrings__HangfireProvisioning'))
        with tempfile.TemporaryDirectory() as directory, patch.object(manage, 'call', return_value=exports), \
                patch.dict(os.environ, {'CONNECTIONSTRINGS__ROMD': 'unrelated-database-credential',
                                        'ROMD__READINESSMARKERPATH': '/foreign/marker',
                                        'ROMD__ADMINHOST__CORSORIGINS__0': 'http://foreign:1234'}):
            for key in [key for key in os.environ if key.startswith('ROMD_DEV_')]:
                del os.environ[key]  # Restored by patch.dict; inherited port selections are not under test.
            root = Path(directory)
            environment = manage.source_environment(root, root / 'run/ready')
            self.assertEqual(environment['Romd__DataDirectory'], str(root.resolve() / '.data/romd'))
            self.assertEqual(environment['Romd__ReadinessMarkerPath'], str(root / 'run/ready'))
            self.assertEqual(environment['ConnectionStrings__Romd'], 'Host=127.0.0.1;Database=owned')
            self.assertEqual(environment['ConnectionStrings__Hangfire'], environment['ConnectionStrings__Romd'])
            self.assertNotIn('CONNECTIONSTRINGS__ROMD', environment)
            self.assertNotIn('ROMD__READINESSMARKERPATH', environment)
            self.assertNotIn('ROMD__ADMINHOST__CORSORIGINS__0', environment)
            self.assertEqual(environment['Romd__AdminHost__CorsOrigins__0'], 'http://localhost:5137')

    def test_existing_durable_data_and_configuration_are_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            durable = root / '.data/romd'
            durable.mkdir(parents=True)
            sentinel = durable / 'server-identity'
            sentinel.write_bytes(b'existing identity and CAS marker')
            config = root / manage.HOSTS['admin'] / 'appsettings.Development.json'
            config.parent.mkdir(parents=True)
            config.write_text(json.dumps({'Romd': {'DataDirectory': str(durable)}}))
            before = {path: path.read_bytes() for path in (sentinel, config)}
            self.assertEqual(manage.source_data_directory(root, {}), str(durable.resolve()))
            explicit = root / 'existing-custom-data'
            explicit.mkdir()
            self.assertEqual(manage.source_data_directory(root, {'Romd__DataDirectory': str(explicit)}), str(explicit.resolve()))
            self.assertEqual(manage.source_data_directory(root, {'ROMD__DATADIRECTORY': str(explicit)}), str(explicit.resolve()))
            self.assertEqual(before, {path: path.read_bytes() for path in (sentinel, config)})

    def test_incomplete_database_exports_cannot_inherit_other_credentials(self):
        with tempfile.TemporaryDirectory() as directory, \
                patch.object(manage, 'call', return_value='export ConnectionStrings__Romd=owned'), \
                patch.dict(os.environ, {'CONNECTIONSTRINGS__HANGFIRE': 'foreign-credential'}):
            with self.assertRaisesRegex(manage.SetupError, 'Incomplete checkout database exports'):
                manage.source_environment(Path(directory), Path(directory) / 'ready')

    def test_conflicting_or_relative_durable_paths_fail_without_mutation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ('admin', 'worker'):
                config = root / manage.HOSTS[name] / 'appsettings.Development.json'
                config.parent.mkdir(parents=True)
                config.write_text(json.dumps({'Romd': {'DataDirectory': str(root / name)}}))
            with self.assertRaisesRegex(manage.SetupError, 'disagree'):
                manage.source_data_directory(root, {})
            with self.assertRaisesRegex(manage.SetupError, 'absolute'):
                manage.source_data_directory(root, {'Romd__DataDirectory': 'relative-path'})

    def test_source_waits_for_worker_then_each_ready_api_before_portals(self):
        events = []
        class FakeProcesses:
            def start(self, name, command, **kwargs):
                events.append(('start', name, command, kwargs['env']))
        processes = FakeProcesses()
        with tempfile.TemporaryDirectory() as directory, patch.object(manage, 'call'), \
                patch.object(manage, 'build_source_hosts'), \
                patch.object(manage, 'source_environment', return_value={
                    'ConnectionStrings__RomdProvisioning': 'worker-only',
                    'ConnectionStrings__HangfireProvisioning': 'worker-only',
                    'ROMD__READINESSMARKERPATH': 'worker-only'}), \
                patch.object(manage, 'wait_for', side_effect=lambda condition, p, label, timeout:
                             events.append(('ready', label))), \
                patch.object(manage, 'start_portals', side_effect=lambda *a, **kw:
                             events.append(('portals',))):
            root = Path(directory)
            processes.directory = root
            manage.source_start(root, root, processes, processes, selected_runtime())
        self.assertEqual([event[:2] for event in events], [
            ('start', 'worker'), ('ready', 'worker provisioning and seeders'),
            ('start', 'admin'), ('ready', 'admin API readiness'),
            ('start', 'consumer'), ('ready', 'consumer API readiness'), ('portals',),
        ])
        for event in events:
            if event[0] == 'start':
                self.assertEqual(event[2][:3], ['dotnet', 'run', '--no-build'])
                self.assertEqual('ConnectionStrings__RomdProvisioning' in event[3], event[1] == 'worker')
                self.assertEqual('ConnectionStrings__HangfireProvisioning' in event[3], event[1] == 'worker')
                self.assertEqual('ROMD__READINESSMARKERPATH' in event[3], event[1] == 'worker')

    def test_backend_input_snapshot_observes_edits_and_deletions_but_ignores_outputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / 'src/Example/Program.cs'
            source.parent.mkdir(parents=True)
            source.write_text('before')
            before = manage.source_snapshot(root)
            (source.parent / 'bin').mkdir()
            (source.parent / 'bin/Generated.cs').write_text('generated output')
            self.assertEqual(manage.source_snapshot(root), before)
            source.write_text('after')
            self.assertNotEqual(manage.source_snapshot(root), before)
            source.unlink()
            self.assertEqual(manage.source_snapshot(root), {})

    def test_root_build_settings_trigger_reload(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            before = manage.source_snapshot(root)
            (root / 'Directory.Build.props').write_text('<Project/>')
            self.assertNotEqual(before, manage.source_snapshot(root))

    def test_docker_refuses_preexisting_checkout_containers_without_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(manage, 'call', return_value='existing-container\n') as call:
                with self.assertRaisesRegex(manage.SetupError, 'earlier managed run'):
                    manage.docker_start(root, root, None, 'new-owned-run', selected_runtime())
            self.assertEqual(call.call_count, 1)
            self.assertEqual(call.call_args.args[0][:3], ['podman', 'ps', '--quiet'])
            self.assertFalse((root / 'compose.json').exists())

    def test_docker_start_failure_cleans_only_its_project_and_keeps_volumes(self):
        commands = []
        def fake_call(command, **kwargs):
            commands.append(command)
            if command[:2] == ['podman', 'ps']:
                return ''
            if 'up' in command:
                raise manage.SetupError('deliberate startup failure')
            return ''
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(manage, 'call', side_effect=fake_call), \
                    patch.object(manage, 'build_container_images'), \
                    patch.object(manage, 'ensure_checkout_volumes'), \
                    patch.object(manage, 'docker_configuration', return_value={}):
                with self.assertRaisesRegex(manage.SetupError, 'deliberate startup failure'):
                    manage.docker_start(root, root, None, 'new-owned-run', selected_runtime())
            cleanup = commands[-1]
            self.assertEqual(cleanup[:3], ['docker-compose', '-p', 'new-owned-run'])
            self.assertEqual(cleanup[-3:], ['down', '--timeout', '30'])
            self.assertNotIn('--volumes', cleanup)
            self.assertEqual((root / 'compose.json').stat().st_mode & 0o777, 0o600)

    def test_existing_persistent_volume_with_foreign_owner_is_never_reset(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = {'volumes': {'data': {'name': 'existing-checkout-data'}}}
            with patch.object(manage, 'call', side_effect=['existing-checkout-data\n', 'foreign-owner']) as call:
                with self.assertRaisesRegex(manage.SetupError, 'different owner'):
                    manage.ensure_checkout_volumes(root, config, selected_runtime())
            self.assertEqual(call.call_count, 2)
            self.assertTrue(all('create' not in invocation.args[0] for invocation in call.call_args_list))


class ManagedPortalTests(unittest.TestCase):
    def test_selected_apps_share_one_dependency_build_and_watch_before_direct_vite(self):
        for player in (False, True):
            with self.subTest(player=player), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                events = []
                processes = Mock(directory=root)
                builder = Mock(directory=root)
                child = builder.start.return_value
                child.wait.return_value = 0
                builder.start.side_effect = lambda *args, **kwargs: events.append(('prepare', args, kwargs)) or child
                builder.stop.side_effect = lambda: events.append(('prepared',))
                processes.start.side_effect = lambda *args, **kwargs: events.append(('start', args, kwargs))
                origins = {'admin': 'http://localhost:31000', 'consumer': 'http://localhost:31001'}
                ports = {'admin_web': 31002, 'consumer_web': 31003, 'player': 31004}
                environment = {'OWNED_CHECKOUT': 'fixture'}
                with patch.object(manage, 'Processes', return_value=builder), \
                        patch.object(manage, 'wait_for') as ready, patch('sys.stdout', new=io.StringIO()):
                    manage.start_portals(processes, environment, origins, root, player=player, ports=ports)
                apps = ['romd-admin-app', 'romd-consumer-app'] + (['romd-player-app'] if player else [])
                filters = [argument for app in apps for argument in ('--filter', app + '^...')]
                self.assertEqual(events[0][1], ('build-web-dependencies', ['pnpm', '--recursive', *filters, 'exec', 'tsc']))
                self.assertEqual(events[0][2], {'cwd': root / 'web', 'env': environment})
                self.assertEqual(events[1], ('prepared',))
                child.wait.assert_called_once_with(timeout=300)
                builder.stop.assert_called_once()
                launches = [event for event in events if event[0] == 'start']
                self.assertEqual(launches[0][1], ('web-dependencies',
                    ['pnpm', '--recursive', *filters, '--parallel', 'exec', 'tsc', '--watch', '--preserveWatchOutput']))
                self.assertEqual(len(launches), len(apps) + 1)
                for launch, name, app in zip(launches[1:], ('admin', 'consumer', 'player'), apps):
                    port = ports['player' if name == 'player' else name + '_web']
                    self.assertEqual(launch[1], ('web-' + name, ['pnpm', '--filter', app, 'dev', '--port', str(port)]))
                    self.assertEqual(launch[2]['cwd'], root / 'web')
                    self.assertEqual(launch[2]['env']['OWNED_CHECKOUT'], 'fixture')
                    key = 'VITE_ROMD_PLAYER_ALLOWED_PARENTS' if name == 'player' else f'VITE_ROMD_{name.upper()}_API_ORIGIN'
                    expected = 'http://localhost:31003' if name == 'player' else origins[name]
                    self.assertEqual(launch[2]['env'][key], expected)
                self.assertEqual(ready.call_count, len(apps))
                self.assertEqual(environment, {'OWNED_CHECKOUT': 'fixture'})

    def test_preparation_failure_timeout_or_cancellation_stops_owner_without_starting_watchers_or_vite(self):
        for failure in (1, subprocess.TimeoutExpired('pnpm', 300), KeyboardInterrupt()):
            with self.subTest(failure=type(failure).__name__), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                processes = Mock(directory=root)
                builder = Mock(directory=root)
                if isinstance(failure, BaseException):
                    builder.start.return_value.wait.side_effect = failure
                else:
                    builder.start.return_value.wait.return_value = failure
                with patch.object(manage, 'Processes', return_value=builder), patch('sys.stdout', new=io.StringIO()):
                    expected = KeyboardInterrupt if isinstance(failure, KeyboardInterrupt) else manage.HostFailure
                    with self.assertRaises(expected) as error:
                        manage.start_portals(processes, {}, {}, root, player=True,
                                             ports={'admin_web': 31002, 'consumer_web': 31003, 'player': 31004})
                builder.stop.assert_called_once()
                processes.start.assert_not_called()
                if not isinstance(failure, KeyboardInterrupt):
                    self.assertEqual(error.exception.component, 'build-web-dependencies')
                    self.assertEqual(error.exception.log, root / 'build-web-dependencies.log')


@unittest.skipIf(os.name == 'nt', 'Managed development uses POSIX process groups')
class OwnershipTests(unittest.TestCase):
    def test_edit_failure_and_recovery_publish_current_logs_and_join_owned_hosts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / 'src/Probe.cs'
            source.parent.mkdir()
            source.write_text('valid')
            runner_code = f'''
import sys
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0, {str(Path(manage.__file__).parent)!r})
import manage
root = Path({str(root)!r})
def start(root, directory, backend):
    if (root / 'src/Probe.cs').read_text() == 'invalid':
        (directory / 'build-worker.log').write_text('compiler failure')
        raise manage.SetupError('compiler failure')
    backend.start('worker', [sys.executable, '-c', 'import time; time.sleep(60)'], cwd=root)
def source_start(root, directory, processes, backend, runtime):
    start(root, backend.directory, backend)
    processes.start('web-dependencies', [sys.executable, '-c', 'import time; time.sleep(60)'], cwd=root)
with patch.object(manage, 'check_tools'), patch.object(manage, 'check_containers'), \\
     patch.object(manage, 'check_web_dependencies'), \\
     patch.object(manage, 'check_ports'), patch.object(manage, 'start_source_hosts', side_effect=start), \\
     patch.object(manage, 'source_start', side_effect=source_start):
    try:
        manage.development('source', root)
    except KeyboardInterrupt:
        sys.exit(130)
'''
            with (root / 'runner.log').open('w') as log:
                runner = subprocess.Popen([sys.executable, '-c', runner_code], stdout=log,
                                          stderr=subprocess.STDOUT, start_new_session=True)
            def wait_state(state, previous=None):
                deadline = time.monotonic() + 30  # Bounded; tolerates parallel-suite load.
                while time.monotonic() < deadline:
                    current = manage.session_status(root)
                    if current['state'] == state and (previous is None or current['backendDirectory'] != previous):
                        return current
                    self.assertIsNone(runner.poll(), (root / 'runner.log').read_text())
                    time.sleep(0.01)
                self.fail(f'Timed out waiting for {state}: {(root / "runner.log").read_text()}')
            try:
                first = wait_state('ready')
                pid = first['pids']['worker']
                watcher = first['pids']['web-dependencies']
                source.write_text('invalid')
                failed = wait_state('reload-failed')
                self.assertTrue(failed['active'])
                self.assertEqual(failed['pids'], {'web-dependencies': watcher})
                self.assertEqual(failed['error'], 'compiler failure')
                self.assertNotEqual(first['backendDirectory'], failed['backendDirectory'])
                self.assertEqual((Path(failed['backendDirectory']) / 'build-worker.log').read_text(), 'compiler failure')
                self.assertFalse(manage.group_exists(pid))
                source.write_text('corrected')
                recovered = wait_state('ready', failed['backendDirectory'])
                self.assertNotEqual(recovered['pids']['worker'], pid)
                self.assertEqual(recovered['pids']['web-dependencies'], watcher)
                self.assertNotIn('error', recovered)
                runner.send_signal(signal.SIGINT)
                self.assertEqual(runner.wait(timeout=10), 130)
                stopped = manage.session_status(root)
                self.assertEqual(stopped['state'], 'stopped')
                self.assertFalse(stopped['active'])
                self.assertFalse(manage.group_exists(recovered['pids']['worker']))
                self.assertFalse(manage.group_exists(watcher))
            finally:
                if runner.poll() is None:
                    runner.send_signal(signal.SIGINT)
                    runner.wait(timeout=10)

    def test_status_is_private_and_detects_crashed_sessions_without_trusting_pid(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            expected = {name: f'http://localhost:{port}' for name, port in manage.port_settings('source', root=root).items()}
            self.assertEqual(manage.session_status(root), {'schemaVersion': manage.STATUS_SCHEMA_VERSION,
                                                           'state': 'not-started', 'mode': 'source',
                                                           'urls': expected, 'active': False})
            with manage.session_lock(root):
                record = {'state': 'ready', 'pid': 1, 'urls': {'admin_web': 'http://localhost:30002'}}
                manage.write_session(root, record)
                self.assertEqual(manage.session_status(root), record | {'active': True})
                self.assertEqual((root / '.data/dev/session.json').stat().st_mode & 0o777, 0o600)
            self.assertEqual(manage.session_status(root)['state'], 'interrupted')
            self.assertFalse(manage.session_status(root)['active'])
            manage.write_session(root, record | {'state': 'stopped'})
            self.assertEqual(manage.session_status(root)['state'], 'stopped')

    def test_repeated_shutdown_signals_cannot_interrupt_owned_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            marker = root / 'owned.pid'
            cleaning = root / 'cleaning'
            child_code = ('import os,signal,time; from pathlib import Path; '
                          'signal.signal(signal.SIGTERM,lambda *_:None); '
                          f'Path({str(marker)!r}).write_text(str(os.getpid())); time.sleep(60)')
            runner = f'''
import sys,time
from pathlib import Path
from unittest.mock import Mock, patch
sys.path.insert(0,{str(Path(manage.__file__).parent)!r})
import manage
root=Path({str(root)!r})
class Processes(manage.Processes):
    def stop(self, grace=0.2):
        Path({str(cleaning)!r}).touch()
        time.sleep(0.3)
        super().stop(grace)
def start(root,directory,processes,backend,runtime):
    backend.start('owned',[sys.executable,'-c',{child_code!r}],cwd=root)
with patch.object(manage,'check_tools'),patch.object(manage,'check_containers'), \\
     patch.object(manage,'check_web_dependencies'), \\
     patch.object(manage,'check_ports'),patch.object(manage,'source_start',side_effect=start), \\
     patch.object(manage,'Processes',Processes):
    try:
        manage.development('source',root)
    except KeyboardInterrupt:
        sys.exit(130)
'''
            with (root / 'runner.log').open('w') as log:
                runner_process = subprocess.Popen([sys.executable, '-c', runner], stdout=log,
                                                  stderr=subprocess.STDOUT, start_new_session=True)
            child_pid = None
            try:
                deadline = time.monotonic() + 5
                while time.monotonic() < deadline and not marker.exists():
                    time.sleep(0.01)
                self.assertTrue(marker.exists(), (root / 'runner.log').read_text())
                child_pid = int(marker.read_text())
                runner_process.send_signal(signal.SIGINT)
                while time.monotonic() < deadline and not cleaning.exists():
                    time.sleep(0.01)
                self.assertTrue(cleaning.exists())
                for number in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
                    runner_process.send_signal(number)
                self.assertEqual(runner_process.wait(timeout=5), 130,
                                 (root / 'runner.log').read_text())
                with self.assertRaises(ProcessLookupError):
                    os.kill(child_pid, 0)
            finally:
                if runner_process.poll() is None:
                    runner_process.kill()
                    runner_process.wait(timeout=5)
                if child_pid:
                    try:
                        os.killpg(child_pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass

    def test_another_session_cannot_take_checkout_lock(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with manage.session_lock(root):
                with self.assertRaisesRegex(manage.SetupError, 'already owns'):
                    with manage.session_lock(root):
                        self.fail('second session acquired ownership')

    def test_shutdown_joins_stubborn_descendant_and_preserves_foreign_process(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            marker = root / 'descendant.pid'
            child_code = ('import os,signal,time; from pathlib import Path; '
                          'signal.signal(signal.SIGTERM,lambda *_:None); '
                          f'Path({str(marker)!r}).write_text(str(os.getpid())); time.sleep(60)')
            parent_code = ('import subprocess,sys,signal,time; '
                           f'subprocess.Popen([sys.executable,"-c",{child_code!r}]); '
                           'signal.signal(signal.SIGTERM,lambda *_:sys.exit(0)); time.sleep(60)')
            processes = manage.Processes(root)
            foreign = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'],
                                       start_new_session=True)
            child_pid = None
            try:
                processes.start('owned', [sys.executable, '-c', parent_code], cwd=root)
                deadline = time.monotonic() + 5
                while time.monotonic() < deadline:
                    if marker.exists() and marker.read_text():
                        child_pid = int(marker.read_text())
                        break
                    time.sleep(0.01)
                self.assertIsNotNone(child_pid)
                processes.stop(grace=0.2)
                with self.assertRaises(ProcessLookupError):
                    os.kill(child_pid, 0)
                self.assertIsNone(foreign.poll())
            finally:
                processes.stop(grace=0.01)
                os.killpg(foreign.pid, signal.SIGTERM)
                foreign.wait(timeout=5)



def web_checkout(root, lockfile=b'lockfileVersion: 9.0\n'):
    (root / 'web').mkdir(parents=True, exist_ok=True)
    (root / 'web/pnpm-lock.yaml').write_bytes(lockfile)
    return root / 'web/node_modules'


class PreflightTests(unittest.TestCase):
    def test_missing_web_dependencies_fail_with_bootstrap_hint(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            web_checkout(root)
            with self.assertRaisesRegex(manage.SetupError, 'not installed: run `mise run bootstrap`'):
                manage.check_web_dependencies(root)

    def test_pnpm_installed_lockfile_or_bootstrap_marker_satisfies_check_and_lockfile_change_is_stale(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            modules = web_checkout(root)
            (modules / '.pnpm').mkdir(parents=True)
            (modules / '.modules.yaml').write_text('layoutVersion: 5\n')
            (modules / '.pnpm/lock.yaml').write_bytes(b'lockfileVersion: 9.0\n')
            manage.check_web_dependencies(root)
            (root / 'web/pnpm-lock.yaml').write_bytes(b'lockfileVersion: 9.0\nchanged: true\n')
            with self.assertRaisesRegex(manage.SetupError, 'stale.*mise run bootstrap'):
                manage.check_web_dependencies(root)
            # Bootstrap records the digest it installed from (covers platform-filtered pnpm copies).
            manage.record_web_dependencies(root)
            manage.check_web_dependencies(root)
            self.assertEqual((modules / manage.WEB_DEPENDENCY_MARKER).read_text().strip(), manage.lockfile_digest(root))

    def test_unrestored_repository_dotnet_tools_are_reported(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / '.config').mkdir()
            (root / '.config/dotnet-tools.json').write_text(json.dumps(
                {'version': 1, 'tools': {'dotnet-ef': {'version': '10.0.7'}, 'Other.Tool': {'version': '1.2.3'}}}))
            packages = root / 'packages'
            (packages / 'dotnet-ef/10.0.7').mkdir(parents=True)
            with self.assertRaisesRegex(manage.SetupError, r'\(Other.Tool\): run `mise run bootstrap`'):
                manage.check_dotnet_tools(root, {'NUGET_PACKAGES': str(packages)})
            (packages / 'other.tool/1.2.3').mkdir(parents=True)
            manage.check_dotnet_tools(root, {'NUGET_PACKAGES': str(packages)})

    def test_doctor_reports_missing_dependencies_as_failure(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(manage, 'check_tools'), \
                patch.object(manage, 'check_containers'), patch.object(manage, 'check_ports'), \
                patch.object(manage, 'source_data_directory'), patch('sys.stdout', new=io.StringIO()) as output:
            root = Path(directory)
            web_checkout(root)
            self.assertFalse(manage.doctor(root))
        self.assertIn('FAIL restored web and .NET tool dependencies: Web dependencies are not installed: '
                      'run `mise run bootstrap`.', output.getvalue())

    def test_development_refuses_missing_web_dependencies_before_database_or_build(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(manage, 'check_tools'), \
                patch.object(manage, 'check_containers') as containers, patch.object(manage, 'call') as call, \
                patch.object(manage, 'source_start') as start, patch.object(manage, 'docker_start') as docker:
            root = Path(directory)
            web_checkout(root)
            for mode in ('source', 'containers'):
                with self.subTest(mode=mode), self.assertRaisesRegex(manage.SetupError, 'mise run bootstrap'):
                    manage.development(mode, root)
            for mock in (containers, call, start, docker):
                mock.assert_not_called()
            self.assertFalse((root / '.data').exists())


class DiagnosticsTests(unittest.TestCase):
    def test_build_errors_are_distinct_relative_and_limited(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            log = root / 'build-worker.log'
            source = root / 'src/Romd.Worker.Host/Probe.cs'
            log.write_text('\n'.join([
                '  Determining projects to restore...',
                f"{source}(1,2): error CS1029: #error: 'EXPECTED' [{root}/src/Romd.Worker.Host/Romd.Worker.Host.csproj]",
                f"{source}(1,2): warning CS0168: unused [{root}/x.csproj]",
                'Build FAILED.',
                f"{source}(1,2): error CS1029: #error: 'EXPECTED' [{root}/src/Romd.Worker.Host/Romd.Worker.Host.csproj]",
                f'{root}/Directory.Build.targets(4,5): error MSB4018: task failed [{root}/x.csproj]',
                'error NU1101: Unable to find package Missing.',
                f'{source}(9,9): error CS0103: fourth error is beyond the limit',
            ]))
            self.assertEqual(manage.build_errors(log, root), [
                "src/Romd.Worker.Host/Probe.cs(1,2): error CS1029: #error: 'EXPECTED'",
                'Directory.Build.targets(4,5): error MSB4018: task failed',
                'error NU1101: Unable to find package Missing.',
            ])
            self.assertEqual(manage.build_errors(root / 'missing.log', root), [])

    def test_failure_fields_name_component_log_and_compiler_errors(self):
        build = manage.HostFailure('worker build failed; inspect /run/build-worker.log locally.', 'build-worker',
                                   Path('/run/build-worker.log'), ['Probe.cs(1,1): error CS1029: x'])
        self.assertEqual(manage.failure_fields(build), {
            'error': 'worker build failed; inspect /run/build-worker.log locally. Probe.cs(1,1): error CS1029: x',
            'failedComponent': 'build-worker', 'log': '/run/build-worker.log'})
        exited = manage.ProcessExited('admin exited -9', 'admin', Path('/run/admin.log'), ['private log tail'])
        self.assertEqual(manage.failure_fields(exited),
                         {'error': 'admin exited -9', 'failedComponent': 'admin', 'log': '/run/admin.log'})
        self.assertEqual(manage.failure_fields(manage.SetupError('plain')), {'error': 'plain'})

    def test_embedded_reference_assets_trigger_reload_but_other_images_do_not(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            platform = root / 'reference-data/assets/presentation/platforms/gb.png'
            rating = root / 'reference-data/assets/presentation/ratings/esrb/e.svg'
            for path in (platform, rating):
                path.parent.mkdir(parents=True)
                path.write_bytes(b'before')
            unrelated = root / 'src/Example/wwwroot-like/logo.png'
            unrelated.parent.mkdir(parents=True)
            unrelated.write_bytes(b'before')
            before = manage.source_snapshot(root)
            self.assertIn('reference-data/assets/presentation/platforms/gb.png', before)
            self.assertIn('reference-data/assets/presentation/ratings/esrb/e.svg', before)
            unrelated.write_bytes(b'after')
            self.assertEqual(before, manage.source_snapshot(root))
            rating.write_bytes(b'after')
            self.assertNotEqual(before, manage.source_snapshot(root))


class ImageOwnershipTests(unittest.TestCase):
    def fake_engine(self, root, images, dangling=(), tags=None, failing=()):
        identity = manage.checkout_id(root)
        commands = []
        def fake_call(command, **kwargs):
            commands.append(command)
            if command[1:3] == ['images', '--format']:
                return '\n'.join(images) + '\n'
            if command[1:3] == ['images', '--quiet']:
                self.assertIn(f'label=com.romd.dev.checkout={identity}', command)
                return '\n'.join(dangling)
            if command[1:3] == ['image', 'inspect'] and command[4] == '{{.Id}}':
                if command[5] not in (tags or {}):
                    raise manage.SetupError('missing')
                return tags[command[5]] + '\n'
            if command[1:3] == ['image', 'inspect']:
                return json.dumps((tags or {}).get('repo-tags:' + command[5], []))
            if command[1] == 'rmi' and command[2] in failing:
                raise manage.SetupError('in use')
            return ''
        return fake_call, commands

    def test_only_this_checkouts_legacy_current_and_labelled_untagged_images_are_selected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            identity = manage.checkout_id(root)
            foreign = manage.checkout_id(root / 'other')
            images = [f'localhost/romd-dev-{identity}-d170dab2-romd-admin:local',
                      f'localhost/romd-dev-{identity}-4f71ba3b-romd-player:local',
                      f'localhost/romd-dev-{identity}-romd-worker:local',
                      f'localhost/romd-dev-{foreign}-d170dab2-romd-admin:local',
                      f'localhost/romd-dev-{foreign}-romd-worker:local',
                      f'localhost/romd-dev-{identity}-d170dab2-unrelated:local',
                      f'docker.io/library/romd-dev-{identity}-d170dab2-romd-admin:local',
                      'docker.io/library/postgres:18-bookworm', '<none>:<none>']
            fake_call, _ = self.fake_engine(root, images, ['sha256:aaa', 'sha256:aaa'])
            with patch.object(manage, 'call', side_effect=fake_call):
                legacy, current, dangling = manage.owned_images(root, selected_runtime())
            self.assertEqual(legacy, sorted(images[:2]))
            self.assertEqual(current, [images[2]])
            self.assertEqual(dangling, ['sha256:aaa'])

    def test_clean_images_keeps_current_cache_never_forces_and_refuses_active_session(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            identity = manage.checkout_id(root)
            legacy = f'localhost/romd-dev-{identity}-d170dab2-romd-admin:local'
            busy = f'localhost/romd-dev-{identity}-d170dab2-romd-worker:local'
            current = f'localhost/romd-dev-{identity}-romd-worker:local'
            fake_call, commands = self.fake_engine(root, [legacy, busy, current], ['sha256:old'], failing=[busy])
            with patch.object(manage, 'call', side_effect=fake_call), patch('sys.stdout', new=io.StringIO()) as output:
                self.assertTrue(manage.clean_images(root, dry_run=True, runtime=selected_runtime()))
                self.assertFalse([command for command in commands if command[1] == 'rmi'])
                self.assertFalse(manage.clean_images(root, runtime=selected_runtime()))
                with manage.session_lock(root), self.assertRaisesRegex(manage.SetupError, 'stop it before'):
                    manage.clean_images(root, runtime=selected_runtime())
            removed = [command for command in commands if command[1] == 'rmi']
            self.assertEqual(removed, [['podman', 'rmi', legacy], ['podman', 'rmi', busy], ['podman', 'rmi', 'sha256:old']])
            self.assertIn(f'kept {busy}', output.getvalue())
            self.assertIn('Kept 1 current checkout image(s)', output.getvalue())

    def test_rebuild_removes_only_replaced_images_that_lost_every_tag(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            worker, admin = (manage.checkout_image(root, name) for name in ('romd-worker', 'romd-admin'))
            fake_call, commands = self.fake_engine(root, [], tags={
                worker: 'sha256:new-worker', admin: 'sha256:same-admin',
                'repo-tags:sha256:old-worker': [], 'repo-tags:sha256:retagged': ['localhost/someone-else:keep']})
            with patch.object(manage, 'call', side_effect=fake_call):
                manage.remove_replaced_images(root, {'romd-worker': 'sha256:old-worker',
                                                     'romd-admin': 'sha256:same-admin',
                                                     'romd-consumer': None}, selected_runtime())
                manage.remove_replaced_images(root, {'romd-worker': 'sha256:retagged'}, selected_runtime())
            self.assertEqual([command for command in commands if command[1] == 'rmi'],
                             [['podman', 'rmi', 'sha256:old-worker']])


@unittest.skipIf(os.name == 'nt', 'Managed development uses POSIX process groups')
class CrashRecoveryTests(unittest.TestCase):
    def run_controller(self, root):
        runner_code = f'''
import sys
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0, {str(Path(manage.__file__).parent)!r})
import manage
root = Path({str(root)!r})
sleeper = [sys.executable, '-c', 'import time; print("host log line", flush=True); time.sleep(60)']
def start_hosts(root, directory, backend):
    for name in ('worker', 'admin', 'consumer'):
        backend.start(name, sleeper, cwd=root)
def source_start(root, directory, processes, backend, runtime):
    start_hosts(root, backend.directory, backend)
    processes.start('web-dependencies', sleeper, cwd=root)
    processes.start('web-admin', sleeper, cwd=root)
with patch.object(manage, 'check_tools'), patch.object(manage, 'check_containers'), \\
     patch.object(manage, 'check_web_dependencies'), patch.object(manage, 'check_ports'), \\
     patch.object(manage, 'start_source_hosts', side_effect=start_hosts), \\
     patch.object(manage, 'source_start', side_effect=source_start):
    try:
        manage.development('source', root)
    except KeyboardInterrupt:
        sys.exit(130)
'''
        log = (root / 'runner.log').open('w')
        self.addCleanup(log.close)
        runner = subprocess.Popen([sys.executable, '-c', runner_code], stdout=log,
                                  stderr=subprocess.STDOUT, start_new_session=True)
        def cleanup():
            if runner.poll() is None:
                runner.send_signal(signal.SIGINT)
                runner.wait(timeout=self.TIMEOUT)
        self.addCleanup(cleanup)
        return runner

    # Generous bounds: these run real subprocesses and the controller polls every 2s,
    # so parallel suites under load must not turn scheduling delay into failures.
    TIMEOUT = 60

    def wait_state(self, root, runner, state, previous=None, allow_exit=False):
        deadline = time.monotonic() + self.TIMEOUT
        while time.monotonic() < deadline:
            current = manage.session_status(root)
            if current['state'] == state and (previous is None or current.get('backendDirectory') != previous):
                return current
            if not allow_exit:
                self.assertIsNone(runner.poll(), (root / 'runner.log').read_text())
            time.sleep(0.05)
        self.fail(f'Timed out waiting for {state}: {(root / "runner.log").read_text()}')

    def wait_text(self, path, text):
        # Readiness in the fake controller does not wait for host output, so a
        # kill right after `ready` could precede the line the log tail must show.
        deadline = time.monotonic() + self.TIMEOUT
        while time.monotonic() < deadline:
            try:
                if text in path.read_text():
                    return
            except FileNotFoundError:
                pass
            time.sleep(0.02)
        self.fail(f'Timed out waiting for {text!r} in {path}')

    def test_backend_host_exit_after_ready_is_recoverable_and_keeps_web_servers(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'src').mkdir()
            runner = self.run_controller(root)
            ready = self.wait_state(root, runner, 'ready')
            web = ready['pids']['web-admin']
            dependencies = ready['pids']['web-dependencies']
            self.wait_text(Path(ready['backendDirectory']) / 'admin.log', 'host log line')
            os.killpg(ready['pids']['admin'], signal.SIGKILL)
            exited = self.wait_state(root, runner, 'backend-exited')
            self.assertTrue(exited['active'])
            self.assertEqual(exited['failedComponent'], 'admin')
            self.assertEqual(exited['log'], str(Path(ready['backendDirectory']) / 'admin.log'))
            self.assertIn('admin exited -9 after readiness', exited['error'])
            self.assertEqual(exited['pids'], {'web-admin': web, 'web-dependencies': dependencies})
            for name in ('worker', 'consumer'):
                self.assertFalse(manage.group_exists(ready['pids'][name]))
            output = (root / 'runner.log').read_text()
            self.assertIn('Backend host stopped: admin exited -9', output)
            self.assertIn('  host log line', output)
            with patch('sys.stdout', new=io.StringIO()) as requested:
                manage.request_restart(root)  # Content-identical recovery without editing source.
            self.assertIn('current state: backend-exited', requested.getvalue())
            recovered = self.wait_state(root, runner, 'ready', ready['backendDirectory'])
            self.assertNotIn('error', recovered)
            self.assertEqual(recovered['pids']['web-admin'], web)
            self.assertEqual(recovered['pids']['web-dependencies'], dependencies)
            self.assertEqual(set(recovered['pids']), {'worker', 'admin', 'consumer', 'web-admin', 'web-dependencies'})
            runner.send_signal(signal.SIGINT)
            self.assertEqual(runner.wait(timeout=self.TIMEOUT), 130)
            self.assertEqual(manage.session_status(root)['state'], 'stopped')
            self.assertFalse(manage.group_exists(dependencies))

    def test_web_server_or_dependency_watcher_exit_ends_session_and_joins_every_owned_group(self):
        for component in ('web-admin', 'web-dependencies'):
            with self.subTest(component=component), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                runner = self.run_controller(root)
                ready = self.wait_state(root, runner, 'ready')
                os.killpg(ready['pids'][component], signal.SIGKILL)
                self.assertEqual(runner.wait(timeout=self.TIMEOUT), 1)
                failed = manage.session_status(root)
                self.assertEqual(failed['state'], 'failed')
                self.assertEqual(failed['failedComponent'], component)
                self.assertEqual(failed['log'], str(Path(ready['runDirectory']) / (component + '.log')))
                self.assertIn(component + ' exited -9', failed['error'])
                self.assertEqual(failed['pids'], {})
                for pid in ready['pids'].values():
                    self.assertFalse(manage.group_exists(pid))

    def test_restart_request_requires_an_active_source_session(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with self.assertRaisesRegex(manage.SetupError, 'No active managed source run'):
                manage.request_restart(root)
            self.assertFalse((root / manage.RESTART_TRIGGER).exists())


if __name__ == '__main__':
    unittest.main()
