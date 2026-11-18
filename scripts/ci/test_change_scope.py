import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from change_scope import changed_paths, required_drills

BOTH = {'chd', 'packaged'}


class ChangeScopeTests(unittest.TestCase):
    def test_unrelated_pull_request_paths_run_neither_drill(self):
        unrelated = ['README.md', 'docs/features/disc-compression.md', 'web/packages/romd-admin-app/src/App.tsx',
                     'clients/romd_console/lib/main.dart', 'src/Romd.Infrastructure/Import/PathValidator.cs',
                     'tests/Romd.Application.Tests/Libraries/LibraryTests.cs', 'web/pnpm-lock.yaml']
        self.assertEqual(set(), required_drills('pull_request', unrelated))
        for event in ('push', 'workflow_call', 'workflow_dispatch', 'schedule', ''):
            self.assertEqual(BOTH, required_drills(event, unrelated))
        self.assertEqual(BOTH, required_drills('pull_request', []))

    def test_source_advisory_changes_skip_only_the_pr_rehearsal(self):
        paths = ['scripts/security/' + name for name in (
            'advisory_exceptions.py', 'source-advisory-exceptions.json', 'source_advisories.py',
            'test_advisory_exceptions.py', 'test_javascript_advisories.py', 'test_source_advisories.py',
        )]
        self.assertEqual(set(), required_drills('pull_request', paths))
        for event in ('push', 'workflow_call'):
            self.assertEqual(BOTH, required_drills(event, paths))
        for shared in ('policy.py', 'metadata.py', 'tools.json', 'go_vex.py', 'new_scanner.py'):
            with self.subTest(shared=shared):
                self.assertEqual({'packaged'}, required_drills('pull_request', paths + ['scripts/security/' + shared]))

    def test_related_pull_request_paths_select_their_drill(self):
        cases = {'deploy/README.md': {'packaged'}, 'scripts/deployment/validate.py': {'packaged'},
                 'scripts/release/qualify.py': {'packaged'}, 'scripts/backup/backup.sh': {'packaged'},
                 'scripts/security/policy.py': {'packaged'},
                 'native/libchdr/upstream': {'chd'}, 'scripts/e2e/chd.py': {'chd'},
                 'src/Romd.Media/Cd/CdReader.cs': {'chd'}, 'tests/Romd.Media.Tests/Test.cs': {'chd'},
                 'src/Romd.Infrastructure/Storage/ChdEncoder.cs': {'chd'},
                 'tools/Moonlark.Native.Engineering/Program.cs': {'chd'},
                 'Dockerfile': BOTH, '.dockerignore': BOTH, 'compose.yaml': BOTH,
                 '.github/workflows/ci.yml': BOTH, 'scripts/ci/change_scope.py': BOTH,
                 'scripts/validation/checks.json': BOTH,
                 'brand/../src/File.cs': BOTH, '/docs/file.md': BOTH}
        for path, expected in cases.items():
            with self.subTest(path=path):
                self.assertEqual(expected, required_drills('pull_request', ['README.md', path]))
        self.assertEqual(BOTH, required_drills('pull_request', ['deploy/README.md', 'native/libchdr/upstream']))

    def test_rename_out_of_a_drill_path_and_bad_history_cannot_hide_relevant_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            def git(*args):
                return subprocess.check_output(['git', '-C', str(root), '-c', 'user.name=Test',
                    '-c', 'user.email=test@example.test', *args], text=True).strip()
            git('init', '-q')
            (root / 'native').mkdir()
            (root / 'native/old.c').write_text('same bytes')
            git('add', '.'); git('commit', '-qm', 'before')
            base = git('rev-parse', 'HEAD')
            (root / 'docs').mkdir()
            os.rename(root / 'native/old.c', root / 'docs/moved.md')
            git('add', '-A'); git('commit', '-qm', 'after')
            paths = changed_paths(root, base)
            self.assertEqual({'native/old.c', 'docs/moved.md'}, set(paths))
            self.assertEqual({'chd'}, required_drills('pull_request', paths))
            for invalid in ('', 'HEAD', '--all', '0' * 40):
                with self.subTest(base=invalid), self.assertRaises((ValueError, subprocess.CalledProcessError)):
                    changed_paths(root, invalid)
