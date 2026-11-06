import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import runtime


class RuntimeTests(unittest.TestCase):
    def selected(self, engine='podman', **values):
        return runtime.Runtime({'ROMD_CONTAINER_ENGINE': engine, 'PATH': '/tools', **values})

    def setUp(self):
        self.which = patch.object(runtime.shutil, 'which', side_effect=lambda name, **_: name if name.startswith('/') else '/tools/' + name)
        self.which.start()
        self.addCleanup(self.which.stop)

    def test_ambiguous_engines_require_selection_and_missing_selection_never_falls_back(self):
        with self.assertRaisesRegex(runtime.ContainerError, 'Select ROMD_CONTAINER_ENGINE'):
            runtime.Runtime({'PATH': '/tools'})
        with patch.object(runtime.shutil, 'which', side_effect=lambda name, **_: '/tools/podman' if name == 'podman' else None):
            self.assertEqual('podman', runtime.Runtime({'PATH': '/tools'}).engine)
            with self.assertRaisesRegex(runtime.ContainerError, 'no fallback'):
                self.selected('docker')

    def test_failed_runtime_probe_never_falls_back_or_exposes_tool_stderr(self):
        failed = subprocess.CompletedProcess([], 1, stdout='', stderr='private-token')
        with patch.object(runtime.subprocess, 'run', return_value=failed) as command:
            with self.assertRaises(runtime.ContainerError) as error:
                self.selected().info()
        self.assertNotIn('private-token', str(error.exception))
        self.assertEqual(1, command.call_count)
        self.assertEqual('podman', command.call_args.args[0][0])

    def test_podman_endpoint_and_reaper_settings_are_scoped_to_children(self):
        environment = {'ROMD_CONTAINER_ENGINE': 'podman', 'PATH': '/tools', 'DOCKER_CONTEXT': 'unrelated'}
        selected = runtime.Runtime(environment)
        with patch.object(selected, 'info', return_value={'socket': '/run/user/123/podman/podman.sock'}), \
                patch.object(runtime, 'socket_version', return_value={'Components': [{'Name': 'Podman Engine'}]}):
            child = selected.child_environment(tests=True)
        self.assertEqual('unix:///run/user/123/podman/podman.sock', child['DOCKER_HOST'])
        self.assertEqual('/run/user/123/podman/podman.sock', child['TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE'])
        self.assertNotIn('DOCKER_CONTEXT', child)
        self.assertNotIn('TESTCONTAINERS_RYUK_DISABLED', child)
        self.assertEqual('unrelated', environment['DOCKER_CONTEXT'])
        self.assertNotIn('DOCKER_HOST', environment)

    def test_mismatched_engine_and_reaper_endpoints_are_rejected(self):
        selected = self.selected()
        with patch.object(selected, 'info', return_value={'socket': '/podman.sock'}), \
                patch.object(runtime, 'socket_version', return_value={'Components': [{'Name': 'Engine'}]}):
            with self.assertRaisesRegex(runtime.ContainerError, 'another engine'):
                selected.child_environment(tests=True)
        selected = self.selected(TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE='/other.sock')
        with patch.object(selected, 'info', return_value={'socket': '/podman.sock'}), \
                patch.object(runtime, 'socket_version', return_value={'Components': [{'Name': 'Podman Engine'}]}):
            with self.assertRaisesRegex(runtime.ContainerError, 'disagrees'):
                selected.child_environment(tests=True)
        with self.assertRaisesRegex(runtime.ContainerError, 'cleanup must stay enabled'):
            self.selected('docker', TESTCONTAINERS_RYUK_DISABLED='true').child_environment(tests=True)

    def test_another_podman_instance_is_rejected_before_probing_or_running_compose(self):
        selected = self.selected(DOCKER_HOST='unix:///other/podman.sock')
        with patch.object(selected, 'info', return_value={'socket': '/native/podman.sock'}), \
                patch.object(runtime, 'socket_version', return_value={'Components': [{'Name': 'Podman Engine'}]}) as probe:
            with self.assertRaisesRegex(runtime.ContainerError, 'different Podman instance'):
                selected.child_environment(tests=True)
            with self.assertRaisesRegex(runtime.ContainerError, 'different Podman instance'):
                selected.engine_command('volume', 'create', 'owned-volume')
            probe.assert_not_called()

    def test_socket_path_aliases_resolve_to_the_native_instance_for_compose_and_tests(self):
        with tempfile.TemporaryDirectory() as directory:
            native = Path(directory) / 'podman.sock'
            native.touch()
            alias = Path(directory) / 'alias.sock'
            alias.symlink_to(native)
            selected = self.selected(DOCKER_HOST='unix://' + str(alias),
                                     TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=str(alias))
            with patch.object(selected, 'info', return_value={'socket': str(native)}), \
                    patch.object(runtime, 'socket_version', return_value={'Components': [{'Name': 'Podman Engine'}]}) as probe:
                environment = selected.child_environment(tests=True)
            self.assertEqual(environment['DOCKER_HOST'], 'unix://' + str(native.resolve()))
            self.assertEqual(environment['TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE'], str(native.resolve()))
            probe.assert_called_once_with(str(native.resolve()))

    def test_docker_preserves_explicit_context_and_tls_configuration(self):
        selected = self.selected('docker', DOCKER_CONTEXT='desktop-linux', DOCKER_TLS_VERIFY='1')
        self.assertEqual(selected.environment, selected.child_environment(tests=True))

    def test_docker_info_is_normalized_without_replacing_its_context(self):
        selected = self.selected('docker', DOCKER_CONTEXT='desktop-linux')
        with patch.object(runtime, 'capture', return_value=json.dumps({'ServerVersion': '29.1.3'})) as probe:
            self.assertEqual({'engine': 'docker', 'version': '29.1.3'}, selected.info())
        self.assertEqual('desktop-linux', probe.call_args.args[1]['DOCKER_CONTEXT'])

    def test_plugin_fallback_is_docker_only_when_standalone_compose_is_missing(self):
        with patch.object(runtime.shutil, 'which', side_effect=lambda name, **_: None if name == 'docker-compose' else '/tools/' + name):
            self.assertEqual(['docker', 'compose'], self.selected('docker').compose_command())
            with self.assertRaisesRegex(runtime.ContainerError, 'mise install'):
                self.selected('podman').compose_command()

    def test_offline_compose_client_resolves_the_provider_without_probing_the_engine(self):
        with patch.object(runtime, 'capture') as probe, patch.object(runtime, 'socket_version') as api_probe:
            self.assertEqual(['/tools/docker-compose', 'config'], self.selected('podman').compose_client('config'))
            probe.assert_not_called()
            api_probe.assert_not_called()
        with patch.object(runtime.shutil, 'which', side_effect=lambda name, **_: None if name == 'docker-compose' else '/tools/' + name):
            self.assertEqual(['docker', 'compose', 'config'], self.selected('docker').compose_client('config'))
            with self.assertRaisesRegex(runtime.ContainerError, 'mise install'):
                self.selected('podman').compose_client('config')

    def test_podman_machine_and_rootful_podman_fail_with_supported_path(self):
        for remote, rootless in ((True, True), (False, False)):
            info = {'host': {'os': 'linux', 'serviceIsRemote': remote, 'security': {'rootless': rootless}},
                    'version': {'Version': '5.8.7'}}
            with patch.object(runtime, 'capture', return_value=json.dumps(info)):
                with self.assertRaisesRegex(runtime.ContainerError, 'native rootless Linux'):
                    self.selected().info()

    def test_one_runtime_reuses_its_validated_instance_and_returns_independent_environments(self):
        selected = self.selected(DOCKER_CONTEXT='unrelated')
        info = {'host': {'os': 'linux', 'security': {'rootless': True},
                         'remoteSocket': {'path': '/podman.sock'}}, 'version': {'Version': '5.8.7'}}
        with patch.object(runtime, 'capture', return_value=json.dumps(info)) as native_probe, \
                patch.object(runtime, 'socket_version', return_value={'Components': [{'Name': 'Podman Engine'}]}) as api_probe:
            selected.info()
            self.assertEqual(selected.engine_command('volume', 'ls'), ['podman', 'volume', 'ls'])
            self.assertEqual(selected.compose_command('ps'), ['/tools/docker-compose', 'ps'])
            child = selected.child_environment(tests=True)
            child['DOCKER_HOST'] = 'unix:///foreign.sock'
            self.assertEqual(selected.child_environment()['DOCKER_HOST'], 'unix:///podman.sock')
            native_probe.assert_called_once()
            api_probe.assert_called_once_with('/podman.sock')

    def test_builder_selection_is_explicit_for_podman_and_preserved_for_docker(self):
        selected = self.selected(DOCKER_BUILDKIT='1')
        with patch.object(selected, 'info', return_value={'socket': '/podman.sock'}), \
                patch.object(runtime, 'socket_version', return_value={'Components': [{'Name': 'Podman Engine'}]}):
            self.assertEqual(selected.child_environment()['DOCKER_BUILDKIT'], '0')
        self.assertEqual(self.selected('docker', DOCKER_BUILDKIT='1').child_environment()['DOCKER_BUILDKIT'], '1')
        self.assertNotIn('DOCKER_BUILDKIT', self.selected('docker').child_environment())


if __name__ == '__main__':
    unittest.main()
