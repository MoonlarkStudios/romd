"""A readiness report must describe verified evidence, including failed runs."""
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import readiness
from test_rehearsal import PINS, REVISION, TAG, fixture, write_json


class ReadinessTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.output = self.root / 'evidence'
        self.repo = self.root / 'repo'
        write_json(self.repo / 'scripts/deployment/base-images.json', PINS)
        self.patcher = patch.object(readiness, 'ROOT', self.repo)
        self.patcher.start()
        self.addCleanup(self.patcher.stop)
        self.source = patch.object(readiness, 'verify_source')
        self.source.start()
        self.addCleanup(self.source.stop)
        self.stages = []

    def command(self, argv, stage):
        self.stages.append(stage)
        self.assertIn(REVISION, argv)
        self.assertIn(TAG, argv)
        if stage == readiness.GATES[2]:
            (self.output / 'rehearsal').mkdir()
            fixture(self.output / 'rehearsal')
        return dict(stage=stage, exitCode=0)

    def test_success_requires_publication_verifier_and_reports_exact_digests(self):
        report = readiness.readiness(self.output, REVISION, TAG, command=self.command)
        self.assertEqual('passed', report['result'])
        self.assertEqual(['passed'] * 3, list(report['gates'].values()))
        self.assertEqual(5, len(report['images']))
        self.assertEqual(report, json.loads((self.output / 'readiness.json').read_text()))
        self.assertIn('does not qualify a published release', (self.output / 'summary.md').read_text())
        self.assertNotIn('ghcr.io', (self.output / 'summary.md').read_text())

    def test_build_failure_stops_before_rehearsal_and_retains_report(self):
        def failed(argv, stage):
            self.stages.append(stage)
            return dict(stage=stage, exitCode=1)
        report = readiness.readiness(self.output, REVISION, TAG, command=failed)
        self.assertEqual('failed', report['result'])
        self.assertEqual([readiness.GATES[1]], self.stages)
        self.assertEqual('not run', report['gates'][readiness.GATES[2]])
        self.assertEqual(readiness.GATES[1], report['blockingStage'])

    def test_exit_zero_without_bound_evidence_cannot_report_ready(self):
        report = readiness.readiness(self.output, REVISION, TAG,
                                     command=lambda argv, stage: dict(stage=stage, exitCode=0))
        self.assertEqual('failed', report['result'])
        self.assertEqual('failed', report['gates'][readiness.GATES[2]])
        self.assertNotIn('images', report)

    def test_changed_or_foreign_evidence_cannot_report_ready(self):
        for fault in ('changed-scan', 'wrong-source', 'failed-cleanup'):
            with self.subTest(fault=fault), tempfile.TemporaryDirectory() as temporary:
                self.output = Path(temporary) / 'evidence'
                def altered(argv, stage):
                    record = self.command(argv, stage)
                    if stage == readiness.GATES[2]:
                        root = self.output / 'rehearsal'
                        if fault == 'changed-scan':
                            (root / 'security/worker.sbom.trivy.json').write_text('{}')
                        else:
                            receipt = json.loads((root / 'receipt.json').read_text())
                            receipt['sourceCommit' if fault == 'wrong-source' else 'registryCleanup'] = 'bad'
                            write_json(root / 'receipt.json', receipt)
                    return record
                report = readiness.readiness(self.output, REVISION, TAG, command=altered)
                self.assertEqual('failed', report['result'])
                self.assertNotIn('receiptSha256', report)

    def test_existing_evidence_and_private_exception_text_are_not_reused(self):
        self.output.mkdir()
        (self.output / 'readiness.json').write_text('original evidence')
        with self.assertRaises(FileExistsError):
            readiness.readiness(self.output, REVISION, TAG, command=self.command)
        self.assertEqual('original evidence', (self.output / 'readiness.json').read_text())
        self.output = self.root / 'new-evidence'
        def private_failure(argv, stage):
            raise RuntimeError('private-token-do-not-publish')
        readiness.readiness(self.output, REVISION, TAG, command=private_failure)
        for name in ('readiness.json', 'summary.md'):
            self.assertNotIn('private-token', (self.output / name).read_text())

    def test_failed_runtime_report_names_only_fixed_nested_blockers(self):
        def failed_runtime(argv, stage):
            record = self.command(argv, stage)
            if stage == readiness.GATES[2]:
                root = self.output / 'rehearsal'
                receipt = json.loads((root / 'receipt.json').read_text())
                receipt.update(result='failed', failure='private-token-do-not-publish', commands=[
                    dict(stage='pre-publication-image-security', exitCode=0),
                    dict(stage='packaged-positive-and-negative-suite', exitCode=1),
                    dict(stage='private-token-do-not-publish', exitCode=1)])
                write_json(root / 'receipt.json', receipt)
                path = root / 'packaged/positive/qualification.json'
                qualification = json.loads(path.read_text())
                qualification['commands'] = [dict(stage='browser-after-restore', exitCode=1)]
                write_json(path, qualification)
                record['exitCode'] = 1
            return record
        report = readiness.readiness(self.output, REVISION, TAG, command=failed_runtime)
        self.assertEqual('failed', report['result'])
        self.assertEqual(['packaged-positive-and-negative-suite', 'browser-after-restore'], report['blockingChecks'])
        summary = (self.output / 'summary.md').read_text()
        self.assertIn('browser-after-restore', summary)
        self.assertNotIn('private-token', summary)
        self.assertEqual([], readiness.failure_detail(self.output, 'f' * 40, TAG))

    def test_child_output_is_hashed_without_retaining_secrets_or_tokens(self):
        with patch.dict(os.environ, {'GITHUB_TOKEN': 'private-token', 'GH_TOKEN': 'other-token',
                                    'GITHUB_OUTPUT': '/private/output', 'ROMD_SECRET': 'private-secret'}), \
                contextlib.redirect_stdout(io.StringIO()) as visible:
            record = readiness.run_step([sys.executable, '-c',
                "import os; assert not any(k in os.environ for k in "
                "('GITHUB_TOKEN', 'GH_TOKEN', 'GITHUB_OUTPUT', 'ROMD_SECRET')); print('private-output')"],
                readiness.GATES[1])
        self.assertEqual(0, record['exitCode'])
        self.assertEqual(64, len(record['outputSha256']))
        self.assertNotIn('private-output', visible.getvalue())
        self.assertNotIn('private-output', json.dumps(record))


class SourceIdentityTests(unittest.TestCase):
    def test_version_checkout_and_cleanliness_are_required_without_a_tag(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            def git(*args):
                return subprocess.check_output(['git', *args], cwd=root, text=True, stderr=subprocess.DEVNULL).strip()
            git('init')
            (root / 'source.txt').write_text('source')
            git('add', 'source.txt')
            git('-c', 'commit.gpgsign=false', '-c', 'user.name=Fixture', '-c',
                'user.email=fixture@example.test', 'commit', '-m', 'test fixture')
            revision = git('rev-parse', 'HEAD')
            self.assertEqual('', git('tag'))
            readiness.verify_source(root, revision, TAG)
            for bad_revision, tag in ((revision[:12], TAG), ('f' * 40, TAG),
                                      (revision, 'v1.0.0-rc.01'), (revision, 'v1.0.0\ninjected')):
                with self.subTest(revision=bad_revision, tag=tag), self.assertRaises(ValueError):
                    readiness.verify_source(root, bad_revision, tag)
            (root / 'untracked.txt').write_text('uncommitted')
            with self.assertRaisesRegex(ValueError, 'clean checkout'):
                readiness.verify_source(root, revision, TAG)


if __name__ == '__main__':
    unittest.main()
