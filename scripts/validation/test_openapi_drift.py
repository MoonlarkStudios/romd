import contextlib
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import openapi_drift

SCHEMA = 'web/schemas/admin-v1.json'
GENERATED = 'web/packages/romd-consumer-api-client/src/generated'


class OpenApiDriftTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        for relative in (SCHEMA, GENERATED + '/client.ts', 'web/node_modules/.keep'):
            (self.root / relative).parent.mkdir(parents=True, exist_ok=True)
            (self.root / relative).write_text('committed\n')
        (self.root / '.gitignore').write_text('node_modules/\n')
        subprocess.run(['git', 'init', '--quiet', str(self.root)], check=True)
        subprocess.run(['git', '-C', str(self.root), 'add', '.'], check=True)
        subprocess.run(['git', '-C', str(self.root), '-c', 'user.name=Test', '-c', 'user.email=test@example.test',
                        'commit', '-qm', 'fixture'], check=True)

    def run_check(self, script):
        regenerate = [sys.executable, '-c', script] if script is not None else [sys.executable, '-c', 'raise SystemExit(99)']
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr), contextlib.redirect_stdout(io.StringIO()):
            code = openapi_drift.main(self.root, regenerate)
        return code, stderr.getvalue()

    def test_matching_regeneration_passes(self):
        self.assertEqual((0, ''), self.run_check('pass'))

    def test_changed_or_untracked_generated_output_fails_with_remediation(self):
        for script in ('open("schemas/admin-v1.json", "w").write("drift\\n")',
                       'open("packages/romd-consumer-api-client/src/generated/new.ts", "w").write("new\\n")'):
            with self.subTest(script=script):
                code, message = self.run_check(script)
                self.assertEqual(1, code)
                self.assertIn("run 'pnpm api:update' from web/", message)
                subprocess.run(['git', '-C', str(self.root), 'checkout', '--quiet', '--', '.'], check=True)
                subprocess.run(['git', '-C', str(self.root), 'clean', '-qfd', '--', 'web/packages'], check=True)

    def test_dirty_generated_paths_are_refused_before_regeneration(self):
        for relative in (GENERATED + '/client.ts', GENERATED + '/untracked.ts'):
            with self.subTest(relative=relative):
                (self.root / relative).write_text('hand edit\n')
                code, message = self.run_check(None)  # Regeneration would exit 99 if it ran.
                self.assertEqual(2, code)
                self.assertIn('refused', message)
                self.assertIn(relative, message)
                self.assertEqual('hand edit\n', (self.root / relative).read_text())
                subprocess.run(['git', '-C', str(self.root), 'checkout', '--quiet', '--', '.'], check=True)
                subprocess.run(['git', '-C', str(self.root), 'clean', '-qfd', '--', 'web/packages'], check=True)

    def test_missing_web_dependencies_fail_before_regeneration(self):
        (self.root / 'web/node_modules/.keep').unlink()
        (self.root / 'web/node_modules').rmdir()
        code, message = self.run_check(None)
        self.assertEqual(2, code)
        self.assertIn('pnpm install --frozen-lockfile', message)

    def test_failed_regeneration_propagates_its_status(self):
        code, message = self.run_check('raise SystemExit(3)')
        self.assertEqual(3, code)
        self.assertIn('regeneration failed', message)


if __name__ == '__main__':
    unittest.main()
