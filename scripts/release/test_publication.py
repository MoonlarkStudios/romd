import json
import io
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch, MagicMock
import urllib.error
from types import SimpleNamespace

from preflight import valid_tag, verify
from publication import allow_retry, registry_exists, release_metadata, publish
from qualify import verify_publisher, IMAGE_KEYS, canonical_digest


class ReleasePolicyTests(unittest.TestCase):
    def test_tagged_official_pins_match_docker_normalized_repo_digests(self):
        digest = 'sha256:' + 'a' * 64
        self.assertEqual(canonical_digest('postgres:18-bookworm@' + digest),
                         canonical_digest('docker.io/library/postgres@' + digest))
        self.assertEqual('registry.example:5000/repo@' + digest,
                         canonical_digest('registry.example:5000/repo:version@' + digest))

    def test_malformed_versions_are_rejected_before_any_build(self):
        for tag in ('v1.2.3', 'v0.0.0-alpha.1', 'v1.2.3-rc-1'):
            self.assertTrue(valid_tag(tag))
        for tag in ('v01.2.3', 'v1.2.3-', 'v1.2.3-01', 'v1.2.3-a..b', 'v1.2.3;true', 'main'):
            self.assertFalse(valid_tag(tag))

    def test_tag_must_be_allowed_branch_tip_and_clean_checkout(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            def git(*args):
                return subprocess.check_output(['git', '-C', str(root), '-c', 'user.name=Test',
                    '-c', 'user.email=test@example.test', *args], text=True).strip()
            git('init', '-q')
            (root / '.github').mkdir()
            (root / '.github/release-policy.json').write_text(json.dumps(dict(schemaVersion=1,
                allowedBranch='main', requireBranchTip=True, platform='linux/amd64', candidateOnly=True)))
            git('add', '.')
            git('commit', '-qm', 'fixture')
            revision = git('rev-parse', 'HEAD')
            git('tag', 'v1.2.3')
            git('update-ref', 'refs/remotes/origin/main', revision)
            self.assertEqual(revision, verify(root, 'v1.2.3', revision)['commit'])
            (root / 'dirty').touch()
            with self.assertRaisesRegex(ValueError, 'uncommitted'):
                verify(root, 'v1.2.3', revision)
            (root / 'dirty').unlink()
            git('commit', '--allow-empty', '-qm', 'new tip')
            git('update-ref', 'refs/remotes/origin/main', git('rev-parse', 'HEAD'))
            git('checkout', '-q', '--detach', revision)
            with self.assertRaisesRegex(ValueError, 'branch tip'):
                verify(root, 'v1.2.3', revision)

    def test_partial_or_exposed_candidates_cannot_be_silently_replaced(self):
        revision = 'a' * 40
        allow_retry(None, revision)
        allow_retry(dict(draft=True, prerelease=True, target_commitish=revision, assets=[]), revision)
        for change in (dict(draft=False), dict(prerelease=False), dict(target_commitish='b' * 40),
                       dict(assets=[dict(name='images.env')]), dict(assets=[dict(name='mame-source.tar.gz')])):
            state = dict(draft=True, prerelease=True, target_commitish=revision, assets=[])
            state.update(change)
            with self.assertRaises(ValueError):
                allow_retry(state, revision)

    def test_draft_lookup_is_authenticated_and_blocks_existing_source_assets(self):
        response = io.BytesIO(json.dumps([dict(tag_name='v1.2.3', draft=True, prerelease=True,
            target_commitish='a' * 40, assets=[dict(name='mame-source.tar.gz')])]).encode())
        opener = MagicMock()
        opener.open.return_value = response
        with patch.dict(os.environ, GH_TOKEN='fixture'), patch('publication.urllib.request.build_opener', return_value=opener):
            metadata = release_metadata('owner/repo', 'v1.2.3')
        self.assertIn('/releases?per_page=100&page=1', opener.open.call_args.args[0].full_url)
        with self.assertRaises(ValueError):
            allow_retry(metadata, 'a' * 40)

    def test_only_authenticated_manifest_not_found_establishes_absence(self):
        reference = 'ghcr.io/owner/romd-admin:v1.2.3'
        def token():
            return io.BytesIO(b'{"token":"fixture-scoped-token"}')
        def error(body, status=404):
            return urllib.error.HTTPError('https://ghcr.io/v2/owner/romd-admin/manifests/v1.2.3',
                status, 'fixture', {}, io.BytesIO(json.dumps(body).encode()))
        opener = MagicMock()
        with patch.dict(os.environ, GH_TOKEN='fixture', GITHUB_ACTOR='actor'), patch('publication.urllib.request.build_opener', return_value=opener):
            for role in ('admin', 'consumer', 'worker', 'player'):
                for code in ('MANIFEST_UNKNOWN', 'NAME_UNKNOWN'):
                    with self.subTest(role=role, code=code):
                        opener.open.side_effect = [token(), error({'errors': [{'code': code}]})]
                        self.assertFalse(registry_exists(f'ghcr.io/owner/romd-{role}:v1.2.3'))
                        self.assertEqual(f'https://ghcr.io/v2/owner/romd-{role}/manifests/v1.2.3',
                                         opener.open.call_args.args[0].full_url)
            for diagnostic in ('docker-buildx executable not found', 'authorization failed: token not found',
                               'credential configuration not found'):
                opener.open.side_effect = [token(), urllib.error.URLError(diagnostic)]
                with self.assertRaises(RuntimeError):
                    registry_exists(reference)
            for body, status in [({'message': 'not found'}, 404), ({'errors': [{'code': 'UNAUTHORIZED'}]}, 401)]:
                opener.open.side_effect = [token(), error(body, status)]
                with self.assertRaises(ValueError):
                    registry_exists(reference)

    def test_publisher_hash_blocks_self_consistent_substituted_bundle(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            bundle = root / 'bundle.tar.gz'
            bundle.write_bytes(b'published bundle')
            images = {key: 'example.test/image@sha256:' + 'b' * 64 for key in IMAGE_KEYS}
            image_file = root / 'images.env'
            image_file.write_text(''.join(f'{key}={value}\n' for key, value in images.items()))
            state = root / 'candidate.json'
            state.write_text(json.dumps(dict(sourceCommit='a' * 40, releaseTag='v1.2.3', images=images,
                bundleSha256=hashlib.sha256(bundle.read_bytes()).hexdigest())))
            verify_publisher(bundle, state, image_file, 'a' * 40, 'v1.2.3')
            bundle.write_bytes(b'substitute with its own valid manifest and checksum')
            with self.assertRaisesRegex(ValueError, 'independent publisher'):
                verify_publisher(bundle, state, image_file, 'a' * 40, 'v1.2.3')

    def test_verified_worker_source_is_uploaded_before_any_image_push(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / 'source.tar.gz'
            source.write_bytes(b'synthetic source')
            (root / 'native/chdman').mkdir(parents=True)
            (root / 'native/chdman/mame.json').write_text(json.dumps(dict(sourceBytes=source.stat().st_size,
                sourceSha256=hashlib.sha256(source.read_bytes()).hexdigest())))
            (root / 'scripts/deployment').mkdir(parents=True)
            (root / 'scripts/deployment/base-images.json').write_text(json.dumps(dict(postgres='postgres@sha256:' + 'b' * 64)))
            output = root / 'output'
            output.mkdir()
            args = SimpleNamespace(repository='owner/repo', tag='v1.2.3', revision='a' * 40,
                                   source_archive=source, output=output)
            commands = []
            labels = {'org.opencontainers.image.revision': args.revision, 'org.opencontainers.image.version': args.tag,
                      'org.opencontainers.image.source': 'https://github.com/other/repo'}
            def execute(command, capture=False):
                command = [str(part) for part in command]
                commands.append(command)
                if command[:3] == ['docker', 'image', 'inspect']:
                    return json.dumps([dict(Id='sha256:' + 'c' * 64, Os='linux', Architecture='amd64', Config=dict(Labels=labels))])
                if command[:3] == ['docker', 'buildx', 'imagetools']:
                    return json.dumps('sha256:' + 'b' * 64)
                if command[0].endswith('/bundle.sh'):
                    Path(command[2]).write_bytes(b'synthetic bundle')
            with patch('publication.ROOT', root), patch('publication.release_metadata', return_value=None), \
                    patch('publication.registry_exists', return_value=False), patch('publication.run', side_effect=execute), \
                    patch('publication.verify_rehearsal', return_value={'images': {key: ('postgres' if key == 'ROMD_POSTGRES_IMAGE' else 'example') + '@sha256:' + 'b' * 64 for key in IMAGE_KEYS}}), \
                    patch('publication.verify_published_bundle'):
                # An image that would link its new package to another repository is refused before any upload or push.
                with self.assertRaisesRegex(ValueError, 'source repository label'):
                    publish(args)
                self.assertFalse(any(command[:2] in (['docker', 'push'], ['gh', 'release']) for command in commands))
                labels['org.opencontainers.image.source'] = 'https://github.com/Owner/Repo'
                commands.clear()
                publish(args)
            upload = next(i for i, command in enumerate(commands) if command[:3] == ['gh', 'release', 'upload']
                          and str(source) in command)
            pushes = [i for i, command in enumerate(commands) if command[:2] == ['docker', 'push']]
            self.assertEqual({f'ghcr.io/owner/romd-{role}:v1.2.3' for role in ('admin', 'consumer', 'worker', 'player')},
                             {commands[i][2] for i in pushes})
            self.assertEqual('postgres@sha256:' + 'b' * 64,
                             json.loads((output / 'candidate.json').read_text())['images']['ROMD_POSTGRES_IMAGE'])
            self.assertTrue(all(upload < i for i in pushes))
            self.assertFalse(any('--clobber' in command for command in commands))
            self.assertIn('--prerelease', commands[-1])
            source.write_bytes(b'tampered source')
            with patch('publication.ROOT', root), patch('publication.release_metadata') as lookup:
                with self.assertRaisesRegex(ValueError, 'source archive'):
                    publish(args)
                lookup.assert_not_called()
