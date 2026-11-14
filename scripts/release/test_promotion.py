import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import urllib.error

import promotion
from promotion import (COMPLETION_JOB, Registry, check_candidate_release, compare_bundles, image_repositories,
                       qualified_candidate_run)

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = 'Owner/romd'
REVISION = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
DIGEST = 'sha256:' + 'a' * 64
SOURCE = json.loads((ROOT / 'native/chdman/mame.json').read_text())
SOURCE_NAME = SOURCE['sourceUrl'].rsplit('/', 1)[1]


def image_lines(owner='owner', digest=DIGEST):
    services = {f'ROMD_{name.upper()}_IMAGE': f'ghcr.io/{owner}/romd-{name}@{digest}'
                for name in ('admin', 'consumer', 'worker', 'player')}
    services['ROMD_POSTGRES_IMAGE'] = 'postgres:18.7-bookworm@' + DIGEST
    return ''.join(f'{key}={value}\n' for key, value in services.items())


def release_run(run_id, **changes):
    item = dict(id=run_id, head_sha=REVISION, head_branch='v1.2.3-rc.2', event='push', status='completed',
                conclusion='success', path='.github/workflows/release.yml', repository=dict(full_name=REPOSITORY))
    item.update(changes)
    return item


def candidate_release(**changes):
    names = ['romd-v1.2.3-rc.2-deployment.tar.gz', 'romd-v1.2.3-rc.2-deployment.tar.gz.sha256',
             'romd-v1.2.3-rc.2-verification.tar.gz', 'romd-v1.2.3-rc.2-verification.tar.gz.sha256', SOURCE_NAME]
    item = dict(draft=False, prerelease=True, target_commitish=REVISION, assets=[dict(name=name) for name in names])
    item.update(changes)
    return item


def checksum(path):
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    Path(str(path) + '.sha256').write_text(f'{digest}  {path.name}\n')


def repack(bundle, change):
    """Rewrite a bundle with a consistent manifest/checksum after `change(directory)`."""
    with tempfile.TemporaryDirectory() as temporary:
        stage = Path(temporary)
        with tarfile.open(bundle) as archive:
            archive.extractall(stage, filter='data')
        change(stage)
        manifest = json.loads((stage / 'manifest.json').read_text())
        manifest['files'] = {path.relative_to(stage).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                             for path in sorted(stage.rglob('*')) if path.is_file() and path.name != 'manifest.json'}
        (stage / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
        with tarfile.open(bundle, 'w:gz') as archive:
            archive.add(stage, arcname='.')
    checksum(bundle)


class Response(io.BytesIO):
    def __init__(self, body=b'', headers=None, status=200):
        super().__init__(body)
        self.headers, self.status = headers or {}, status

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


class FakeRegistry:
    """Registry double keyed by tag; records every request."""

    def __init__(self, manifest=b'{"schemaVersion":2}', tags=None, stored=None):
        self.manifest = manifest
        self.digest = 'sha256:' + hashlib.sha256(manifest).hexdigest()
        self.tags = dict(tags or {})
        self.stored = stored
        self.requests = []

    def open(self, request, timeout=None):
        url, method = request.full_url, request.get_method()
        self.requests.append((method, url, request.data))
        if url.startswith('https://ghcr.io/token?'):
            return Response(json.dumps(dict(token='bearer')).encode())
        reference = url.rsplit('/manifests/', 1)[1]
        if method == 'GET' and reference.startswith('sha256:'):
            return Response(self.manifest, {'Content-Type': 'application/vnd.oci.image.manifest.v1+json'})
        if method == 'HEAD':
            if reference not in self.tags:
                raise urllib.error.HTTPError(url, 404, 'missing', {}, io.BytesIO())
            return Response(headers={'Docker-Content-Digest': self.tags[reference]})
        if method == 'PUT':
            stored = self.stored or 'sha256:' + hashlib.sha256(request.data).hexdigest()
            self.tags[reference] = stored
            return Response(headers={'Docker-Content-Digest': stored}, status=201)
        raise urllib.error.HTTPError(url, 404, 'missing', {}, io.BytesIO())


class CandidateEvidenceTests(unittest.TestCase):
    def jobs(self, conclusion='success'):
        return dict(jobs=[dict(name='Downloaded Linux amd64 bundle and real-browser qualification', conclusion='success'),
                          dict(name=COMPLETION_JOB, conclusion=conclusion)])

    def test_only_a_completed_same_commit_candidate_release_run_qualifies(self):
        calls = []
        def get(path):
            calls.append(path)
            return self.jobs() if '/jobs?' in path else dict(workflow_runs=[release_run(1), release_run(2, conclusion='failure')])
        self.assertEqual(1, qualified_candidate_run(REPOSITORY, 'v1.2.3-rc.2', REVISION, get))
        self.assertIn('/actions/workflows/release.yml/runs?head_sha=' + REVISION, calls[0])
        for impostor in (release_run(1, head_branch='v1.2.3-rc.1'), release_run(1, head_sha='b' * 40),
                         release_run(1, event='workflow_dispatch'), release_run(1, status='in_progress'),
                         release_run(1, conclusion='cancelled'), release_run(1, path='.github/workflows/ci.yml'),
                         release_run(1, repository=dict(full_name='fork/romd'))):
            with self.subTest(impostor=impostor), self.assertRaisesRegex(ValueError, 'no successful release workflow run'):
                qualified_candidate_run(REPOSITORY, 'v1.2.3-rc.2', REVISION,
                                        lambda path: self.jobs() if '/jobs?' in path else dict(workflow_runs=[impostor]))
        for jobs in (self.jobs('failure'), self.jobs('skipped'), dict(jobs=[])):
            with self.subTest(jobs=jobs), self.assertRaises(ValueError):
                qualified_candidate_run(REPOSITORY, 'v1.2.3-rc.2', REVISION,
                                        lambda path: jobs if '/jobs?' in path else dict(workflow_runs=[release_run(1)]))

    def test_candidate_release_must_carry_exactly_the_qualified_public_assets(self):
        check_candidate_release(candidate_release(), 'v1.2.3-rc.2', REVISION, SOURCE_NAME)
        assets = candidate_release()['assets']
        for state in (None, candidate_release(draft=True), candidate_release(target_commitish='main'),
                      candidate_release(assets=assets[:-1]), candidate_release(assets=assets + [dict(name='images.env')]),
                      candidate_release(assets=[dict(name=item['name'].replace('rc.2', 'rc.1')) for item in assets])):
            with self.subTest(state=state), self.assertRaises(ValueError):
                check_candidate_release(state, 'v1.2.3-rc.2', REVISION, SOURCE_NAME)

    def test_candidate_images_must_be_this_repositorys_published_roles(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'images.env'
            path.write_text(image_lines())
            images = promotion.image_map(path)
        self.assertEqual(('owner/romd-admin', DIGEST), image_repositories(images, 'owner')['admin'])
        for changed in (dict(images, ROMD_ADMIN_IMAGE='ghcr.io/other/romd-admin@' + DIGEST),
                        dict(images, ROMD_ADMIN_IMAGE='ghcr.io/owner/romd-worker@' + DIGEST),
                        dict(images, ROMD_ADMIN_IMAGE='docker.io/owner/romd-admin@' + DIGEST)):
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                image_repositories(changed, 'owner')


class CandidateVerificationTests(unittest.TestCase):
    def test_candidate_is_verified_as_its_own_tag_by_the_release_workflow_identity(self):
        commands = []
        args = SimpleNamespace(repository=REPOSITORY, candidate='v1.2.3-rc.2', revision=REVISION)
        with tempfile.TemporaryDirectory() as temporary, \
                patch('promotion.qualified_candidate_run', return_value=9) as run_lookup, \
                patch('promotion.release_metadata', return_value=candidate_release()), \
                patch('promotion.unpack_archive') as unpack, \
                patch('promotion.run', side_effect=lambda command, capture=False: commands.append([str(part) for part in command])), \
                patch('promotion.check_command', side_effect=lambda command: commands.append([str(part) for part in command])):
            work = Path(temporary)
            run_id, bundle, images = promotion.verify_candidate(args, SOURCE_NAME, work)
        run_lookup.assert_called_once_with(REPOSITORY, 'v1.2.3-rc.2', REVISION)
        self.assertEqual((REVISION, 'v1.2.3-rc.2'), unpack.call_args.args[3:5])
        self.assertEqual((9, 'romd-v1.2.3-rc.2-deployment.tar.gz', 'images.env'), (run_id, bundle.name, images.name))
        download, verify, suite = commands
        self.assertEqual(['gh', 'release', 'download', 'v1.2.3-rc.2'], download[:4])
        self.assertTrue(verify[1].endswith('scripts/security/verify.py'))
        self.assertEqual('v1.2.3-rc.2', verify[verify.index('--tag') + 1])
        self.assertEqual(REVISION, verify[verify.index('--revision') + 1])
        self.assertEqual(REPOSITORY + '/.github/workflows/release.yml', verify[verify.index('--signer-workflow') + 1])
        self.assertIn('--verify-suite', suite)
        self.assertNotIn('--rehearsal', suite)
        self.assertEqual('v1.2.3-rc.2', suite[suite.index('--expected-tag') + 1])

    def test_public_verifiers_run_without_workflow_credentials_and_block_on_failure(self):
        with patch.dict(os.environ, GH_TOKEN='secret', GITHUB_TOKEN='secret'), \
                patch('promotion.subprocess.run', return_value=subprocess.CompletedProcess([], 3)) as run:
            with self.assertRaisesRegex(RuntimeError, 'verify.py rejected the candidate'):
                promotion.check_command(['python3', 'scripts/security/verify.py'])
        environment = run.call_args.kwargs['env']
        self.assertNotIn('GH_TOKEN', environment)
        self.assertNotIn('GITHUB_TOKEN', environment)

    def test_unqualified_candidate_is_rejected_before_any_download(self):
        args = SimpleNamespace(repository=REPOSITORY, candidate='v1.2.3-rc.2', revision=REVISION)
        with patch('promotion.qualified_candidate_run', side_effect=ValueError('no successful run')), \
                patch('promotion.run') as run, self.assertRaises(ValueError):
            promotion.verify_candidate(args, SOURCE_NAME, Path('unused'))
        run.assert_not_called()


class BundleEquivalenceTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.images = self.work / 'images.env'
        self.images.write_text(image_lines())
        self.candidate = self.work / 'romd-v1.2.3-rc.2-deployment.tar.gz'
        self.promoted = self.work / 'romd-v1.2.3-deployment.tar.gz'
        for bundle, tag in ((self.candidate, 'v1.2.3-rc.2'), (self.promoted, 'v1.2.3')):
            subprocess.run([ROOT / 'scripts/deployment/bundle.sh', self.images, bundle, REVISION, tag],
                           cwd=ROOT, check=True, capture_output=True)

    def compare(self, name):
        compare_bundles(self.candidate, self.promoted, self.images, REVISION, 'v1.2.3-rc.2', 'v1.2.3', self.work / name)

    def test_rebuilt_bundle_from_the_same_commit_differs_only_in_its_release_tag(self):
        (self.work / 'ok').mkdir()
        self.compare('ok')

    def test_any_installed_file_mode_or_manifest_change_is_refused(self):
        def edit_readme(stage):
            (stage / 'README.md').write_text((stage / 'README.md').read_text() + '\nchanged\n')
        def add_file(stage):
            (stage / 'extra.sh').write_text('true\n')
        def mode(stage):
            (stage / 'scripts/deployment/compose.sh').chmod(0o644)
        def manifest(stage):
            data = json.loads((stage / 'manifest.json').read_text())
            (stage / 'manifest.json').write_text(json.dumps(dict(data, platform='linux/amd64', extra=True)))
        for index, change in enumerate((edit_readme, add_file, mode, manifest)):
            with self.subTest(change=change.__name__):
                original = self.promoted.read_bytes()
                repack(self.promoted, change)
                (self.work / str(index)).mkdir()
                with self.assertRaises(ValueError):
                    self.compare(str(index))
                self.promoted.write_bytes(original)
                checksum(self.promoted)

    def test_bundle_for_other_images_or_tag_is_refused(self):
        other = self.work / 'other.env'
        other.write_text(image_lines(digest='sha256:' + 'b' * 64))
        subprocess.run([ROOT / 'scripts/deployment/bundle.sh', other, self.promoted, REVISION, 'v1.2.3'],
                       cwd=ROOT, check=True, capture_output=True)
        (self.work / 'images').mkdir()
        with self.assertRaises(ValueError):
            self.compare('images')
        subprocess.run([ROOT / 'scripts/deployment/bundle.sh', self.images, self.promoted, REVISION, 'v1.2.4'],
                       cwd=ROOT, check=True, capture_output=True)
        (self.work / 'tag').mkdir()
        with self.assertRaises(ValueError):
            self.compare('tag')


class RegistryTests(unittest.TestCase):
    def registry(self, fake):
        with patch.dict(os.environ, GH_TOKEN='token', GITHUB_ACTOR='actor'):
            return Registry('owner/romd-admin', fake)

    def test_tag_copies_the_exact_manifest_bytes_and_confirms_the_digest(self):
        fake = FakeRegistry()
        registry = self.registry(fake)
        self.assertIsNone(registry.tagged_digest('v1.2.3'))
        registry.tag(fake.digest, 'v1.2.3')
        put = [request for request in fake.requests if request[0] == 'PUT']
        self.assertEqual([('PUT', 'https://ghcr.io/v2/owner/romd-admin/manifests/v1.2.3', fake.manifest)], put)
        self.assertEqual(fake.digest, fake.tags['v1.2.3'])

    def test_mismatched_manifest_or_stored_digest_is_refused(self):
        fake = FakeRegistry()
        with self.assertRaisesRegex(ValueError, 'does not match the qualified digest'):
            self.registry(fake).tag('sha256:' + 'c' * 64, 'v1.2.3')
        self.assertFalse(any(request[0] == 'PUT' for request in fake.requests))
        fake = FakeRegistry(stored='sha256:' + 'd' * 64)
        with self.assertRaisesRegex(ValueError, 'different digest'):
            self.registry(fake).tag(fake.digest, 'v1.2.3')

    def test_registry_requires_workflow_credentials(self):
        with patch.dict(os.environ, {}, clear=True), self.assertRaises(ValueError):
            Registry('owner/romd-admin', FakeRegistry())


class PromotionOrderTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.source = self.work / SOURCE_NAME
        self.source.write_bytes(b'')
        images = self.work / 'publisher.env'
        images.write_text(image_lines('owner', FakeRegistry().digest))
        self.bundle = self.work / 'candidate/romd-v1.2.3-rc.2-deployment.tar.gz'
        self.bundle.parent.mkdir()
        subprocess.run([ROOT / 'scripts/deployment/bundle.sh', images, self.bundle, REVISION, 'v1.2.3-rc.2'],
                       cwd=ROOT, check=True, capture_output=True)
        self.publisher_images = images

    def promote(self, registries, metadata=None, **changes):
        commands = self.commands = []
        def run(command, capture=False):
            command = [str(part) for part in command]
            commands.append(command)
            if command[0].endswith('bundle.sh'):
                subprocess.run(command, cwd=ROOT, check=True, capture_output=True)
        args = SimpleNamespace(repository=REPOSITORY, tag='v1.2.3', revision=REVISION, candidate='v1.2.3-rc.2',
                               source_archive=self.source, output=self.work / 'output')
        vars(args).update(changes)
        args.output.mkdir(exist_ok=True)
        with patch('promotion.run', run), patch('promotion.check_source_archive'), \
                patch('promotion.verify_candidate', return_value=(77, self.bundle, self.publisher_images)), \
                patch('promotion.release_metadata', return_value=metadata), \
                patch('promotion.Registry', side_effect=lambda repository: registries[repository]):
            promotion.promote(args)
        return args, commands

    def registries(self, **tags):
        result = {}
        with patch.dict(os.environ, GH_TOKEN='token', GITHUB_ACTOR='actor'):
            for service in ('admin', 'consumer', 'worker', 'player'):
                fake = FakeRegistry(tags=tags.get(service))
                result[f'owner/romd-{service}'] = Registry(f'owner/romd-{service}', fake)
                result[f'owner/romd-{service}'].fake = fake
        return result

    def test_promotion_tags_exact_digests_and_publishes_an_unqualified_prerelease(self):
        registries = self.registries()
        args, commands = self.promote(registries)
        writes = [command[1:3] for command in commands if command[0] == 'gh']
        self.assertEqual([['release', 'create'], ['release', 'upload'], ['release', 'upload'], ['release', 'edit']], writes)
        create = next(command for command in commands if command[1:3] == ['release', 'create'])
        self.assertIn('--prerelease', create)
        self.assertIn('--draft', create)
        self.assertEqual(REVISION, create[create.index('--target') + 1])
        self.assertIn('Promoted without rebuilding from', (args.output / 'candidate-notes.txt').read_text())
        self.assertEqual(['--draft=false', '--prerelease'], commands[-1][-2:])
        for registry in registries.values():
            self.assertEqual(registry.fake.digest, registry.fake.tags['v1.2.3'])
        state = json.loads((args.output / 'candidate.json').read_text())
        self.assertEqual(('v1.2.3', 'v1.2.3-rc.2', 'draft-unqualified'),
                         (state['releaseTag'], state['promotedFrom'], state['result']))
        self.assertEqual((args.output / 'images.env').read_text(), self.publisher_images.read_text())
        self.assertEqual(hashlib.sha256((args.output / 'romd-v1.2.3-deployment.tar.gz').read_bytes()).hexdigest(),
                         state['bundleSha256'])

    def test_existing_tag_for_another_digest_blocks_every_public_write(self):
        registries = self.registries(worker={'v1.2.3': 'sha256:' + 'e' * 64})
        with self.assertRaisesRegex(ValueError, 'different digest'):
            self.promote(registries)
        self.assertFalse(any(command[0] == 'gh' for command in self.commands))
        self.assertFalse(any(request[0] == 'PUT' for registry in registries.values() for request in registry.fake.requests))

    def test_same_digest_tag_from_an_interrupted_attempt_is_reused(self):
        digest = FakeRegistry().digest
        registries = self.registries(admin={'v1.2.3': digest})
        _, commands = self.promote(registries, metadata=dict(draft=True, prerelease=True, target_commitish=REVISION, assets=[]))
        self.assertFalse(any(command[1:3] == ['release', 'create'] for command in commands))

    def test_exposed_or_partially_uploaded_release_blocks_promotion(self):
        for metadata in (dict(draft=False, prerelease=True, target_commitish=REVISION, assets=[]),
                         dict(draft=True, prerelease=True, target_commitish=REVISION, assets=[dict(name=SOURCE_NAME)])):
            with self.subTest(metadata=metadata):
                with self.assertRaises(ValueError):
                    self.promote(self.registries(), metadata=metadata)
                self.assertFalse(any(command[0] == 'gh' for command in self.commands))

    def test_source_archive_must_match_the_reviewed_gpl_pin(self):
        with self.assertRaisesRegex(ValueError, 'reviewed source pin'):
            promotion.check_source_archive(self.source)

    def test_only_stable_tags_promote_same_version_rc_candidates(self):
        for changes in (dict(tag='v1.2.3-rc.3'), dict(candidate='v1.2.4-rc.1'), dict(candidate='v1.2.3-beta.1'),
                        dict(candidate='v1.2.3'), dict(revision='main'), dict(repository='owner/romd;x')):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.promote(self.registries(), **changes)


if __name__ == '__main__':
    unittest.main()
