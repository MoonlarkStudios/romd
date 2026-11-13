"""Publication binding and rejection controls; native runtime proof belongs to CI."""
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

from qualify import verify_bundle
from publication import publish
from rehearsal import Commands, candidate_images, cleanup_registry, local_json, rehearse
from rehearsal_evidence import ROLES, verify_rehearsal, verify_published_bundle
from suite import CASES, CHECKS, REPORTS, STATES, digest, run_suite, verify_suite


REVISION, TAG = 'a' * 40, 'v1.2.3-rc.1'
PINS = {'schemaVersion': 1, 'postgres': 'postgres:18-bookworm@sha256:' + 'b' * 64,
        'ingress': 'nginx@sha256:' + 'c' * 64}
IDS = {role: 'sha256:' + str(index) * 64 for index, role in enumerate(ROLES, 1)}


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value) + '\n')


def package(root, images, *, extra=b''):
    image_file = root / 'images.env'
    image_file.write_text(''.join(f'{key}={value}\n' for key, value in images.items()))
    files = {name: b'fixture' + extra for name in ('compose.yaml', '.env.example',
        'scripts/deployment/compose.sh', 'scripts/deployment/validate.sh', 'scripts/deployment/verify.sh',
        'scripts/backup/backup.sh', 'scripts/backup/restore.sh', 'deploy/examples/https/compose.yaml')}
    files['images.env'] = image_file.read_bytes()
    manifest = dict(schemaVersion=1, sourceCommit=REVISION, releaseTag=TAG, platform='linux/amd64', images=images,
                    files={name: hashlib.sha256(data).hexdigest() for name, data in files.items()})
    bundle = root / 'rehearsal-bundle.tar.gz'
    with tarfile.open(bundle, 'w:gz') as archive:
        for name, data in dict(files, **{'manifest.json': json.dumps(manifest).encode()}).items():
            member = tarfile.TarInfo(name)
            member.size, member.mode = len(data), 0o755
            archive.addfile(member, io.BytesIO(data))
    checksum = Path(str(bundle) + '.sha256')
    checksum.write_text(f'{digest(bundle)}  {bundle.name}\n')
    state = root / 'candidate.json'
    write_json(state, dict(sourceCommit=REVISION, releaseTag=TAG, images=images, bundleSha256=digest(bundle)))
    return SimpleNamespace(bundle=bundle, checksum=checksum, expected_images=image_file, publisher_state=state,
                           expected_revision=REVISION, expected_tag=TAG, rehearsal=True, verify_only=False, inject_fault='none')


def fixture(root):
    images = {f'ROMD_{role.upper()}_IMAGE': f'127.0.0.1:51234/romd-rehearsal-abcd/romd-{role}@sha256:' + str(index) * 64
              for index, role in enumerate(ROLES, 1)}
    images['ROMD_POSTGRES_IMAGE'] = PINS['postgres']
    args = package(root, images)
    with tempfile.TemporaryDirectory() as temporary:
        bound = verify_bundle(args.bundle, args.checksum, args.expected_images, Path(temporary) / 'bundle', REVISION, TAG)
    running = {'romd-' + role: dict(reference=images[f'ROMD_{role.upper()}_IMAGE'], imageId=IDS[role], platform='linux/amd64') for role in ROLES}
    running['postgres'] = dict(reference=PINS['postgres'], imageId='sha256:' + '9' * 64, platform='linux/amd64')
    lifecycle = dict(result='passed', complete=True, errors=[], cleanupErrors=[])
    reports = {}
    for case in CASES:
        positive = case == 'positive'
        browser = dict(result='passed' if positive else 'failed', failures=0 if positive else 1,
                       checks={key: True for key in CHECKS}, persistedState={key: 'd' * 64 for key in STATES})
        qualification = dict(result=browser['result'], evidenceType='disposable-rehearsal', bundle=bound,
                             runningImages=running, lifecycle=lifecycle, injectedFault='none' if positive else case,
                             commands=[dict(stage='cleanup-disposable-project', exitCode=0)])
        if positive:
            qualification['restoredRunningImages'] = running
            for phase in ('after-restart', 'after-restore'):
                reports[f'positive/browser-{phase}/browser-diagnostics.json'] = copy.deepcopy(browser)
        else:
            browser.update(injectedFault=case, records=[dict(kind='injected-fault', fault=case),
                dict(kind='required-response-failed', status=503, surface='admin' if case == 'portal-asset' else 'player')])
            browser['checks']['playerGameStarted'] = False
        reports[f'{case}/qualification.json'] = qualification
        reports[f'{case}/lifecycle.json'] = lifecycle
        reports[f'{case}/browser-initial/browser-diagnostics.json'] = browser
    packaged = root / 'packaged'
    for name, report in reports.items():
        write_json(packaged / name, report)
    summary = dict(schemaVersion=1, result='passed', evidenceType='disposable-rehearsal', sourceCommit=REVISION,
                   releaseTag=TAG, bundleSha256=digest(args.bundle), images=images,
                   files={name: digest(packaged / name) for name in REPORTS})
    write_json(packaged / 'suite.json', summary)
    receipt = dict(schemaVersion=1, result='passed', evidenceType='disposable-rehearsal', sourceCommit=REVISION,
                   releaseTag=TAG, platform='linux/amd64', imageIds=IDS, finalImageIds=IDS,
                   dependencies=PINS, registryCleanup='passed', registryExit=dict(exitCode=0, oomKilled=False),
                   images=images, bundleSha256=digest(args.bundle), suiteSha256=digest(packaged / 'suite.json'),
                   payloadFiles={name: value for name, value in bound['files'].items() if name != 'images.env'})
    path = root / 'receipt.json'
    write_json(path, receipt)
    return args, path, receipt


class RehearsalEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.args, self.path, self.receipt = fixture(self.root)

    def test_complete_bound_rehearsal_accepts_only_registry_name_change(self):
        verified = verify_rehearsal(self.path, REVISION, TAG, IDS, PINS)
        published = self.root / 'public'
        published.mkdir()
        images = {key: ('ghcr.io/owner/' + value.split('/', 2)[-1] if key != 'ROMD_POSTGRES_IMAGE' else value)
                  for key, value in verified['images'].items()}
        args = package(published, images)
        verify_published_bundle(verified, args.bundle, args.expected_images, REVISION, TAG)
        # Self-consistent new archives still cannot substitute installation code.
        args = package(published, images, extra=b'changed installation')
        with self.assertRaisesRegex(ValueError, 'payload differs'):
            verify_published_bundle(verified, args.bundle, args.expected_images, REVISION, TAG)
        images['ROMD_WORKER_IMAGE'] = 'ghcr.io/owner/romd-worker@sha256:' + 'e' * 64
        args = package(published, images)
        with self.assertRaisesRegex(ValueError, 'digest differs'):
            verify_published_bundle(verified, args.bundle, args.expected_images, REVISION, TAG)

    def test_missing_failed_foreign_or_incomplete_receipt_cannot_unlock_publication(self):
        with self.assertRaises(ValueError):
            verify_rehearsal(None, REVISION, TAG, IDS, PINS)
        for mutation in (dict(result='failed'), dict(evidenceType='disposable-release'), dict(sourceCommit='e' * 40),
                         dict(releaseTag='v9.9.9'), dict(platform='linux/arm64'), dict(registryCleanup='failed'),
                         dict(imageIds={}), dict(finalImageIds={}), dict(registryExit=dict(exitCode=2, oomKilled=False)),
                         dict(dependencies={}), dict(payloadFiles={}), dict(suiteSha256='f' * 64)):
            with self.subTest(mutation=mutation):
                write_json(self.path, dict(self.receipt, **mutation))
                with self.assertRaises(ValueError):
                    verify_rehearsal(self.path, REVISION, TAG, IDS, PINS)
        write_json(self.path, self.receipt)
        with self.assertRaises(ValueError):
            verify_rehearsal(self.path, REVISION, TAG, dict(IDS, worker='sha256:' + 'f' * 64), PINS)
        self.args.bundle.write_bytes(self.args.bundle.read_bytes() + b'tampered')
        with self.assertRaises(ValueError):
            verify_rehearsal(self.path, REVISION, TAG, IDS, PINS)

    def test_rehearsal_evidence_never_qualifies_a_published_release(self):
        self.args.rehearsal = False
        with self.assertRaisesRegex(ValueError, 'evidence type'):
            verify_suite(self.root / 'packaged', self.args)

    def test_publication_cannot_write_before_receipt_and_payload_binding_pass(self):
        source = self.root / 'source.tar.gz'
        source.write_bytes(b'synthetic worker source')
        write_json(self.root / 'native/chdman/mame.json', dict(sourceBytes=source.stat().st_size,
                   sourceSha256=digest(source)))
        write_json(self.root / 'scripts/deployment/base-images.json', PINS)
        output = self.root / 'public'
        output.mkdir()
        args = SimpleNamespace(repository='owner/repo', tag=TAG, revision=REVISION, source_archive=source,
                               output=output, rehearsal=None)
        commands = []
        def command(argv, capture=False):
            argv = [str(part) for part in argv]
            commands.append(argv)
            if argv[:3] == ['docker', 'image', 'inspect']:
                role = argv[3].removeprefix('romd-').removesuffix(':test')
                return json.dumps([dict(Id=IDS[role], Os='linux', Architecture='amd64', Config=dict(Labels={
                    'org.opencontainers.image.revision': REVISION, 'org.opencontainers.image.version': TAG,
                    'org.opencontainers.image.source': 'https://github.com/owner/repo'}))])
            if argv[0].endswith('bundle.sh'):
                images = dict(line.split('=', 1) for line in Path(argv[1]).read_text().splitlines())
                changed = package(output, images, extra=b'changed after rehearsal')
                target = Path(argv[2])
                target.write_bytes(changed.bundle.read_bytes())
                Path(str(target) + '.sha256').write_text(f'{digest(target)}  {target.name}\n')
                return
            self.fail('Publication wrote before binding passed: ' + repr(argv))
        with patch('publication.ROOT', self.root), patch('publication.run', side_effect=command), \
                patch('publication.release_metadata') as lookup, patch('publication.registry_exists') as registry:
            with self.assertRaisesRegex(ValueError, 'requires a passed'):
                publish(args)
            args.rehearsal = self.path
            write_json(self.path, dict(self.receipt, result='failed'))
            with self.assertRaisesRegex(ValueError, 'does not match'):
                publish(args)
            write_json(self.path, self.receipt)
            with self.assertRaisesRegex(ValueError, 'payload differs'):
                publish(args)
            lookup.assert_not_called()
            registry.assert_not_called()
        self.assertFalse(any(argv[:2] in (['docker', 'push'], ['gh', 'release']) for argv in commands))

    def test_partial_identity_reset_false_negative_and_dirty_lifecycle_are_rejected(self):
        packaged = self.root / 'packaged'
        original = {name: (packaged / name).read_bytes() for name in REPORTS + ['suite.json']}
        mutations = (
            ('positive/browser-after-restore/browser-diagnostics.json', lambda r: r['persistedState'].update(contentSha256='f' * 64)),
            ('positive/browser-initial/browser-diagnostics.json', lambda r: r['checks'].update(playerBodyPlaying=False)),
            ('positive/lifecycle.json', lambda r: r.update(errors=['nonzero-container-exit'])),
            ('player-startup/browser-initial/browser-diagnostics.json', lambda r: r.update(records=[])),
            ('portal-asset/qualification.json', lambda r: r.update(bundle={})),
        )
        for name, mutate in mutations:
            with self.subTest(name=name):
                for path, data in original.items():
                    (packaged / path).write_bytes(data)
                report = json.loads(original[name])
                mutate(report)
                write_json(packaged / name, report)
                summary = json.loads(original['suite.json'])
                summary['files'][name] = digest(packaged / name)
                write_json(packaged / 'suite.json', summary)
                with self.assertRaises(ValueError):
                    verify_suite(packaged, self.args)
        for path, data in original.items():
            (packaged / path).write_bytes(data)
        (packaged / REPORTS[0]).unlink()
        with self.assertRaises(OSError):
            verify_suite(packaged, self.args)

    def test_suite_runs_all_three_cases_and_rejects_unexpected_negative_success(self):
        templates = self.root / 'packaged'
        def execute(args, work):
            case = 'positive' if args.inject_fault == 'none' else args.inject_fault
            seen.append(case)
            for name in REPORTS:
                if name.startswith(case + '/') and not name.endswith('/qualification.json'):
                    write_json(work / name.split('/', 1)[1], json.loads((templates / name).read_text()))
            return json.loads((templates / case / 'qualification.json').read_text())
        seen = []
        target = self.root / 'run'
        target.mkdir()
        self.assertEqual('passed', run_suite(self.args, target, run_case=execute)['result'])
        self.assertEqual(list(CASES), seen)
        report = json.loads((templates / 'portal-asset/qualification.json').read_text())
        report['result'] = 'passed'
        write_json(templates / 'portal-asset/qualification.json', report)
        seen.clear()
        target = self.root / 'false-negative'
        target.mkdir()
        self.assertEqual('failed', run_suite(self.args, target, run_case=execute)['result'])
        self.assertEqual(['positive', 'portal-asset'], seen)


class DisposableRegistryTests(unittest.TestCase):
    def test_non_native_engine_fails_before_staging_with_isolated_credentials(self):
        stages = []
        def command(instance, argv, stage, **kwargs):
            stages.append(stage)
            self.assertNotIn('GH_TOKEN', instance.env)
            self.assertNotIn('GITHUB_TOKEN', instance.env)
            self.assertNotIn('DOCKER_AUTH_CONFIG', instance.env)
            self.assertEqual([], list(Path(instance.env['DOCKER_CONFIG']).iterdir()))
            return json.dumps(dict(Os='linux', Arch='amd64', Components=[dict(Name='Podman Engine')]))
        with tempfile.TemporaryDirectory() as temporary, \
                patch.dict(os.environ, GH_TOKEN='private', GITHUB_TOKEN='private', DOCKER_AUTH_CONFIG='private'), \
                patch.object(Commands, 'run', command):
            receipt = rehearse(Path(temporary) / 'evidence', REVISION, TAG)
        self.assertEqual('failed', receipt['result'])
        self.assertEqual(['native-docker'], stages)

    def test_no_registry_request_can_escape_loopback(self):
        for url in ('https://ghcr.io/v2/', 'http://localhost:5000/v2/', 'http://127.0.0.1:5000/token',
                    'http://127.0.0.1:5000@evil.test/v2/'):
            with self.subTest(url=url), self.assertRaises(ValueError):
                local_json(url)

    def test_image_validation_rejects_stale_platform_labels_or_id(self):
        image = dict(Id=IDS['admin'], Os='linux', Architecture='amd64', Config=dict(Labels={
            'org.opencontainers.image.revision': REVISION, 'org.opencontainers.image.version': TAG}))
        self.assertEqual(set(ROLES), set(candidate_images(lambda *a: json.dumps([image]), REVISION, TAG)))
        for key, value in (('Architecture', 'arm64'), ('Id', 'mutable'), ('Config', {'Labels': {}})):
            with self.subTest(key=key), self.assertRaises(ValueError):
                candidate_images(lambda *a: json.dumps([dict(image, **{key: value})]), REVISION, TAG)

    def test_cleanup_continues_after_error_and_refuses_foreign_resources(self):
        for foreign in (False, True):
            seen = []
            def command(argv, stage):
                seen.append(stage)
                if stage == 'registry-owner':
                    return json.dumps([{'Config': {'Labels': {'io.romd.rehearsal': 'other' if foreign else 'owned'}}}])
                if stage == 'registry-exit':
                    return json.dumps([{'State': {'ExitCode': 143, 'OOMKilled': False}}])
                if stage == 'registry-data-owner':
                    return json.dumps([{'Labels': {'io.romd.rehearsal': 'owned'}}])
                if stage == 'remove-owned-alias' and argv[-1] == 'first':
                    raise RuntimeError('synthetic remove error')
                return ''
            errors = cleanup_registry(command, 'container', 'volume', ['first', 'second'], 'owned')
            self.assertEqual(2, len(errors))
            self.assertEqual(2, seen.count('remove-owned-alias'))
            self.assertIn('remove-registry-data', seen)
            self.assertEqual(not foreign, 'remove-registry' in seen)

    def test_registry_exit_state_is_retained_and_only_clean_exit_passes(self):
        for code, oom in ((0, False), (2, False), (0, True)):
            def command(argv, stage):
                return json.dumps([{'Config': {'Labels': {'io.romd.rehearsal': 'owned'}},
                                    'State': {'ExitCode': code, 'OOMKilled': oom}}])
            receipt = {}
            errors = cleanup_registry(command, 'container', None, [], 'owned', receipt)
            self.assertEqual(dict(exitCode=code, oomKilled=oom), receipt['registryExit'])
            self.assertEqual(code != 0 or oom, bool(errors))
