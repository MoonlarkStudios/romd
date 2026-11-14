import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from confirm_publication import confirm
from verify_release import PROVENANCE, SBOM, attestations_name, verify_release

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = 'owner/romd'
REVISION = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
SIGNER = REPOSITORY + '/.github/workflows/release.yml'
DIGEST = 'sha256:' + 'a' * 64


class Runner:
    def __init__(self, fail_on=None):
        self.calls, self.fail_on = [], fail_on

    def __call__(self, command, **kwargs):
        self.calls.append((command, kwargs))
        failed = self.fail_on is not None and self.fail_on in command
        return subprocess.CompletedProcess(command, 1 if failed else 0, '', 'verification failed' if failed else '')


class VerifyReleaseTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.images = self.work / 'images.env'
        self.images.write_text(''.join(f'ROMD_{name.upper()}_IMAGE=ghcr.io/owner/romd-{name}@{DIGEST}\n'
                                       for name in ('admin', 'consumer', 'worker', 'player'))
                               + f'ROMD_POSTGRES_IMAGE=postgres:18.7-bookworm@{DIGEST}\n')
        self.bundle = self.work / 'romd-v1.2.3-rc.1-deployment.tar.gz'
        subprocess.run([ROOT / 'scripts/deployment/bundle.sh', self.images, self.bundle, REVISION, 'v1.2.3-rc.1'],
                       cwd=ROOT, check=True, capture_output=True)
        self.checksum = Path(str(self.bundle) + '.sha256')
        self.attestations = self.work / attestations_name('v1.2.3-rc.1')
        self.attestations.write_text('{}\n')

    def verify(self, runner, tag='v1.2.3-rc.1', signer=SIGNER):
        return verify_release(self.bundle, self.checksum, self.images, self.attestations, REPOSITORY,
                              REVISION, tag, signer, runner=runner)

    def test_bundle_and_every_selected_image_need_provenance_and_sbom_from_the_release_workflow(self):
        runner = Runner()
        with patch.dict(os.environ, GH_TOKEN='secret', GITHUB_TOKEN='secret'):
            self.verify(runner)
        commands = [command for command, _ in runner.calls]
        self.assertEqual(12, len(commands))
        subjects = {command[3] for command in commands}
        self.assertEqual({str(self.bundle.resolve()), 'oci://docker.io/library/postgres@' + DIGEST}
                         | {f'oci://ghcr.io/owner/romd-{name}@{DIGEST}' for name in ('admin', 'consumer', 'worker', 'player')},
                         subjects)
        for subject in subjects:
            self.assertEqual({PROVENANCE, SBOM}, {command[-1] for command in commands if command[3] == subject})
        for command, kwargs in runner.calls:
            self.assertEqual(['gh', 'attestation', 'verify'], command[:3])
            for flag, value in (('--repo', REPOSITORY), ('--source-digest', REVISION), ('--signer-workflow', SIGNER),
                                ('--source-ref', 'refs/tags/v1.2.3-rc.1'), ('--bundle', str(self.attestations.resolve()))):
                self.assertEqual(value, command[command.index(flag) + 1])
            self.assertIn('--deny-self-hosted-runners', command)
            self.assertNotIn('GH_TOKEN', kwargs['env'])
            self.assertNotIn('GITHUB_TOKEN', kwargs['env'])

    def test_any_failed_attestation_blocks_and_names_the_subject(self):
        with self.assertRaisesRegex(ValueError, '(?s)romd-worker.*verification failed'):
            self.verify(Runner(fail_on='oci://ghcr.io/owner/romd-worker@' + DIGEST))

    def test_bundle_identity_is_checked_before_any_attestation_call(self):
        runner = Runner()
        for tag, signer in (('v1.2.3-rc.2', SIGNER), ('v1.2.3-rc.1', 'owner/romd/.github/workflows/ci.yml'),
                            ('v1.2.3-rc.1', 'other/romd/.github/workflows/release.yml')):
            with self.subTest(tag=tag, signer=signer), self.assertRaises(ValueError):
                self.verify(runner, tag, signer)
        self.images.write_text(self.images.read_text().replace('a' * 64, 'b' * 64))
        with self.assertRaises(ValueError):
            self.verify(runner)
        self.assertEqual([], runner.calls)


class GhDouble:
    def __init__(self, state, verify_failures=0, asset_failure=None):
        self.state, self.verify_failures, self.asset_failure, self.calls = state, verify_failures, asset_failure, []

    def __call__(self, command, **kwargs):
        self.calls.append(command)
        if command[1:3] == ['release', 'view']:
            return subprocess.CompletedProcess(command, 0, json.dumps(self.state), '')
        if command[1:3] == ['release', 'verify']:
            if self.verify_failures:
                self.verify_failures -= 1
                return subprocess.CompletedProcess(command, 1, '', 'attestation not found')
            return subprocess.CompletedProcess(command, 0, '', '')
        if command[1:3] == ['release', 'verify-asset']:
            failed = self.asset_failure is not None and command[4].endswith(self.asset_failure)
            return subprocess.CompletedProcess(command, int(failed), '', 'digest mismatch' if failed else '')
        raise AssertionError(command)


class ConfirmPublicationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        work = Path(temporary.name)
        self.assets = []
        for name, data in (('romd-v1.2.3-deployment.tar.gz', b'bundle'), ('romd-v1.2.3-deployment.tar.gz.sha256', b'sum'),
                           ('romd-v1.2.3-attestations.jsonl', b'{}\n')):
            (work / name).write_bytes(data)
            self.assets.append(work / name)

    def state(self, **changes):
        assets = [dict(name=path.name, size=path.stat().st_size) for path in self.assets]
        assets.append(dict(name='mame0289.tar.gz', size=213237316))
        value = dict(isDraft=False, isImmutable=True, isPrerelease=True, assets=assets)
        value.update(changes)
        return value

    def confirm(self, gh):
        confirm('owner/romd', 'v1.2.3', self.assets, 'mame0289.tar.gz', runner=gh, sleep=lambda seconds: None)

    def test_published_immutable_release_attests_every_verified_asset(self):
        gh = GhDouble(self.state(), verify_failures=2)
        self.confirm(gh)
        self.assertEqual(3, sum(command[1:3] == ['release', 'verify'] for command in gh.calls))
        self.assertEqual([str(path) for path in self.assets],
                         [command[4] for command in gh.calls if command[1:3] == ['release', 'verify-asset']])

    def test_mutable_draft_or_mismatched_releases_fail(self):
        extra = self.state()['assets'] + [dict(name='images.env', size=1)]
        resized = [dict(asset, size=asset['size'] + 1) if asset['name'].endswith('.jsonl') else asset
                   for asset in self.state()['assets']]
        for changes, message in ((dict(isImmutable=False), 'without immutability'), (dict(isDraft=True), 'not a published'),
                                 (dict(isPrerelease=False), 'not a published'), (dict(assets=extra), 'differ'),
                                 (dict(assets=self.state()['assets'][:-1]), 'differ'), (dict(assets=resized), 'differ')):
            with self.subTest(changes=changes), self.assertRaisesRegex(ValueError, message):
                self.confirm(GhDouble(self.state(**changes)))

    def test_missing_release_attestation_or_changed_asset_fails(self):
        with self.assertRaisesRegex(ValueError, 'attestation not found'):
            self.confirm(GhDouble(self.state(), verify_failures=99))
        with self.assertRaisesRegex(ValueError, 'digest mismatch'):
            self.confirm(GhDouble(self.state(), asset_failure='attestations.jsonl'))


if __name__ == '__main__':
    unittest.main()
