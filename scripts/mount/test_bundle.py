import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import bundle


class BundleTests(unittest.TestCase):
    def test_manifest_rejects_content_mode_inventory_and_symlink_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            file = root / 'client'
            file.write_bytes(b'client')
            file.chmod(0o755)
            manifest = {'signedRelease': False, 'files': {'client': {
                'sha256': hashlib.sha256(b'client').hexdigest(), 'executable': True}}}
            (root / 'manifest.json').write_text(json.dumps(manifest))
            bundle.verify(root)
            file.write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError, 'differs'):
                bundle.verify(root)
            file.write_bytes(b'client')
            file.chmod(0o644)
            with self.assertRaisesRegex(ValueError, 'differs'):
                bundle.verify(root)
            file.chmod(0o755)
            extra = root / 'extra'
            extra.write_text('unexpected')
            with self.assertRaisesRegex(ValueError, 'inventory'):
                bundle.verify(root)
            extra.unlink()
            extra.symlink_to(root, target_is_directory=True)
            with self.assertRaisesRegex(ValueError, 'symbolic links'):
                bundle.verify(root)

    def test_wrong_archive_is_rejected_before_publish(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / 'archive.zip'
            archive.write_bytes(b'not the pinned archive')
            output = root / 'output'
            with patch('bundle.subprocess.run') as publish:
                with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                    bundle.build(output, archive)
                publish.assert_not_called()
            self.assertFalse(output.exists())

    def test_existing_output_is_preserved(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'sentinel').write_text('preserve')
            with self.assertRaisesRegex(ValueError, 'already exist'):
                bundle.build(root, None)
            self.assertEqual((root / 'sentinel').read_text(), 'preserve')


if __name__ == '__main__':
    unittest.main()
