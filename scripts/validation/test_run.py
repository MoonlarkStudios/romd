import io
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from contextlib import redirect_stdout

import run
from run import execute

HERE = Path(__file__).resolve().parent


def check(id, code=0, text='ok'):
    return {'id': id, 'cwd': '.', 'groups': ['demo'], 'ci_job': 'demo', 'required_ci': True,
            'local': [sys.executable, '-c', f'print({text!r}); raise SystemExit({code})'],
            'ci': [sys.executable, '-c', f'raise SystemExit({code})']}


class RunEvidenceTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.repo = Path(temporary.name) / 'repo'
        self.evidence = Path(temporary.name) / 'evidence'
        self.evidence.mkdir()
        self.repo.mkdir()
        git = ['git', '-C', str(self.repo), '-c', 'user.name=Test', '-c', 'user.email=test@example.test']
        subprocess.run(['git', 'init', '--quiet', str(self.repo)], check=True)
        (self.repo / 'tracked.txt').write_text('one\n')
        (self.repo / '.gitignore').write_text('ignored/\n')
        subprocess.run(git + ['add', '.'], check=True)
        subprocess.run(git + ['commit', '-qm', 'fixture'], check=True)
        self.head = subprocess.check_output(git + ['rev-parse', 'HEAD'], text=True).strip()

    def execute(self, checks, **kwargs):
        with redirect_stdout(io.StringIO()) as output:
            code = execute(checks, self.repo, self.evidence, **kwargs)
        self.output = output.getvalue()
        return code, json.loads((self.evidence / 'results.json').read_text())

    def test_fail_fast_records_not_run_checks(self):
        code, result = self.execute([check('a'), check('b', 3), check('c'), check('d')])
        self.assertEqual(3, code)
        self.assertEqual(2, result['schemaVersion'])
        self.assertEqual('failed', result['status'])
        self.assertEqual(3, result['exitCode'])
        self.assertEqual(['passed', 'failed', 'not-run', 'not-run'], [c['status'] for c in result['checks']])
        self.assertEqual(['c', 'd'], result['notRun'])
        self.assertIsNone(result['checks'][2]['artifact'])
        self.assertIn('Not run: c, d', self.output)

    def test_keep_going_runs_every_check_and_reports_first_failure(self):
        code, result = self.execute([check('a', 4), check('b'), check('c', 5)], keep_going=True)
        self.assertEqual(4, code)
        self.assertEqual('failed', result['status'])
        self.assertEqual(['failed', 'passed', 'failed'], [c['status'] for c in result['checks']])
        self.assertEqual([4, 0, 5], [c['exit_code'] for c in result['checks']])
        self.assertEqual([], result['notRun'])
        self.assertTrue(result['selector']['keepGoing'])

    def test_status_timestamps_and_durations_are_recorded(self):
        code, result = self.execute([check('a'), check('b')], selector={'group': 'demo', 'job': None, 'ids': None})
        self.assertEqual(0, code)
        self.assertEqual('passed', result['status'])
        self.assertEqual({'group': 'demo', 'job': None, 'ids': None, 'ci': False, 'filter': None,
                          'keepGoing': False}, result['selector'])
        self.assertEqual(self.head, result['revision'])
        self.assertFalse(result['dirty'])
        self.assertNotIn('tree', result)
        self.assertFalse(result['treeChangedDuringRun'])
        self.assertLessEqual(result['started'], result['finished'])
        self.assertGreaterEqual(result['durationSeconds'], 0)
        for entry in result['checks']:
            self.assertEqual('passed', entry['status'])
            self.assertIsInstance(entry['durationSeconds'], float)
            self.assertIsNotNone(entry['started'])
            self.assertEqual('ok', Path(entry['artifact']).read_text().strip())
        self.assertNotIn('uncommitted', self.output)

    def test_dirty_tree_records_hashes_without_contents(self):
        (self.repo / 'ignored').mkdir()
        (self.repo / 'ignored/private.bin').write_text('ignored')
        _, clean = self.execute([check('a')])
        self.assertFalse(clean['dirty'])

        (self.repo / 'tracked.txt').write_text('secret-modification\n')
        _, modified = self.execute([check('a')])
        self.assertTrue(modified['dirty'])
        self.assertEqual(self.head, modified['revision'])
        self.assertRegex(modified['tree']['diffSha256'], r'^[0-9a-f]{64}$')
        self.assertEqual(0, modified['tree']['untrackedCount'])
        self.assertIsNone(modified['tree']['untrackedSha256'])
        self.assertIn(f'results describe uncommitted changes on top of {self.head}', self.output)

        (self.repo / 'new-file.txt').write_text('secret-untracked\n')
        _, untracked = self.execute([check('a')])
        self.assertEqual(modified['tree']['diffSha256'], untracked['tree']['diffSha256'])
        self.assertEqual(1, untracked['tree']['untrackedCount'])
        first_hash = untracked['tree']['untrackedSha256']
        (self.repo / 'new-file.txt').write_text('changed\n')
        _, changed = self.execute([check('a')])
        self.assertNotEqual(first_hash, changed['tree']['untrackedSha256'])

        raw = (self.evidence / 'results.json').read_text()
        for private in ('secret', 'new-file.txt', 'tracked.txt', 'private.bin'):
            self.assertNotIn(private, raw)

    def test_tree_change_during_run_is_flagged(self):
        mutate = {'id': 'mutate', 'cwd': '.', 'local': [sys.executable, '-c', 'open("late.txt", "w").write("x")']}
        _, result = self.execute([mutate])
        self.assertFalse(result['dirty'])
        self.assertTrue(result['treeChangedDuringRun'])
        self.assertIn('working tree changed', self.output)

    def test_missing_filter_fails_before_running_anything(self):
        filtered = check('filtered') | {'local': ['echo', '{filter}']}
        with redirect_stdout(io.StringIO()), self.assertRaises(ValueError):
            execute([check('a'), filtered], self.repo, self.evidence)
        result = json.loads((self.evidence / 'results.json').read_text())
        self.assertEqual('error', result['status'])
        self.assertEqual(['a', 'filtered'], result['notRun'])

    def interrupt(self, signum):
        driver = (
            'import sys; sys.path.insert(0, sys.argv[1]); from pathlib import Path; import run; '
            'checks = [{"id": "first", "cwd": ".", "local": [sys.executable, "-c", "pass"]}, '
            '{"id": "slow", "cwd": ".", "local": [sys.executable, "-c", "import time; time.sleep(60)"]}, '
            '{"id": "after", "cwd": ".", "local": [sys.executable, "-c", "pass"]}]; '
            'sys.exit(run.execute(checks, Path(sys.argv[2]), Path(sys.argv[3])))')
        process = subprocess.Popen([sys.executable, '-B', '-W', 'error', '-c', driver,
                                    str(HERE), str(self.repo), str(self.evidence)],
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        results = self.evidence / 'results.json'
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            try:
                if json.loads(results.read_text())['checks'][1]['status'] == 'running':
                    break
            except (OSError, ValueError):
                pass
            time.sleep(0.05)
        else:
            process.kill()
            self.fail('slow check never started')
        time.sleep(0.2)
        process.send_signal(signum)
        output, _ = process.communicate(timeout=30)
        self.assertEqual(128 + signum, process.returncode, output)
        result = json.loads(results.read_text())
        self.assertEqual('interrupted', result['status'])
        self.assertEqual(['passed', 'interrupted', 'not-run'], [c['status'] for c in result['checks']])
        self.assertEqual(['after'], result['notRun'])
        self.assertIsNotNone(result['finished'])
        self.assertIsNotNone(result['checks'][1]['durationSeconds'])
        self.assertFalse((self.evidence / '.results.json.tmp').exists())

    def test_sigterm_leaves_interrupted_partial_record(self):
        self.interrupt(signal.SIGTERM)

    def test_sigint_leaves_interrupted_partial_record(self):
        self.interrupt(signal.SIGINT)

    def test_main_names_evidence_and_records_selector(self):
        registry = {'checks': [check('demo.one'), check('demo.two', 6)]}
        with redirect_stdout(io.StringIO()) as output:
            code = run.main(['--group', 'demo', '--evidence-parent', str(self.evidence)], registry=registry)
        self.assertEqual(6, code)
        [directory] = [p for p in self.evidence.iterdir() if p.is_dir()]
        self.assertRegex(directory.name, r'^\d{8}T\d{6}Z-demo-\w+$')
        self.assertIn(f'Validation evidence: {directory.resolve()}', output.getvalue())
        result = json.loads((directory / 'results.json').read_text())
        self.assertEqual('demo', result['selector']['group'])
        self.assertEqual([], result['notRun'])

        with redirect_stdout(io.StringIO()):
            run.main(['--id', 'demo.one', '--evidence-parent', str(self.evidence / 'ids')], registry=registry)
        [single] = list((self.evidence / 'ids').iterdir())
        self.assertTrue(re.match(r'^\d{8}T\d{6}Z-demo\.one-', single.name), single.name)

    def test_list_does_not_create_evidence(self):
        registry = {'checks': [check('demo.one')]}
        with redirect_stdout(io.StringIO()) as output:
            self.assertEqual(0, run.main(['--group', 'demo', '--list', '--evidence-parent',
                                          str(self.evidence / 'unused')], registry=registry))
        self.assertEqual('demo.one\n', output.getvalue())
        self.assertFalse((self.evidence / 'unused').exists())


if __name__ == '__main__':
    unittest.main()
