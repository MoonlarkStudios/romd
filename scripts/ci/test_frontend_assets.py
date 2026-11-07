import base64
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from frontend_assets import HOSTS, assets


class FrontendAssetTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for role, host in HOSTS.items():
            dist = self.root / f'web/packages/romd-{role}-app/dist'
            dist.mkdir(parents=True)
            (dist / 'index.html').write_text('<script src="app.js"></script>')
            (dist / 'app.js').write_text('current build')
            (self.root / 'src' / host).mkdir(parents=True)

    def manifests(self):
        for host in HOSTS.values():
            directory = self.root / 'src' / host
            manifest = directory / 'obj/Release/net10.0/staticwebassets.build.json'
            manifest.parent.mkdir(parents=True, exist_ok=True)
            manifest.write_text(json.dumps({'Assets': [
                {'Identity': str(path), 'AssetRole': 'Primary', 'CopyToPublishDirectory': 'PreserveNewest',
                 'Integrity': base64.b64encode(hashlib.sha256(path.read_bytes()).digest()).decode()}
                for path in (directory / 'wwwroot').iterdir()]}))

    def test_current_bytes_replace_stale_assets_and_are_bound_by_msbuild(self):
        stale = self.root / 'src/Romd.Admin.Host/wwwroot'
        stale.mkdir(); (stale / 'old.js').write_text('obsolete')
        assets(self.root, stage=True)
        self.assertFalse((stale / 'old.js').exists())
        self.manifests()
        assets(self.root)
        (stale / 'app.js').write_text('old bytes')
        with self.assertRaisesRegex(ValueError, 'differ'):
            assets(self.root)

    def test_missing_index_linked_inputs_and_linked_destinations_fail(self):
        dist = self.root / 'web/packages/romd-admin-app/dist'
        (dist / 'index.html').unlink()
        with self.assertRaisesRegex(ValueError, 'index'):
            assets(self.root, stage=True)
        (dist / 'index.html').symlink_to(dist / 'app.js')
        with self.assertRaisesRegex(ValueError, 'Linked'):
            assets(self.root, stage=True)
        (dist / 'index.html').unlink(); (dist / 'index.html').write_text('html')
        (self.root / 'src/Romd.Admin.Host/wwwroot').symlink_to(dist, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'linked'):
            assets(self.root, stage=True)

    def test_missing_manifest_asset_wrong_integrity_or_unpublished_asset_fails(self):
        assets(self.root, stage=True)
        self.manifests()
        path = self.root / 'src/Romd.Admin.Host/obj/Release/net10.0/staticwebassets.build.json'
        original = json.loads(path.read_text())
        for key, value in [('Integrity', 'wrong'), ('CopyToPublishDirectory', 'Never')]:
            modified = json.loads(json.dumps(original)); modified['Assets'][0][key] = value
            path.write_text(json.dumps(modified))
            with self.assertRaisesRegex(ValueError, 'MSBuild'):
                assets(self.root)
        path.write_text(json.dumps({'Assets': []}))
        with self.assertRaisesRegex(ValueError, 'MSBuild'):
            assets(self.root)
