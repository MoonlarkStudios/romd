import json
from pathlib import Path
import shutil
import tempfile
import tomllib
import unittest

from toolchain import ROOT, check


class ToolchainTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='romd-toolchain-contract-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        for name in ('.mise.toml', 'global.json', 'web/package.json', '.github/workflows/ci.yml',
                     '.github/workflows/dependencies.yml', '.github/workflows/release.yml',
                     '.github/workflows/security-maintenance.yml',
                     'clients/romd_console/.mise.toml', 'Dockerfile'):
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, target)
        self.sdk = json.loads((self.root / 'global.json').read_text())['sdk']['version']

    def test_declared_toolchains_agree(self):
        self.assertEqual([], check(self.root, runtime=False))

    def test_maintenance_sdk_must_match_the_strict_pin(self):
        workflow = self.root / '.github/workflows/security-maintenance.yml'
        workflow.write_text(workflow.read_text().replace('global-json-file: global.json', 'dotnet-version: 0.0.1'))
        self.assertIn('CI step SDK disagrees with mise', check(self.root, runtime=False))

    def test_stale_hardcoded_ci_step_sdk_is_rejected(self):
        workflow = self.root / '.github/workflows/ci.yml'
        workflow.write_text(workflow.read_text().replace(f'dotnet-version: {self.sdk}', 'dotnet-version: 0.0.1'))
        self.assertIn('CI step SDK disagrees with mise', check(self.root, runtime=False))

    def test_node_in_every_workflow_must_match_regardless_of_quotes(self):
        node = tomllib.loads((self.root / '.mise.toml').read_text())['tools']['node']
        exercised = set()
        for name in ('ci.yml', 'dependencies.yml', 'release.yml'):
            workflow = self.root / '.github/workflows' / name
            original = workflow.read_text()
            for quote in ('"', "'"):
                stale = original.replace(f'node-version: {quote}{node}{quote}', f'node-version: {quote}0.0.1{quote}')
                if stale == original:
                    continue
                exercised.add(name)
                with self.subTest(workflow=name, quote=quote):
                    workflow.write_text(stale)
                    self.assertIn('CI Node disagrees with mise', check(self.root, runtime=False))
                    workflow.write_text(original)
        self.assertEqual({'ci.yml', 'dependencies.yml', 'release.yml'}, exercised)

    def test_flutter_must_match_the_console_pin(self):
        console = self.root / 'clients/romd_console/.mise.toml'
        flutter = tomllib.loads(console.read_text())['tools']['flutter']
        console.write_text(console.read_text().replace(f'flutter = "{flutter}"', 'flutter = "0.0.1"'))
        self.assertIn('CI Flutter disagrees with the console mise pin', check(self.root, runtime=False))

    def test_actionlint_url_directory_and_filename_must_both_match(self):
        version = tomllib.loads((self.root / '.mise.toml').read_text())['tools']['actionlint']
        workflow = self.root / '.github/workflows/ci.yml'
        original = workflow.read_text()
        for old, new in ((f'/download/v{version}/', '/download/v0.0.1/'),
                         (f'/actionlint_{version}_linux', '/actionlint_0.0.1_linux')):
            with self.subTest(part=old):
                workflow.write_text(original.replace(old, new))
                self.assertIn('CI actionlint download disagrees with mise', check(self.root, runtime=False))

    def test_native_sdk_archive_directory_and_filename_must_both_match(self):
        dockerfile = self.root / 'Dockerfile'
        original = dockerfile.read_text()
        for old, new in ((f'/Sdk/{self.sdk}/', '/Sdk/0.0.1/'),
                         (f'/dotnet-sdk-{self.sdk}-linux-', '/dotnet-sdk-0.0.1-linux-')):
            with self.subTest(part=old):
                dockerfile.write_text(original.replace(old, new))
                self.assertIn('native builder SDK archive disagrees with strict global.json selection',
                              check(self.root, runtime=False))


if __name__ == '__main__':
    unittest.main()
