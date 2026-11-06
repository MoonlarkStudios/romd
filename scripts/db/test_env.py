#!/usr/bin/env python3
"""Exercise pre-install database naming with an isolated system-tool PATH."""
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import unittest


HERE = Path(__file__).resolve().parent
CONNECTION_KEYS = ('ConnectionStrings__Romd', 'ConnectionStrings__RomdProvisioning',
                   'ConnectionStrings__Hangfire', 'ConnectionStrings__HangfireProvisioning')


class DatabaseEnvironmentTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.repo = self.work / 'Checkout Space'
        self.scripts = self.repo / 'scripts/db'
        self.scripts.mkdir(parents=True)
        for name in ('lib.sh', 'env.sh'):
            shutil.copy2(HERE / name, self.scripts / name)
        self.bin = self.work / 'bin'
        self.bin.mkdir()
        for command in ('basename', 'dirname', 'tr', 'cut', 'sed', 'tail'):
            (self.bin / command).symlink_to(shutil.which(command))
        self.environment = {key: value for key, value in os.environ.items()
                            if not key.startswith('ConnectionStrings__') and key not in ('BASH_ENV', 'ROMD_DB_PORT')}
        self.environment['PATH'] = str(self.bin)
        self.calls = self.work / 'hash-calls.jsonl'

    def provider(self, name, *, result=None, exit_code=0):
        script = self.bin / name
        script.write_text(f'#!{sys.executable}\n'
                          'import hashlib, json, pathlib, sys\n'
                          f'with pathlib.Path({str(self.calls)!r}).open("a") as stream:\n'
                          '    stream.write(json.dumps(sys.argv) + "\\n")\n'
                          'data = sys.stdin.buffer.read()\n'
                          f'print({result!r} if {result is not None!r} else hashlib.sha256(data).hexdigest() + "  -")\n'
                          f'raise SystemExit({exit_code})\n')
        script.chmod(0o755)

    def run_env(self, *, sourced=False, prior=None, path=None):
        environment = dict(self.environment, **(prior or {}))
        path = path or self.scripts / 'env.sh'
        command = ['/bin/bash', str(path)]
        if sourced:
            dump = 'import json, os; print(json.dumps({k: v for k, v in os.environ.items() if k.startswith("ConnectionStrings__")}))'
            command = ['/bin/bash', '-c', 'source "$1"; status=$?; "$2" -c "$3"; exit "$status"',
                       'test-env', str(path), sys.executable, dump]
        return subprocess.run(command, cwd=self.work, env=environment, capture_output=True, text=True)

    def expected(self, port='15432'):
        slug = re.sub('[^a-z0-9]', '_', self.repo.name.lower())[:24].removesuffix('_')
        suffix = hashlib.sha256(str(self.repo.resolve()).encode()).hexdigest()[:8]
        value = f'Host=127.0.0.1;Port={port};Database=romd_{slug}_{suffix};Username=romd'
        return dict.fromkeys(CONNECTION_KEYS, value)

    def test_both_hash_providers_keep_identity_and_export_exactly_four_values(self):
        for provider, arguments in (('sha256sum', []), ('shasum', ['-a', '256'])):
            with self.subTest(provider=provider):
                self.provider(provider)
                executed = self.run_env()
                self.assertEqual(0, executed.returncode, executed.stderr)
                exports = [shlex.split(line) for line in executed.stdout.splitlines()]
                self.assertTrue(all(len(line) == 2 and line[0] == 'export' for line in exports))
                self.assertEqual(4, len(exports))
                self.assertEqual(self.expected(), dict(line[1].split('=', 1) for line in exports))
                sourced = self.run_env(sourced=True)
                self.assertEqual(0, sourced.returncode, sourced.stderr)
                self.assertEqual(self.expected(), json.loads(sourced.stdout))
                calls = [json.loads(line) for line in self.calls.read_text().splitlines()]
                self.assertEqual([[str(self.bin / provider), *arguments]] * 2, calls)
                (self.bin / provider).unlink()
                self.calls.unlink()

    def test_sha256sum_is_preferred_when_both_providers_exist(self):
        self.provider('sha256sum')
        self.provider('shasum', exit_code=13)
        result = self.run_env(sourced=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.expected(), json.loads(result.stdout))
        self.assertEqual([[str(self.bin / 'sha256sum')]], [json.loads(line) for line in self.calls.read_text().splitlines()])

    def test_failed_missing_or_invalid_hash_never_exports_a_partial_connection(self):
        prior = {key: f'prior-{key}' for key in CONNECTION_KEYS}
        for provider in ('sha256sum', 'shasum', None):
            for result, exit_code in ((None, 17), ('12345678  -', 0), ('z' * 64 + '  -', 0)):
                with self.subTest(provider=provider, result=result, exit_code=exit_code):
                    if provider:
                        self.provider(provider, result=result, exit_code=exit_code)
                    executed = self.run_env()
                    self.assertNotEqual(0, executed.returncode)
                    self.assertEqual('', executed.stdout)
                    for initial in ({}, prior):
                        sourced = self.run_env(sourced=True, prior=initial)
                        self.assertNotEqual(0, sourced.returncode)
                        self.assertEqual(initial, json.loads(sourced.stdout))
                    if provider:
                        (self.bin / provider).unlink()

    def test_failed_preferred_provider_does_not_switch_database_identity(self):
        self.provider('sha256sum', exit_code=17)
        self.provider('shasum')
        result = self.run_env()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual('', result.stdout)
        self.assertEqual([[str(self.bin / 'sha256sum')]], [json.loads(line) for line in self.calls.read_text().splitlines()])

    def test_canonical_checkout_path_and_port_precedence_are_preserved(self):
        self.provider('sha256sum')
        alias = self.work / 'alias'
        alias.symlink_to(self.repo, target_is_directory=True)
        (self.repo / '.env').write_text('ROMD_DB_PORT=25432\n')
        result = self.run_env(sourced=True, path=alias / 'scripts/db/env.sh')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.expected('25432'), json.loads(result.stdout))
        self.environment['ROMD_DB_PORT'] = '35432'
        result = self.run_env(sourced=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.expected('35432'), json.loads(result.stdout))


if __name__ == '__main__':
    unittest.main()
