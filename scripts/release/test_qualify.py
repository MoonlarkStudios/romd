#!/usr/bin/env python3
"""Deterministic archive/digest/environment guards; no Docker or publication."""
import hashlib
import io
import json
import os
from contextlib import chdir, redirect_stdout, contextmanager
from pathlib import Path
import ssl
import subprocess
import sys
import tarfile
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch

import qualify as qualification
import compose_images
from qualify import IMAGE_KEYS, Runner, deployment_environment, image_map, quick_start, verify_bundle
from assert_failure import assert_injected_failure


ROOT_DIR = 'romd-v1.2.3-rc.1'

TRUSTED_AUTHORITY = '-----BEGIN CERTIFICATE-----\ndHJ1c3RlZA==\n-----END CERTIFICATE-----\n'
UNRELATED_AUTHORITY = '-----BEGIN CERTIFICATE-----\ndW5yZWxhdGVk\n-----END CERTIFICATE-----\n'
SERVED_CERTIFICATE = '-----BEGIN CERTIFICATE-----\nc2VydmVk\n-----END CERTIFICATE-----\n'


class RecordingLifecycle:
    """Flow fixture: real collector protocol is covered by deployment tests."""
    def __init__(self, env, projects, image, evidence):
        self.events = []
        self.evidence = evidence
        self.receipt = None

    @contextmanager
    def monitor(self, cleanups):
        self.events.append('monitor-start')
        try:
            yield self
        finally:
            for project, stop, down in cleanups:
                self.events.append('cleanup-' + project)
                try:
                    stop()
                finally:
                    down()
            self.receipt = {'result': 'passed', 'complete': True, 'errors': [], 'cleanupErrors': []}
            self.evidence.write_text(json.dumps(self.receipt))
            self.events.append('monitor-finish')

    @contextmanager
    def stopping(self, project, services=None):
        self.events.append('stopping-' + project)
        yield

    def checkpoint(self, project):
        self.events.append('checkpoint-' + project)

    def diagnostics(self, project):
        self.events.append('diagnostics-' + project)


class BundleQualificationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.work = Path(self.temporary.name)
        self.archive = self.work / 'romd-v1.2.3-rc.1-deployment.tar.gz'
        self.checksum = Path(str(self.archive) + '.sha256')
        self.images = self.work / 'expected-images.env'
        self.entries = {name: f'ghcr.io/example/{name.lower()}@sha256:{"a" * 64}' for name in IMAGE_KEYS}
        self.images.write_text(''.join(f'{name}={value}\n' for name, value in sorted(self.entries.items())))
        self.files = {name: b'fixture' for name in ('compose.yaml', '.env.example',
            'scripts/deployment/compose.sh', 'scripts/deployment/validate.sh', 'scripts/deployment/verify.sh',
            'scripts/backup/backup.sh', 'scripts/backup/restore.sh', 'deploy/ingress/templates/default.conf.template',
            'deploy/examples/https/compose.yaml')}
        self.files['images.env'] = self.images.read_bytes()
        self.files['compose.yaml'] = compose_images.render(
            ''.join(f'image: {compose_images.required(key)}\n' for key in compose_images.KEYS), self.entries).encode()

    def bundle(self, manifest_changes=None, extra=None):
        manifest = dict(schemaVersion=1, sourceCommit='a' * 40, releaseTag='v1.2.3-rc.1',
                        platform='linux/amd64', images=self.entries,
                        files={name: hashlib.sha256(value).hexdigest() for name, value in self.files.items()})
        manifest.update(manifest_changes or {})
        files = dict(self.files, **{'manifest.json': json.dumps(manifest).encode()})
        with tarfile.open(self.archive, 'w:gz') as archive:
            for name, value in files.items():
                member = tarfile.TarInfo(f'{ROOT_DIR}/{name}')
                member.size, member.mode = len(value), 0o755
                archive.addfile(member, io.BytesIO(value))
            if extra:
                member, value = extra
                archive.addfile(member, io.BytesIO(value))
        self.checksum.write_text(f'{hashlib.sha256(self.archive.read_bytes()).hexdigest()}  {self.archive.name}\n')

    def verify(self):
        return verify_bundle(self.archive, self.checksum, self.images, self.work / 'extracted', 'a' * 40, 'v1.2.3-rc.1')

    def test_exact_downloaded_bundle_extracts_executable_tools(self):
        self.bundle()
        result = self.verify()
        self.assertEqual(self.entries, result['images'])
        self.assertTrue((self.work / 'extracted/scripts/deployment/compose.sh').stat().st_mode & 0o100)

    def test_public_bundle_directories_remain_readable_under_private_umask(self):
        self.bundle()
        previous = os.umask(0o077)
        try:
            self.verify()
        finally:
            os.umask(previous)
        root = self.work / 'extracted'
        self.assertEqual(0o700, root.stat().st_mode & 0o777)
        for directory in root.rglob('*'):
            if directory.is_dir():
                self.assertEqual(0o755, directory.stat().st_mode & 0o777, directory)

    def test_tampered_download_is_refused_before_extraction(self):
        self.bundle()
        self.archive.write_bytes(self.archive.read_bytes() + b'tampered')
        with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
            self.verify()
        self.assertFalse((self.work / 'extracted').exists())

    def test_revision_tag_platform_inventory_and_hashes_fail_closed(self):
        for mutation in ({'sourceCommit': 'b' * 40}, {'releaseTag': 'v9.9.9'}, {'platform': 'linux/arm64'},
                         {'schemaVersion': 2}, {'files': {}}, {'files': {'compose.yaml': 'b' * 64}}):
            with self.subTest(mutation=mutation):
                self.bundle(mutation)
                with self.assertRaises(ValueError):
                    self.verify()

    def test_self_consistent_bundle_cannot_substitute_publisher_digests(self):
        self.bundle({'images': dict(self.entries, ROMD_PLAYER_IMAGE='ghcr.io/example/player@sha256:' + 'b' * 64)})
        with self.assertRaisesRegex(ValueError, 'published image map mismatch'):
            self.verify()

    def test_traversal_absolute_duplicate_and_link_members_fail(self):
        for name, kind in (('../outside', tarfile.REGTYPE), ('/outside', tarfile.REGTYPE),
                           (f'{ROOT_DIR}/compose.yaml', tarfile.REGTYPE), (f'{ROOT_DIR}/linked', tarfile.SYMTYPE),
                           (f'{ROOT_DIR}/hardlinked', tarfile.LNKTYPE), (f'{ROOT_DIR}/device', tarfile.CHRTYPE)):
            with self.subTest(name=name):
                member = tarfile.TarInfo(name)
                member.type, member.linkname, member.size = kind, '../outside', 0
                self.bundle(extra=(member, b''))
                with self.assertRaises(ValueError):
                    self.verify()

    def test_members_outside_the_release_directory_are_refused(self):
        for name in ('compose.override.yaml', './README.md', 'romd-v1.2.3-rc.2/compose.yaml', 'romd-v1.2.3-rc.1x/a'):
            with self.subTest(name=name):
                member = tarfile.TarInfo(name)
                member.size = 0
                self.bundle(extra=(member, b''))
                with self.assertRaisesRegex(ValueError, f'inside {ROOT_DIR}/'):
                    self.verify()

    def test_unmanifested_extra_file_is_refused(self):
        member = tarfile.TarInfo(f'{ROOT_DIR}/unexpected-secret.env')
        member.size = 6
        self.bundle(extra=(member, b'secret'))
        with self.assertRaisesRegex(ValueError, 'file inventory'):
            self.verify()

    def test_compose_image_defaults_must_match_the_published_image_map(self):
        published = self.files['compose.yaml'].decode()
        for changed in (published.replace('a' * 64, 'b' * 64, 1),
                        published.replace('${ROMD_WORKER_IMAGE:-', '${ROMD_WORKER_IMAGE:?', 1),
                        published + 'image: ${ROMD_ADMIN_IMAGE:-ghcr.io/other/admin@sha256:' + 'c' * 64 + '}\n',
                        'image: fixture\n'):
            self.files['compose.yaml'] = changed.encode()
            self.bundle()
            with self.subTest(changed=changed[-80:]), self.assertRaisesRegex(ValueError, 'compose.yaml'):
                self.verify()

    def test_payload_ignores_only_the_image_references(self):
        self.bundle()
        first = self.verify()
        self.assertNotIn('images.env', first['payload'])
        self.assertNotEqual(first['files']['compose.yaml'], first['payload']['compose.yaml'])
        # The same source published from another registry: only images.env and the compose defaults differ.
        self.entries = {name: f'registry.example/{name.lower()}@sha256:{"d" * 64}' for name in IMAGE_KEYS}
        self.images.write_text(''.join(f'{name}={value}\n' for name, value in sorted(self.entries.items())))
        self.files['images.env'] = self.images.read_bytes()
        self.files['compose.yaml'] = compose_images.render(
            ''.join(f'image: {compose_images.required(key)}\n' for key in compose_images.KEYS), self.entries).encode()
        self.bundle()
        self.work = self.work / 'second'
        self.work.mkdir()
        second = self.verify()
        self.assertEqual(first['payload'], second['payload'])
        self.files['compose.yaml'] += b'# changed installation content\n'
        self.bundle()
        self.work = self.work / 'third'
        self.work.mkdir()
        self.assertNotEqual(first['payload'], self.verify()['payload'])

    def test_image_map_rejects_duplicates_mutable_and_unknown_keys(self):
        original = self.images.read_text()
        for added in ('ROMD_PLAYER_IMAGE=tag:latest\n', 'UNEXPECTED=value\n',
                      'ROMD_PLAYER_IMAGE=' + self.entries['ROMD_PLAYER_IMAGE'] + '\n'):
            with self.subTest(added=added):
                self.images.write_text(original + added)
                with self.assertRaises(ValueError):
                    image_map(self.images)

    def test_first_start_is_the_quick_start_with_settings_only_in_the_env_file(self):
        bundle = self.work / 'bundle'
        bundle.mkdir(exist_ok=True)
        environment = deployment_environment(self.work, bundle, 'nginx@sha256:' + 'f' * 64)
        runner = Mock(bundle=bundle)
        with patch.dict('os.environ', {'ROMD_HOST': 'operator.example', 'COMPOSE_FILE': '/operator/compose.yaml'}):
            quick_start(runner, environment)
        (command, stage), options = runner.run.call_args.args, runner.run.call_args.kwargs
        self.assertEqual((['docker', 'compose', 'up', '-d', '--wait', '--wait-timeout', '240'], 'quick-start'), (command, stage))
        # Nothing reaches Compose through the process environment: no wrapper, image map or settings.
        self.assertFalse([key for key in options['env'] if key.startswith(('ROMD_', 'COMPOSE_'))])
        written = dict(line.split('=', 1) for line in (bundle / '.env').read_text().splitlines())
        self.assertEqual(0o600, (bundle / '.env').stat().st_mode & 0o777)
        # The three values the quick start asks for, plus what keeps this disposable copy isolated.
        self.assertLessEqual({'ROMD_HOST', 'ROMD_DEFAULT_ADMIN_EMAIL', 'ROMD_DEFAULT_ADMIN_PASSWORD',
                              'ROMD_BIND', 'COMPOSE_PROJECT_NAME'}, set(written))
        self.assertFalse({'ROMD_ENV_FILE', 'COMPOSE_FILE'} & set(written))
        self.assertFalse([key for key in written if key.endswith('_IMAGE') and key != 'ROMD_NGINX_IMAGE'])
        self.assertEqual(f"'{environment['ROMD_DEFAULT_ADMIN_PASSWORD']}'", written['ROMD_DEFAULT_ADMIN_PASSWORD'])
        with self.assertRaises(ValueError):
            quick_start(runner, dict(environment, ROMD_HOST="nas'.local"))

    def test_disposable_environment_does_not_inherit_operator_state(self):
        with patch.dict('os.environ', {'ROMD_ADMIN_IMAGE': 'evil:mutable', 'COMPOSE_PROFILES': 'operator',
                                      'COMPOSE_PROJECT_NAME': 'production', 'ROMD_ENV_FILE': '/private/operator.env'}):
            environment = deployment_environment(self.work, self.work / 'bundle', 'nginx@sha256:' + 'f' * 64)
        self.assertNotIn('ROMD_ADMIN_IMAGE', environment)
        self.assertNotIn('COMPOSE_PROFILES', environment)
        self.assertRegex(environment['COMPOSE_PROJECT_NAME'], '^romd-qualify-[0-9a-f]{12}$')
        self.assertRegex(environment['ROMD_BIND'], '^127\\.')
        self.assertEqual(str(self.work / 'empty.env'), environment['ROMD_ENV_FILE'])
        # The default installation: a host name and the first administrator; ROMD supplies the rest.
        self.assertEqual('romd.example.test', environment['ROMD_HOST'])
        self.assertEqual(str(self.work / 'bundle/compose.yaml'), environment['COMPOSE_FILE'])
        self.assertFalse([key for key in environment if key.endswith(('_PUBLIC_URL', '_SECRET'))
                          or key in ('ROMD_TLS_DIRECTORY', 'ROMD_ADMIN_HOST')])
        self.assertEqual(['ROMD_DEFAULT_ADMIN_PASSWORD'], [key for key in environment if key.endswith('_PASSWORD')])

    def test_disposable_ingress_network_avoids_every_existing_docker_network(self):
        import ipaddress
        taken = [ipaddress.ip_network(value) for value in ('10.253.16.0/24', '10.253.0.0/19', '172.28.0.0/24', 'fd00::/64')]
        environment = deployment_environment(self.work, self.work / 'bundle', 'nginx@sha256:' + 'f' * 64, taken)
        subnet = ipaddress.ip_network(environment['ROMD_EDGE_SUBNET'])
        self.assertEqual('10.253.32.0/24', str(subnet))
        self.assertFalse(any(subnet.overlaps(other) for other in taken if other.version == 4))
        addresses = [ipaddress.ip_address(environment[key]) for key in
                     ('ROMD_TRUSTED_PROXY', 'ROMD_ADMIN_EDGE_IP', 'ROMD_CONSUMER_EDGE_IP', 'ROMD_PLAYER_EDGE_IP')]
        dynamic = ipaddress.ip_network(environment['ROMD_EDGE_DYNAMIC_RANGE'])
        self.assertEqual(4, len(set(addresses)))
        self.assertTrue(all(address in subnet and address not in dynamic for address in addresses))
        with self.assertRaisesRegex(RuntimeError, 'No free private subnet'):
            deployment_environment(self.work, self.work / 'bundle', 'nginx@sha256:' + 'f' * 64,
                                   [ipaddress.ip_network('10.0.0.0/8')])

    def test_cli_writes_new_local_evidence_and_refuses_directory_reuse(self):
        self.bundle()
        evidence = self.work / 'evidence'
        command = [sys.executable, str(Path(__file__).with_name('qualify.py')), '--verify-only',
                   '--bundle', str(self.archive), '--checksum', str(self.checksum),
                   '--expected-images', str(self.images), '--expected-revision', 'a' * 40,
                   '--expected-tag', 'v1.2.3-rc.1', '--evidence-dir', str(evidence)]
        state = self.work / 'candidate.json'
        state.write_text(json.dumps(dict(sourceCommit='a' * 40, releaseTag='v1.2.3-rc.1',
            images=image_map(self.images), bundleSha256=hashlib.sha256(self.archive.read_bytes()).hexdigest())))
        command += ['--publisher-state', str(state)]
        first = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(0, first.returncode, first.stderr)
        report = json.loads((evidence / 'qualification.json').read_text())
        self.assertEqual('verified-bundle-only', report['result'])
        self.assertEqual('local', report['evidenceType'])
        self.assertNotEqual(0, subprocess.run(command, capture_output=True).returncode)

    def test_negative_drill_cannot_pass_on_missing_browser_or_earlier_startup_failure(self):
        evidence = self.work / 'negative-evidence'
        (evidence / 'browser-initial').mkdir(parents=True)
        (evidence / 'qualification.json').write_text(json.dumps({'result': 'failed', 'injectedFault': 'player-startup',
            'runningImages': {str(number): {} for number in range(5)},
            'commands': [{'stage': 'cleanup-disposable-project', 'exitCode': 0}],
            'lifecycle': {'result': 'passed', 'complete': True}}))
        (evidence / 'lifecycle.json').write_text(json.dumps(
            {'result': 'passed', 'complete': True, 'errors': [], 'cleanupErrors': []}))
        browser = {'result': 'failed', 'injectedFault': 'player-startup', 'checks': {},
                   'records': [{'kind': 'qualification-stage-failed', 'stage': 'launch-browser'}]}
        diagnostics = evidence / 'browser-initial/browser-diagnostics.json'
        diagnostics.write_text(json.dumps(browser))
        with self.assertRaisesRegex(ValueError, 'deliver the requested fault'):
            assert_injected_failure(evidence, 'player-startup')
        browser['records'] += [{'kind': 'injected-fault', 'fault': 'player-startup'},
                               {'kind': 'required-response-failed', 'status': 503, 'surface': 'external'}]
        diagnostics.write_text(json.dumps(browser))
        with self.assertRaisesRegex(ValueError, 'post-handshake startup'):
            assert_injected_failure(evidence, 'player-startup')
        browser['checks'] = {key: True for key in ('adminCallback', 'adminAuthenticatedView',
                            'consumerCallback', 'consumerAuthenticatedView', 'playerHandshake')}
        diagnostics.write_text(json.dumps(browser))
        assert_injected_failure(evidence, 'player-startup')
        lifecycle = evidence / 'lifecycle.json'
        for mutation in ({'result': 'failed'}, {'complete': False}, {'errors': ['nonzero-container-exit']},
                         {'cleanupErrors': ['restore-down-failed']}):
            lifecycle.write_text(json.dumps(dict({'result': 'passed', 'complete': True, 'errors': [], 'cleanupErrors': []}, **mutation)))
            with self.assertRaisesRegex(ValueError, 'complete clean container lifecycle'):
                assert_injected_failure(evidence, 'player-startup')
        lifecycle.write_text(json.dumps({'result': 'passed', 'complete': True, 'errors': [], 'cleanupErrors': []}))
        qualification = json.loads((evidence / 'qualification.json').read_text())
        qualification['commands'][0]['exitCode'] = 1
        (evidence / 'qualification.json').write_text(json.dumps(qualification))
        with self.assertRaisesRegex(ValueError, 'clean up'):
            assert_injected_failure(evidence, 'player-startup')

    def test_relative_evidence_survives_real_browser_failure_from_extracted_bundle_cwd(self):
        self.bundle()
        state = self.work / 'candidate.json'
        state.write_text(json.dumps(dict(sourceCommit='a' * 40, releaseTag='v1.2.3-rc.1',
            images=image_map(self.images), bundleSha256=hashlib.sha256(self.archive.read_bytes()).hexdigest())))
        # Exercise the production browser mkdir/diagnostics in a real Node process.
        # Only Chromium launch is replaced, so this needs neither Docker nor a browser.
        browser_entry = self.work / 'browser-entry.mjs'
        browser_entry.write_text(
            "import { readFile, writeFile } from 'node:fs/promises';\n"
            f"import {{ qualifyBrowser }} from {json.dumps((qualification.HERE / 'browser/run.mjs').as_uri())};\n"
            "const config = JSON.parse(await readFile(process.argv[3], 'utf8'));\n"
            "const passed = await qualifyBrowser(config, { launch() { throw new Error('fixture launch failure'); } });\n"
            "await writeFile(`${config.evidenceDir}/process-location.json`, JSON.stringify({ cwd: process.cwd(), evidenceDir: config.evidenceDir }));\n"
            "process.exitCode = passed ? 0 : 1;\n")
        run_process = Runner.run

        def run(runner, command, stage, *args, **kwargs):
            if stage == 'browser-initial':
                return run_process(runner, [command[0], browser_entry, *command[2:]], stage, *args, **kwargs)
            if stage == 'docker-version':
                return json.dumps({'Os': 'linux', 'Arch': 'amd64'})
            if kwargs.get('binary_result'):
                return b'fixture-public-key'
            if stage == 'generated-authority':
                return TRUSTED_AUTHORITY
            if stage == 'control-authority':
                (runner.work / 'control-authority.crt').write_text(UNRELATED_AUTHORITY)
            return ''

        relative = Path('coverage/packaged/positive')
        argv = ['qualify.py', '--bundle', str(self.archive), '--checksum', str(self.checksum),
                '--expected-images', str(self.images), '--publisher-state', str(state),
                '--expected-revision', 'a' * 40, '--expected-tag', 'v1.2.3-rc.1',
                '--nginx-image', 'nginx@sha256:' + 'f' * 64, '--evidence-dir', str(relative)]
        def handshake(address, port, host, authority):
            if authority != TRUSTED_AUTHORITY:
                raise ssl.SSLCertVerificationError('fixture: unable to get local issuer certificate')
            return ssl.PEM_cert_to_DER_cert(SERVED_CERTIFICATE)

        with chdir(self.work), patch.object(sys, 'argv', argv), patch.object(Runner, 'run', new=run), \
                patch.object(qualification, 'served_certificate', new=handshake), \
                patch.object(Runner, 'inspect_images', return_value={}), \
                patch.object(qualification, 'ContainerLifecycle', RecordingLifecycle), redirect_stdout(io.StringIO()):
            self.assertEqual(1, qualification.main())
        evidence = self.work / relative
        report = json.loads((evidence / 'qualification.json').read_text())
        browser = json.loads((evidence / 'browser-initial/browser-diagnostics.json').read_text())
        location = json.loads((evidence / 'browser-initial/process-location.json').read_text())
        self.assertEqual('failed', report['result'])
        self.assertIn('browser-initial failed (exit 1)', report['failure'])
        self.assertEqual('failed', browser['result'])
        self.assertTrue(any(record.get('stage') == 'launch-browser' for record in browser['records']))
        self.assertEqual(str((evidence / 'browser-initial').resolve()), location['evidenceDir'])
        self.assertNotEqual(str(self.work.resolve()), location['cwd'])
        self.assertEqual('bundle', Path(location['cwd']).name)
        self.assertEqual(1, next(command for command in report['commands'] if command['stage'] == 'browser-initial')['exitCode'])

    def packaged_flow(self, failure=None):
        self.bundle()
        state = self.work / 'publisher.json'
        state.write_text(json.dumps(dict(sourceCommit='a' * 40, releaseTag='v1.2.3-rc.1',
            images=self.entries, bundleSha256=hashlib.sha256(self.archive.read_bytes()).hexdigest())))
        evidence = self.work / f'flow-evidence-{failure or "success"}'
        evidence.mkdir()
        instances, stages = [], []
        resumed = {'starting': False, 'ready': False, 'checkpointed': False}
        def factory(*args):
            gate = RecordingLifecycle(*args)
            checkpoint = gate.checkpoint
            def admit(project):
                if project == args[0]['COMPOSE_PROJECT_NAME'] and resumed['starting']:
                    self.assertTrue(resumed['ready'], 'Lifecycle admission must follow backup-resume readiness')
                    if failure == 'checkpoint':
                        raise RuntimeError('Backup resume lifecycle failed')
                    resumed['checkpointed'] = True
                checkpoint(project)
            gate.checkpoint = admit
            instances.append(gate)
            return gate
        def run(runner, command, stage, **kwargs):
            stages.append(stage)
            if stage == 'docker-version':
                return json.dumps({'Os': 'linux', 'Arch': 'amd64'})
            if stage == 'packaged-backup':
                backup = runner.work / 'backups/fixture'
                backup.mkdir(parents=True)
                (backup / 'manifest.json').write_text('{}')
                resumed['starting'] = True
            if stage == 'backup-resume-readiness':
                self.assertEqual([runner.bundle / 'scripts/deployment/compose.sh', 'up', '-d', '--wait',
                                  '--wait-timeout', '300'], command)
                if failure == 'readiness':
                    raise RuntimeError('Backup resume readiness failed')
                resumed['ready'] = True
            if stage == 'stop-source-before-restore':
                self.assertTrue(resumed['ready'], 'Stopping newly resumed hosts can exit 143 before startup finishes')
                self.assertTrue(resumed['checkpointed'], 'Resume lifecycle must pass before the source is stopped')
                resumed['starting'] = False
            if stage == 'generated-authority':
                return TRUSTED_AUTHORITY
            if stage == 'control-authority':
                (runner.work / 'control-authority.crt').write_text(UNRELATED_AUTHORITY)
            return b'public-key' if kwargs.get('binary_result') else ''
        phases = []
        def handshake(address, port, host, authority):
            self.assertEqual('romd.example.test', host)
            self.assertRegex(address, '^127\\.')
            phase = 'restore' if any(stage == 'packaged-restore' for stage in stages) else 'source'
            phases.append((phase, port, authority == TRUSTED_AUTHORITY))
            # The fake installation serves under the trusted authority, unless a restore replaced it.
            issuer = UNRELATED_AUTHORITY if failure == 'unsound-control' else TRUSTED_AUTHORITY
            if failure == 'authority' and phase == 'restore':
                issuer = 'another authority'
            if authority != issuer and failure != 'unsound-control':
                raise ssl.SSLCertVerificationError('fixture: unable to get local issuer certificate')
            return ssl.PEM_cert_to_DER_cert(SERVED_CERTIFICATE)
        arguments = SimpleNamespace(bundle=self.archive, checksum=self.checksum,
            expected_images=self.images, publisher_state=state, expected_revision='a' * 40,
            expected_tag='v1.2.3-rc.1', verify_only=False, inject_fault='none',
            nginx_image='nginx@sha256:' + 'f' * 64)
        with patch.object(qualification, 'ContainerLifecycle', side_effect=factory), \
                patch.object(Runner, 'run', new=run), patch.object(Runner, 'browser'), \
                patch.object(qualification, 'served_certificate', new=handshake), \
                patch.object(Runner, 'inspect_images', return_value={key: {} for key in self.entries}):
            report = qualification.qualify(arguments, evidence)
        self.handshakes = phases
        return report, instances[0].events, stages

    def test_packaged_flow_keeps_lifecycle_through_backup_restore_and_both_cleanups(self):
        report, events, stages = self.packaged_flow()
        self.assertEqual('passed', report['result'])
        self.assertEqual('passed', report['lifecycle']['result'])
        self.assertEqual('monitor-start', events[0])
        self.assertEqual('monitor-finish', events[-1])
        self.assertEqual(4, sum(value.startswith('checkpoint-') for value in events))
        self.assertEqual(2, sum(value.startswith('cleanup-') for value in events))
        self.assertIn('packaged-backup', stages)
        self.assertIn('packaged-restore', stages)
        self.assertIn('cleanup-restored-project', stages)
        self.assertIn('cleanup-disposable-project', stages)
        self.assertLess(stages.index('cleanup-restored-stop'), stages.index('cleanup-restored-project'))

    def test_served_certificate_requires_the_given_authority_and_host_name_over_real_tls(self):
        import http.server
        import threading

        def openssl(*arguments):
            subprocess.run(['openssl', *arguments], check=True, capture_output=True, cwd=self.work)

        ec = ('-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-nodes')
        for name in ('trusted', 'unrelated'):
            # The same subject on both: only the key distinguishes the wrong issuer.
            openssl('req', '-x509', *ec, '-keyout', f'{name}.key', '-out', f'{name}.crt', '-days', '2',
                    '-subj', '/CN=ROMD local authority (romd.example.test)', '-addext', 'basicConstraints=critical,CA:TRUE')
        openssl('req', '-new', *ec, '-keyout', 'server.key', '-out', 'server.csr', '-subj', '/CN=ROMD server certificate',
                '-addext', 'subjectAltName=DNS:romd.example.test')
        openssl('x509', '-req', '-in', 'server.csr', '-CA', 'trusted.crt', '-CAkey', 'trusted.key', '-days', '2',
                '-copy_extensions', 'copy', '-out', 'server.crt')
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(self.work / 'server.crt', self.work / 'server.key')
        server = http.server.HTTPServer(('127.0.0.1', 0), http.server.BaseHTTPRequestHandler)
        server.handle_error = lambda *arguments: None  # refused handshakes are the point of this test
        server.socket = context.wrap_socket(server.socket, server_side=True)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(lambda: (server.shutdown(), server.server_close(), thread.join(5)))
        port = server.server_address[1]
        trusted, unrelated = ((self.work / f'{name}.crt').read_text() for name in ('trusted', 'unrelated'))

        served = qualification.served_certificate('127.0.0.1', port, 'romd.example.test', trusted)
        self.assertEqual(ssl.PEM_cert_to_DER_cert((self.work / 'server.crt').read_text()), served)
        with self.assertRaises(ssl.SSLCertVerificationError):
            qualification.served_certificate('127.0.0.1', port, 'romd.example.test', unrelated)
        with self.assertRaises(ssl.SSLCertVerificationError):
            qualification.served_certificate('127.0.0.1', port, 'other.example.test', trusted)

    def test_every_phase_verifies_live_tls_against_the_original_authority_before_pinning(self):
        report, _, stages = self.packaged_flow()
        self.assertEqual('passed', report['result'])
        self.assertRegex(report['certificateAuthoritySha256'], '^[0-9a-f]{64}$')
        self.assertEqual(1, stages.count('generated-authority'), 'the authority is copied once, as a person would')
        self.assertNotIn('fixture-certificate', stages)
        # Three surfaces with the trusted authority, then one wrong-issuer control, in each of three phases.
        self.assertEqual([True, True, True, False] * 3, [trusted for _, _, trusted in self.handshakes])
        self.assertEqual(['source'] * 8 + ['restore'] * 4, [phase for phase, _, _ in self.handshakes])
        records = [command for command in report['commands'] if command['stage'].startswith('trusted-tls-')]
        self.assertEqual(['trusted-tls-initial', 'trusted-tls-after-restart', 'trusted-tls-after-restore'],
                         [record['stage'] for record in records])
        self.assertTrue(all(record['result'] == 'trusted' and record['wrongIssuerRejected'] for record in records))
        for phase in ('initial', 'after-restart', 'after-restore'):
            self.assertIn(f'certificate-spki-{phase}', stages)

    def test_restore_that_serves_under_another_authority_fails_before_the_browser_is_pinned(self):
        report, _, stages = self.packaged_flow('authority')
        self.assertEqual('failed', report['result'])
        self.assertEqual('after-restore: the served certificate does not chain to the authority devices trust',
                         report['failure'])
        self.assertNotIn('certificate-spki-after-restore', stages)

    def test_verification_that_accepts_an_unrelated_authority_fails_qualification(self):
        report, _, stages = self.packaged_flow('unsound-control')
        self.assertEqual('failed', report['result'])
        self.assertEqual('initial: TLS verification accepted an unrelated authority', report['failure'])
        self.assertNotIn('certificate-spki-initial', stages)

    def test_backup_resume_readiness_or_lifecycle_failure_blocks_source_stop_and_restore(self):
        for failure in ('readiness', 'checkpoint'):
            with self.subTest(failure=failure):
                report, events, stages = self.packaged_flow(failure)
                self.assertEqual('failed', report['result'])
                self.assertIn('Backup resume', report['failure'])
                self.assertNotIn('stop-source-before-restore', stages)
                self.assertNotIn('packaged-restore', stages)
                self.assertEqual(2, sum(value.startswith('cleanup-') for value in events))

    def test_running_container_requires_exact_published_image_id_digest_and_architecture(self):
        reference = self.entries['ROMD_ADMIN_IMAGE']
        image = {'Os': 'linux', 'Architecture': 'amd64', 'RepoDigests': [reference], 'Id': 'sha256:expected'}
        container = {'Image': image['Id'], 'Config': {'Image': reference}}
        runner = Runner(self.work, self.work / 'evidence', self.work / 'bundle', {'COMPOSE_PROJECT_NAME': 'source'})
        restore_env = {'COMPOSE_PROJECT_NAME': 'restore'}
        with patch.object(runner, 'run', side_effect=[json.dumps([image]), 'container-id\n', json.dumps([container])]) as run:
            actual = runner.inspect_images({'ROMD_ADMIN_IMAGE': reference}, env=restore_env)
            self.assertEqual(reference, actual['romd-admin']['reference'])
            self.assertTrue(all(call.kwargs['env'] == restore_env for call in run.call_args_list))
        for changed_image, changed_container in (
                (dict(image, Architecture='arm64'), container),
                (dict(image, RepoDigests=['ghcr.io/substitute@sha256:' + 'b' * 64]), container),
                (image, dict(container, Image='sha256:substituted')),
                (image, dict(container, Config={'Image': 'mutable:latest'}))):
            with self.subTest(image=changed_image, container=changed_container):
                with patch.object(runner, 'run', side_effect=[json.dumps([changed_image]), 'container-id\n', json.dumps([changed_container])]):
                    with self.assertRaises(RuntimeError):
                        runner.inspect_images({'ROMD_ADMIN_IMAGE': reference})


if __name__ == '__main__':
    unittest.main()
