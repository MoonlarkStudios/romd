import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import source_advisories as source

SCOPE = 'scripts/release/browser/package-lock.json'
URL = 'https://github.com/advisories/GHSA-1234-1234-1234'


class JavascriptAdvisoryTests(unittest.TestCase):
    def packages(self):
        return {'': {'dependencies': {'playwright': '1.0.0'}},
                'node_modules/playwright': {'version': '1.0.0', 'integrity': 'sha512-example'}}

    def report(self, rating=None):
        totals = dict(info=0, low=0, moderate=0, high=0, critical=0, total=0)
        vulnerabilities = {}
        if rating:
            totals.update({rating: 1, 'total': 1})
            vulnerabilities['playwright'] = dict(name='playwright', severity=rating,
                nodes=['node_modules/playwright'], via=[dict(url=URL, severity=rating)])
        return dict(auditReportVersion=2, vulnerabilities=vulnerabilities,
                    metadata=dict(vulnerabilities=totals, dependencies=dict(total=1)))

    def test_npm_clean_and_high_findings_match_locked_versions_and_scope(self):
        self.assertEqual([], source.parse_npm(self.report(), self.packages(), SCOPE))
        self.assertEqual([source.finding('npm', SCOPE, 'playwright', '1.0.0', URL, 'HIGH')],
                         source.parse_npm(self.report('high'), self.packages(), SCOPE))

    def test_npm_missing_inventory_advisories_or_contradictory_totals_fail(self):
        for mutate in (lambda r: r.update(error={'code': 'E500'}),
                       lambda r: r.update(auditReportVersion=1),
                       lambda r: r.update(vulnerabilities={}),
                       lambda r: r['metadata']['dependencies'].update(total=0),
                       lambda r: r['metadata']['vulnerabilities'].update(high=0, low=1),
                       lambda r: r['vulnerabilities']['playwright'].update(nodes=[]),
                       lambda r: r['vulnerabilities']['playwright'].update(nodes=['node_modules/unlisted']),
                       lambda r: r['vulnerabilities']['playwright'].update(via=[]),
                       lambda r: r['vulnerabilities']['playwright'].update(via=['missing']),
                       lambda r: r['vulnerabilities']['playwright'].update(via=['playwright'])):
            report = self.report('high')
            mutate(report)
            with self.subTest(report=report), self.assertRaises(source.ScanError):
                source.parse_npm(report, self.packages(), SCOPE)

    def test_npm_indirect_vulnerabilities_keep_the_advisory_and_affected_version(self):
        report, packages = self.report('high'), self.packages()
        packages['node_modules/wrapper'] = {'version': '2.0.0'}
        report['vulnerabilities']['wrapper'] = dict(name='wrapper', severity='high',
            nodes=['node_modules/wrapper'], via=['playwright'])
        report['metadata']['vulnerabilities'].update(high=2, total=2)
        report['metadata']['dependencies']['total'] = 2
        findings = source.parse_npm(report, packages, SCOPE)
        self.assertIn(source.finding('npm', SCOPE, 'wrapper', '2.0.0', URL, 'HIGH'), findings)

    def test_new_manifests_lockfiles_and_missing_workspace_importers_fail_coverage(self):
        paths = {'web/package.json', 'web/packages/app/package.json',
                 'scripts/release/browser/package.json', *source.JAVASCRIPT_LOCKFILES}
        importers = {'.': {}, 'packages/app': {}}
        self.assertEqual(3, len(source.validate_javascript_coverage(paths, importers)))
        for changed_paths, changed_importers in (
            (paths | {'tools/new/package.json'}, importers),
            (paths | {'tools/new/package-lock.json'}, importers),
            (paths | {'web/yarn.lock'}, importers),
            (paths - {SCOPE}, importers),
            (paths, {'.': {}}), (paths, importers | {'../uncovered': {}}),
        ):
            with self.subTest(paths=changed_paths), self.assertRaises(source.ScanError):
                source.validate_javascript_coverage(changed_paths, changed_importers)

    def test_browser_audit_is_lock_only_and_cannot_inherit_ignore_configuration(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            browser = root / 'scripts/release/browser'
            browser.mkdir(parents=True)
            package = dict(dependencies={'playwright': '1.0.0'}, scripts={'preinstall': 'exit 99'})
            (browser / 'package.json').write_text(json.dumps(package))
            lock = dict(lockfileVersion=3, packages=self.packages())
            (browser / 'package-lock.json').write_text(json.dumps(lock))
            work = root / 'work'
            work.mkdir()
            result = subprocess.CompletedProcess([], 1, json.dumps(self.report('high')), '')
            with patch.object(source, 'run', return_value=result) as run, \
                 patch.dict(source.os.environ, {'NPM_CONFIG_OMIT': 'dev', 'npm_config_audit_level': 'critical'}):
                findings, coverage = source.scan_browser_npm(root, work)
            command = run.call_args.args[0]
            for flag in ('--package-lock-only', '--ignore-scripts', '--include=dev', '--include=optional', '--include=peer'):
                self.assertIn(flag, command)
            environment = run.call_args.kwargs['environment']
            self.assertNotIn('NPM_CONFIG_OMIT', environment)
            self.assertNotIn('npm_config_audit_level', environment)
            self.assertNotIn('scripts', json.loads((work / 'browser/package.json').read_text()))
            self.assertEqual(lock, json.loads((work / 'browser/package-lock.json').read_text()))
            self.assertEqual(SCOPE, coverage['lockfile'])
            self.assertEqual('HIGH', findings[0]['severity'])
            package['dependencies']['new'] = '1.0.0'
            (browser / 'package.json').write_text(json.dumps(package))
            with self.assertRaisesRegex(source.ScanError, 'differs from its lockfile'):
                source.scan_browser_npm(root, work)

    def test_browser_high_finding_blocks_the_default_gate(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            policy = root / 'scripts/security/policy.json'
            policy.parent.mkdir(parents=True)
            policy.write_text(json.dumps({'schemaVersion': 1, 'blockedSeverities': ['HIGH', 'CRITICAL', 'UNKNOWN']}))
            findings = source.parse_npm(self.report('high'), self.packages(), SCOPE)
            with patch.object(source, 'scan_nuget', return_value=([], {})), \
                 patch.object(source, 'scan_javascript', return_value=(findings, {})), \
                 patch.object(source, 'scan_pub', return_value=([], {})), \
                 patch.object(source, 'scan_go_reader', return_value=([], {})), patch('builtins.print'):
                self.assertEqual(1, source.audit(root, root / 'report.json'))
            self.assertEqual('failed', json.loads((root / 'report.json').read_text())['ecosystems']['npm']['status'])
