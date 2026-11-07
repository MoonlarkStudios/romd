import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from change_scope import changed_paths, requires_chd


class ChangeScopeTests(unittest.TestCase):
    def test_only_unrelated_pr_paths_can_omit_chd(self):
        safe = ['README.md', 'docs/features/disc-compression.md', 'brand/romd/logo.png',
                'web/packages/romd-admin-app/src/App.tsx', 'clients/romd_console/lib/main.dart']
        self.assertFalse(requires_chd('pull_request', safe))
        for event in ('push', 'workflow_call', 'workflow_dispatch', 'schedule', ''):
            self.assertTrue(requires_chd(event, safe))
        self.assertTrue(requires_chd('pull_request', []))

    def test_build_contract_native_and_unknown_changes_require_the_drill(self):
        for path in ('src/Romd.Storage/File.cs', 'tests/Romd.Media.Tests/Test.cs', 'Dockerfile',
                     '.github/workflows/ci.yml', 'scripts/ci/change_scope.py', 'global.json',
                     'web/pnpm-lock.yaml', 'web/packages/romd-admin-app/vite.config.ts',
                     'web/schemas/admin-v1.json', 'native/libchdr/upstream', 'reference-data/media.json',
                     'brand/../src/File.cs', '/docs/file.md', 'new-unknown-file'):
            with self.subTest(path=path):
                self.assertTrue(requires_chd('pull_request', ['README.md', path]))

    def test_rename_out_of_backend_and_bad_history_cannot_hide_relevant_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            def git(*args):
                return subprocess.check_output(['git', '-C', str(root), '-c', 'user.name=Test',
                    '-c', 'user.email=test@example.test', *args], text=True).strip()
            git('init', '-q')
            (root / 'src').mkdir()
            (root / 'src/old.cs').write_text('same bytes')
            git('add', '.'); git('commit', '-qm', 'before')
            base = git('rev-parse', 'HEAD')
            (root / 'docs').mkdir()
            os.rename(root / 'src/old.cs', root / 'docs/moved.md')
            git('add', '-A'); git('commit', '-qm', 'after')
            paths = changed_paths(root, base)
            self.assertEqual({'src/old.cs', 'docs/moved.md'}, set(paths))
            self.assertTrue(requires_chd('pull_request', paths))
            for invalid in ('', 'HEAD', '--all', '0' * 40):
                with self.subTest(base=invalid), self.assertRaises((ValueError, subprocess.CalledProcessError)):
                    changed_paths(root, invalid)
