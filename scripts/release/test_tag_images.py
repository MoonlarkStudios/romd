from pathlib import Path
import tempfile
import unittest

from tag_images import tag_images
from test_crane import DIGEST, OTHER, MemoryCrane
from test_promotion import image_lines

SERVICES = ('admin', 'consumer', 'worker', 'player')


class TagImagesTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.images = Path(temporary.name) / 'images.env'
        self.images.write_text(image_lines('owner', DIGEST))

    def test_every_romd_image_gets_the_version_tag_on_its_qualified_digest(self):
        crane = MemoryCrane()
        self.assertEqual({service: DIGEST for service in SERVICES}, tag_images(self.images, 'Owner/romd', 'v1.2.3', crane))
        self.assertEqual({(f'owner/romd-{service}', DIGEST, 'v1.2.3') for service in SERVICES}, set(crane.writes))
        # Every tag is read before the first write.
        self.assertEqual(4, len(crane.reads))

    def test_a_different_existing_digest_blocks_before_any_write(self):
        crane = MemoryCrane({('owner/romd-player', 'v1.2.3'): OTHER})
        with self.assertRaisesRegex(ValueError, 'romd-player:v1.2.3 already names a different digest'):
            tag_images(self.images, 'Owner/romd', 'v1.2.3', crane)
        self.assertEqual([], crane.writes)

    def test_interrupted_attempt_keeps_matching_tags_and_completes_the_rest(self):
        crane = MemoryCrane({('owner/romd-admin', 'v1.2.3'): DIGEST})
        tag_images(self.images, 'Owner/romd', 'v1.2.3', crane)
        self.assertEqual({f'owner/romd-{service}' for service in SERVICES[1:]}, {write[0] for write in crane.writes})

    def test_invalid_tag_repository_or_foreign_image_is_refused(self):
        for tag, repository in (('main', 'Owner/romd'), ('v1.2.3', 'owner/romd;x'), ('v1.2.3', 'Other/romd')):
            crane = MemoryCrane()
            with self.subTest(tag=tag, repository=repository), self.assertRaises(ValueError):
                tag_images(self.images, repository, tag, crane)
            self.assertEqual([], crane.writes)


if __name__ == '__main__':
    unittest.main()
