from contextlib import redirect_stderr, redirect_stdout
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import secret_scan as scan_secrets

SCANNER = os.environ.get('ROMD_TEST_GITLEAKS')


class SecretPolicyTests(unittest.TestCase):
    def test_bad_base_cannot_turn_into_a_successful_empty_scan(self):
        for base in ('', 'HEAD', '--all', '0' * 8):
            with self.subTest(base=base), self.assertRaises(ValueError):
                scan_secrets.log_scope(Path('.'), base)
        self.assertEqual('--all', scan_secrets.log_scope(Path('.'), '0' * 40))

    def test_only_exact_finding_and_git_object_can_be_excepted(self):
        item = {'path': 'tests/fixture.cs', 'rule': 'generic-api-key', 'startLine': 31,
                'endLine': 31, 'gitBlob': 'a' * 40}
        finding = {'File': item['path'], 'RuleID': item['rule'], 'StartLine': 31,
                   'EndLine': 31, 'Commit': 'b' * 40}
        with patch('secret_scan.subprocess.check_output', return_value='a' * 40 + '\n'):
            self.assertTrue(scan_secrets.reviewed(Path('.'), finding, [item]))
            for key, value in [('File', 'tests/other.cs'), ('RuleID', 'other'), ('StartLine', 32), ('EndLine', 32)]:
                self.assertFalse(scan_secrets.reviewed(Path('.'), finding | {key: value}, [item]))
        with patch('secret_scan.subprocess.check_output', return_value='c' * 40):
            self.assertFalse(scan_secrets.reviewed(Path('.'), finding, [item]))


@unittest.skipUnless(SCANNER, 'pinned scanner integration runs in source-checks with ROMD_TEST_GITLEAKS')
class RealSecretScannerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(); self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.git('init', '-q')
        (self.root / '.gitleaks.toml').write_text('title="test"\n[extend]\nuseDefault=true\n')
        self.policy = self.root / 'scripts/ci/secret-exceptions.json'
        self.policy.parent.mkdir(parents=True)
        self.policy.write_text(json.dumps({'version': 1, 'exceptions': []}))
        self.commit()
        self.base = self.git('rev-parse', 'HEAD')
        self.value = hashlib.sha256(b'synthetic-ci-negative-control').hexdigest()

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.root), '-c', 'user.name=Test',
             '-c', 'user.email=test@example.test', *args], text=True).strip()

    def commit(self):
        self.git('add', '-A'); self.git('commit', '--allow-empty', '-qm', 'fixture')

    def scan(self, scope=None):
        output = io.StringIO()
        with redirect_stdout(output), redirect_stderr(output):
            result = scan_secrets.scan(self.root, Path(SCANNER), scope or self.base + '..HEAD')
        self.assertNotIn(self.value, output.getvalue())
        return result

    def write_secret(self, suffix=''):
        (self.root / 'fixture.txt').write_text('api' + '_key = "' + self.value + '"' + suffix + '\n')
        self.commit()

    def test_actual_scanner_rejects_a_new_secret_without_echoing_it(self):
        self.write_secret()
        self.assertTrue(self.scan())

    def test_ignore_files_and_inline_directives_cannot_suppress_a_secret(self):
        (self.root / '.gitleaksignore').write_text('*\n')
        self.write_secret(' # gitleaks:allow')
        with patch.dict(os.environ, {'GITLEAKS_CONFIG_TOML': '[extend]\nuseDefault=false\n'}):
            self.assertTrue(self.scan())

    def test_exact_synthetic_exception_does_not_survive_changed_bytes(self):
        self.write_secret()
        self.policy.write_text(json.dumps({'version': 1, 'exceptions': [{
            'path': 'fixture.txt', 'rule': 'generic-api-key', 'startLine': 1, 'endLine': 1,
            'gitBlob': self.git('rev-parse', 'HEAD:fixture.txt')}]}))
        self.assertFalse(self.scan())
        # Git scans added lines. Emit a new finding at the same path/rule/line,
        # rather than appending unrelated text outside the existing secret hunk.
        self.value = hashlib.sha256(b'changed-synthetic-ci-negative-control').hexdigest()
        self.write_secret()
        self.assertTrue(self.scan('--all'))

    def test_full_history_catches_a_secret_outside_the_current_delta(self):
        self.write_secret()
        base = self.git('rev-parse', 'HEAD')
        (self.root / 'unrelated.txt').write_text('clean')
        self.commit()
        self.assertFalse(self.scan(base + '..HEAD'))
        self.assertTrue(self.scan('--all'))
