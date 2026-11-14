import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from registry import Registry
from tag_images import tag_images
from test_promotion import DIGEST, FakeRegistry, image_lines


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


class TagImagesTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.images = Path(temporary.name) / 'images.env'
        self.digest = FakeRegistry().digest
        self.images.write_text(image_lines('owner', self.digest))

    def tag(self, fakes, tag='v1.2.3', repository='Owner/romd'):
        def client(name):
            with patch.dict(os.environ, GH_TOKEN='token', GITHUB_ACTOR='actor'):
                return Registry(name, fakes[name])
        return tag_images(self.images, repository, tag, registry=client)

    def fakes(self, **tags):
        return {f'owner/romd-{service}': FakeRegistry(tags=tags.get(service))
                for service in ('admin', 'consumer', 'worker', 'player')}

    def test_every_romd_image_gets_the_version_tag_on_its_qualified_digest(self):
        fakes = self.fakes()
        self.assertEqual({'admin', 'consumer', 'worker', 'player'}, set(self.tag(fakes)))
        for fake in fakes.values():
            self.assertEqual(self.digest, fake.tags['v1.2.3'])
            self.assertEqual(1, sum(request[0] == 'PUT' for request in fake.requests))

    def test_a_different_existing_digest_blocks_before_any_write(self):
        fakes = self.fakes(player={'v1.2.3': 'sha256:' + 'e' * 64})
        with self.assertRaisesRegex(ValueError, 'romd-player:v1.2.3 already names a different digest'):
            self.tag(fakes)
        self.assertFalse(any(request[0] == 'PUT' for fake in fakes.values() for request in fake.requests))

    def test_interrupted_attempt_keeps_matching_tags_and_completes_the_rest(self):
        fakes = self.fakes(admin={'v1.2.3': self.digest})
        self.tag(fakes)
        self.assertFalse(any(request[0] == 'PUT' for request in fakes['owner/romd-admin'].requests))
        self.assertEqual(self.digest, fakes['owner/romd-worker'].tags['v1.2.3'])

    def test_invalid_tag_repository_or_foreign_image_is_refused(self):
        for tag, repository in (('main', 'Owner/romd'), ('v1.2.3', 'owner/romd;x'), ('v1.2.3', 'Other/romd')):
            with self.subTest(tag=tag, repository=repository), self.assertRaises(ValueError):
                self.tag(self.fakes(), tag, repository)


if __name__ == '__main__':
    unittest.main()
