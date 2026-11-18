import hashlib
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).with_name('drill_images.sh')


class DrillImageTransferTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.archive = self.root / 'production-drill-images.tar.gz'
        self.output = self.root / 'output'
        docker = self.root / 'docker'
        docker.write_text("""#!/bin/sh
case "$2" in
  inspect) printf '%s\\n' "$IMAGE_IDS" ;;
  save)
    printf 'disposable image archive'
    exit "$SAVE_STATUS"
    ;;
  load) cat > "$RUNNER_TEMP/loaded" ;;
  *) exit 1 ;;
esac
""")
        docker.chmod(0o755)
        self.env = dict(os.environ, PATH=f'{self.root}{os.pathsep}{os.environ["PATH"]}',
                        RUNNER_TEMP=str(self.root), GITHUB_OUTPUT=str(self.output),
                        IMAGE_IDS='source-image-ids', SAVE_STATUS='0')

    def transfer(self, command, **overrides):
        return subprocess.run(['bash', SCRIPT, command], env=self.env | overrides, capture_output=True, text=True)

    def test_loaded_source_images_are_verified_before_the_archive_is_deleted(self):
        self.assertEqual(0, self.transfer('save').returncode)
        expected = self.output.read_text().strip().removeprefix('image-ids=')
        self.assertEqual(hashlib.sha256(b'source-image-ids\n').hexdigest(), expected)
        rejected = self.transfer('load', EXPECTED_IMAGE_IDS=expected, IMAGE_IDS='other-image-ids')
        self.assertNotEqual(0, rejected.returncode)
        self.assertIn('Loaded image IDs do not match', rejected.stderr)
        self.assertTrue(self.archive.is_file())

        self.assertEqual(0, self.transfer('load', EXPECTED_IMAGE_IDS=expected).returncode)
        self.assertEqual('disposable image archive', (self.root / 'loaded').read_text())
        self.assertFalse(self.archive.exists())

    def test_failed_docker_export_does_not_publish_an_image_identity(self):
        failed = self.transfer('save', SAVE_STATUS='7')
        self.assertEqual(7, failed.returncode)
        self.assertFalse(self.output.exists())
