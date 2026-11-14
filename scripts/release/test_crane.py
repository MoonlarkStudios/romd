import gc
import os
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

from crane import Crane, image_repositories

DIGEST = 'sha256:' + 'a' * 64
OTHER = 'sha256:' + 'e' * 64


class MemoryCrane:
    """Caller-level double for Crane: tags keyed by (repository, tag); records reads and writes."""

    def __init__(self, tags=None):
        self.tags = dict(tags or {})
        self.reads, self.writes = [], []

    def digest(self, reference):
        self.reads.append(reference)
        repository, tag = reference.removeprefix('ghcr.io/').rsplit(':', 1)
        return self.tags.get((repository, tag))

    def tag(self, repository, digest, tag):
        self.writes.append((repository, digest, tag))
        self.tags[(repository, tag)] = digest


class CraneCli:
    """Command-level double for the crane binary, using the error lines crane 0.22.1 prints."""

    def __init__(self, tags=None, digest_error=None, tag_stores=None, login_status=0):
        self.tags = dict(tags or {})
        self.digest_error, self.tag_stores, self.login_status = digest_error, tag_stores, login_status
        self.calls = []

    def __call__(self, command, **kwargs):
        self.calls.append((command, kwargs))
        verb = command[1]
        if verb == 'auth':
            return subprocess.CompletedProcess(command, self.login_status, '', '')
        if verb == 'digest':
            reference = command[2]
            if reference in self.tags:
                return subprocess.CompletedProcess(command, 0, self.tags[reference] + '\n', '')
            repository, tag = reference.removeprefix('ghcr.io/').rsplit(':', 1)
            error = self.digest_error if self.digest_error is not None else (f'Error: GET https://ghcr.io/v2/{repository}/manifests/{tag}: '
                                          'MANIFEST_UNKNOWN: manifest unknown')
            stderr = ('2026/11/14 14:22:55 HEAD request failed, falling back on GET: HEAD https://ghcr.io/v2/'
                      f'{repository}/manifests/{tag}: unexpected status code 404 Not Found\n{error}\n')
            return subprocess.CompletedProcess(command, 1, '', stderr)
        if verb == 'tag':
            repository, digest = command[2].removeprefix('ghcr.io/').split('@')
            self.tags[f'ghcr.io/{repository}:{command[3]}'] = self.tag_stores or digest
            return subprocess.CompletedProcess(command, 0, '', '')
        raise AssertionError(command)


class CraneTests(unittest.TestCase):
    def client(self, cli, **environment):
        variables = dict(GH_TOKEN='workflow-token', GITHUB_ACTOR='actor', ROMD_CRANE='/tools/crane',
                         AWS_SECRET_ACCESS_KEY='unrelated', **environment)
        self.verified = []
        with patch.dict(os.environ, variables):
            return Crane(runner=cli, verify=lambda directory, names: self.verified.append((directory, names)))

    def test_login_sends_the_token_on_stdin_into_a_private_config_and_verifies_the_pinned_binary(self):
        cli = CraneCli()
        crane = self.client(cli)
        command, kwargs = cli.calls[0]
        self.assertEqual(['/tools/crane', 'auth', 'login', 'ghcr.io', '--username', 'actor', '--password-stdin'], command)
        self.assertEqual('workflow-token', kwargs['input'])
        self.assertNotIn('workflow-token', ' '.join(command))
        self.assertEqual({'PATH', 'HOME', 'DOCKER_CONFIG'}, set(kwargs['env']))
        self.assertTrue(Path(kwargs['env']['DOCKER_CONFIG']).is_dir())
        self.assertEqual([(Path('/tools'), ('crane',))], self.verified)
        self.assertEqual(Path('/tools/crane'), crane.binary)
        config = Path(kwargs['env']['DOCKER_CONFIG'])
        del crane
        gc.collect()
        self.assertFalse(config.exists())
        with self.assertRaisesRegex(ValueError, 'authenticate'):
            self.client(CraneCli(login_status=1))
        for missing in ('GH_TOKEN', 'GITHUB_ACTOR', 'ROMD_CRANE'):
            environment = {key: value for key, value in dict(GH_TOKEN='t', GITHUB_ACTOR='a', ROMD_CRANE='/c').items()
                           if key != missing}
            with self.subTest(missing=missing), patch.dict(os.environ, environment, clear=True), \
                    self.assertRaisesRegex(ValueError, 'workflow token, actor and pinned crane'):
                Crane(runner=CraneCli(), verify=lambda *args: None)

    def test_digest_returns_existing_digests_and_none_only_for_the_registrys_own_not_found_codes(self):
        reference = 'ghcr.io/owner/romd-admin:v1.2.3'
        self.assertEqual(DIGEST, self.client(CraneCli({reference: DIGEST})).digest(reference))
        for code in ('MANIFEST_UNKNOWN', 'NAME_UNKNOWN'):
            error = f'Error: GET https://ghcr.io/v2/owner/romd-admin/manifests/v1.2.3: {code}: manifest unknown'
            with self.subTest(code=code):
                self.assertIsNone(self.client(CraneCli(digest_error=error)).digest(reference))
        for error in ('Error: GET https://ghcr.io/token?scope=repository%3Aowner%2Fromd-admin%3Apull&service=ghcr.io: '
                      'DENIED: requested access to the resource is denied',
                      'Error: GET https://ghcr.io/v2/owner/romd-admin/manifests/v1.2.3: UNAUTHORIZED: authentication required',
                      'Error: GET https://ghcr.io/v2/owner/romd-worker/manifests/v1.2.3: MANIFEST_UNKNOWN: manifest unknown',
                      'Error: Get "https://ghcr.io/v2/": dial tcp: lookup ghcr.io: no such host',
                      'Error: not found', ''):
            with self.subTest(error=error), self.assertRaisesRegex(ValueError, 'could not be verified'):
                self.client(CraneCli(digest_error=error)).digest(reference)

    def test_invalid_digest_output_and_foreign_references_are_refused(self):
        reference = 'ghcr.io/owner/romd-admin:v1.2.3'
        with self.assertRaisesRegex(ValueError, 'invalid digest'):
            self.client(CraneCli({reference: 'sha256:short'})).digest(reference)
        cli = CraneCli()
        crane = self.client(cli)
        for foreign in ('docker.io/owner/romd-admin:v1.2.3', 'ghcr.io/owner/other:v1.2.3', 'ghcr.io/owner/romd-admin',
                        'ghcr.io/owner/romd-admin:v1 --insecure'):
            with self.subTest(reference=foreign), self.assertRaisesRegex(ValueError, 'exact ghcr.io'):
                crane.digest(foreign)
        self.assertEqual(1, len(cli.calls))

    def test_tag_copies_the_digest_and_reads_the_tag_back(self):
        cli = CraneCli()
        self.client(cli).tag('owner/romd-admin', DIGEST, 'v1.2.3')
        self.assertIn(['/tools/crane', 'tag', 'ghcr.io/owner/romd-admin@' + DIGEST, 'v1.2.3'], [call[0] for call in cli.calls])
        self.assertEqual(['/tools/crane', 'digest', 'ghcr.io/owner/romd-admin:v1.2.3'], cli.calls[-1][0])
        with self.assertRaisesRegex(ValueError, 'does not resolve to the qualified digest'):
            self.client(CraneCli(tag_stores=OTHER)).tag('owner/romd-admin', DIGEST, 'v1.2.3')
        with self.assertRaisesRegex(ValueError, 'Expected an image digest'):
            self.client(CraneCli()).tag('owner/romd-admin', 'latest', 'v1.2.3')

    def test_only_this_repositorys_digest_pinned_romd_images_are_accepted(self):
        images = {f'ROMD_{name.upper()}_IMAGE': f'ghcr.io/owner/romd-{name}@{DIGEST}'
                  for name in ('admin', 'consumer', 'worker', 'player')}
        self.assertEqual(('owner/romd-admin', DIGEST), image_repositories(images, 'owner')['admin'])
        for key, value in (('ROMD_ADMIN_IMAGE', f'ghcr.io/other/romd-admin@{DIGEST}'),
                           ('ROMD_WORKER_IMAGE', 'ghcr.io/owner/romd-worker:v1.2.3'),
                           ('ROMD_PLAYER_IMAGE', f'ghcr.io/owner/romd-admin@{DIGEST}')):
            with self.subTest(key=key), self.assertRaises(ValueError):
                image_repositories(dict(images, **{key: value}), 'owner')


if __name__ == '__main__':
    unittest.main()
