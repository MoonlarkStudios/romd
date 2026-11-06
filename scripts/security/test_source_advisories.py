import copy
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import source_advisories as source
import source_tools


URL = 'https://github.com/advisories/GHSA-1234-1234-1234'


class SourceAdvisoryTests(unittest.TestCase):
    def test_default_gate_includes_go_reader_and_retains_raw_unknown_with_proven_absence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            policy = root / 'scripts/security/policy.json'
            policy.parent.mkdir(parents=True)
            policy.write_text(json.dumps({'schemaVersion': 1, 'blockedSeverities': ['HIGH', 'CRITICAL', 'UNKNOWN']}))
            report = root / 'report.json'
            go_finding = source.finding('go-reader', 'Dockerfile/catalog-reader', 'example', 'v1.0.0', URL, 'UNKNOWN')
            go_finding.update(rawBlocked=True, blocked=False, applicability={'status': 'affected-packages-absent'})
            with patch.object(source, 'scan_nuget', return_value=([], {})), \
                 patch.object(source, 'scan_javascript', return_value=([], {})), \
                 patch.object(source, 'scan_pub', return_value=([], {})), \
                 patch.object(source, 'scan_go_reader', return_value=([go_finding], {'compiler': 'go1.27.3'})) as go_scan, \
                 patch('builtins.print'):
                self.assertEqual(0, source.audit(root, report))
            data = json.loads(report.read_text())
            self.assertEqual({'nuget', 'npm', 'pub', 'go-reader'}, set(data['ecosystems']))
            retained = data['ecosystems']['go-reader']['findings'][0]
            self.assertEqual('UNKNOWN', retained['severity'])
            self.assertTrue(retained['rawBlocked'])
            self.assertFalse(retained['blocked'])
            self.assertEqual(root / 'go-reader-evidence', go_scan.call_args.args[1])
            self.assertEqual(0o700, (root / 'go-reader-evidence').stat().st_mode & 0o777)

    def nuget(self):
        package = {'id': 'Example', 'resolvedVersion': '1.2.3'}
        inventory = {'version': 1, 'parameters': '--include-transitive', 'projects': [
            {'path': 'src/Example.csproj', 'frameworks': [{'framework': 'net10.0',
                                                        'transitivePackages': [package]}]}]}
        audit = copy.deepcopy(inventory)
        audit['parameters'] += ' --vulnerable'
        audit['sources'] = ['https://api.nuget.org/v3/index.json']
        audit['projects'][0]['frameworks'][0]['transitivePackages'][0]['vulnerabilities'] = [
            {'severity': 'High', 'advisoryurl': URL}]
        return inventory, audit

    def parse_nuget(self, inventory, audit):
        return source.parse_nuget(inventory, audit, Path('/checkout'), {'src/Example.csproj'})

    def test_nuget_transitive_findings_and_clean_project_coverage(self):
        inventory, audit = self.nuget()
        findings, count = self.parse_nuget(inventory, audit)
        self.assertEqual((findings[0]['package'], findings[0]['severity'], count), ('Example', 'HIGH', 1))
        audit['projects'][0].pop('frameworks')
        self.assertEqual(([], 1), self.parse_nuget(inventory, audit))

    def test_nuget_missing_empty_duplicate_error_and_unknown_coverage_fail(self):
        inventory, audit = self.nuget()
        for changed in ({}, inventory | {'version': 2}, inventory | {'projects': []},
                        inventory | {'problems': [{'level': 'error'}]},
                        inventory | {'projects': inventory['projects'] * 2},
                        inventory | {'projects': [{'path': 'src/Example.csproj', 'frameworks': []}]},
                        inventory | {'parameters': ''}):
            with self.subTest(changed=changed), self.assertRaises(source.ScanError):
                self.parse_nuget(changed, audit)
        for changed in (audit | {'projects': []}, audit | {'sources': []}, audit | {'parameters': ''},
                        audit | {'projects': [{'path': '/private/elsewhere.csproj'}]}):
            with self.subTest(changed=changed), self.assertRaises(source.ScanError):
                self.parse_nuget(inventory, changed)

    def test_nuget_findings_must_match_resolved_inventory_and_have_advisory_details(self):
        for field, value in (('resolvedVersion', '9.0.0'), ('vulnerabilities', [])):
            inventory, audit = self.nuget()
            audit['projects'][0]['frameworks'][0]['transitivePackages'][0][field] = value
            with self.assertRaises(source.ScanError):
                self.parse_nuget(inventory, audit)

    def pnpm(self, rating='high'):
        totals = dict(info=0, low=0, moderate=0, high=0, critical=0)
        totals[rating] = 1
        return {'advisories': {'42': {'module_name': '@example/package', 'severity': rating, 'url': URL,
                                     'findings': [{'version': '1.0.0'}]}},
                'metadata': {'vulnerabilities': totals}}

    def test_pnpm_keeps_development_lower_and_unfixed_advisories(self):
        data = self.pnpm('moderate')
        data['advisories']['42']['patched_versions'] = '<0.0.0'
        data['advisories']['42']['findings'][0]['dev'] = True
        self.assertEqual('MEDIUM', source.parse_pnpm(data)[0]['severity'])
        data['advisories'] = {}
        data['metadata']['vulnerabilities']['moderate'] = 0
        self.assertEqual([], source.parse_pnpm(data))

    def test_pnpm_errors_schema_changes_and_missing_details_fail(self):
        for data in ({}, {'error': {'code': 'FETCH_FAILED'}}, {'auditReportVersion': 2},
                     self.pnpm() | {'advisories': {}},
                     self.pnpm() | {'metadata': {'vulnerabilities': {'high': 1}}}):
            with self.subTest(data=data), self.assertRaises(source.ScanError):
                source.parse_pnpm(data)

    def test_pnpm_missing_high_details_cannot_hide_behind_a_low_finding(self):
        data = self.pnpm('low')
        data['metadata']['vulnerabilities']['high'] = 1
        with self.assertRaisesRegex(source.ScanError, 'severity totals'):
            source.parse_pnpm(data)

    def pub(self):
        return {'SchemaVersion': 2, 'Results': [{'Class': 'lang-pkgs', 'Type': 'pub',
                'Packages': [{'Name': 'example', 'Version': '1.0.0'}],
                'Vulnerabilities': [{'PkgName': 'example', 'InstalledVersion': '1.0.0',
                                     'PrimaryURL': URL, 'Severity': 'UNKNOWN'}]}]}

    def test_pub_reports_unknown_severity_and_requires_all_hosted_packages(self):
        data = self.pub()
        self.assertEqual('UNKNOWN', source.parse_pub(data, {('example', '1.0.0')})[0]['severity'])
        for changed, expected in (({}, {('example', '1.0.0')}),
                                  (data | {'Results': []}, {('example', '1.0.0')}),
                                  (data, {('dev_package', '2.0.0')}), (data, set())):
            with self.assertRaises(source.ScanError):
                source.parse_pub(changed, expected)
        data['Results'][0]['Vulnerabilities'] = []
        self.assertEqual([], source.parse_pub(data, {('example', '1.0.0')}))

    def test_arbitrary_urls_severities_and_control_text_are_rejected(self):
        for url in ('https://secret:credential@github.com/advisories/x', URL + '?token=secret',
                    'https://example.test/private', URL + '\nprivate', 'http://github.com/advisories/x'):
            with self.assertRaises(source.ScanError):
                source.advisory_url(url)
        for rating in (None, '', 'IMPORTANT', 'NONE'):
            with self.assertRaises(source.ScanError):
                source.severity(rating)
        with self.assertRaises(source.ScanError):
            source.token('package\nprivate')

    def test_scanner_nonzero_stderr_timeout_and_nonjson_are_failures(self):
        with patch.object(source.subprocess, 'run', return_value=subprocess.CompletedProcess([], 2, '{}', '')):
            with self.assertRaises(source.ScanError):
                source.run(['scanner'], Path('.'))
        with patch.object(source.subprocess, 'run', side_effect=subprocess.TimeoutExpired('scanner', 1)):
            with self.assertRaises(source.ScanError):
                source.run(['scanner'], Path('.'))
        for result in (subprocess.CompletedProcess([], 0, '{}', 'warning'),
                       subprocess.CompletedProcess([], 0, 'not json', ''),
                       subprocess.CompletedProcess([], 0, '[]', '')):
            with self.assertRaises(source.ScanError):
                source.decode(result)

    def test_scan_continues_after_failure_and_retains_only_sanitized_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            policy = root / 'scripts/security/policy.json'
            policy.parent.mkdir(parents=True)
            policy.write_text(json.dumps({'schemaVersion': 1, 'blockedSeverities': ['HIGH', 'CRITICAL', 'UNKNOWN']}))
            report = root / 'report.json'

            def error(*args):
                raise OSError('https://secret:credential@private.example.test')

            def high(*args):
                return [source.finding('npm', 'web/pnpm-lock.yaml', 'example', '1.0.0', URL, 'HIGH')], {}

            with patch('builtins.print'):
                self.assertEqual(1, source.audit(root, report, {'nuget': error, 'npm': high,
                                                              'pub': lambda *args: ([], {})}))
            text = report.read_text()
            data = json.loads(text)
            self.assertNotIn('credential', text)
            self.assertNotIn(temporary, text)
            self.assertEqual('failed', data['status'])
            self.assertEqual('error', data['ecosystems']['nuget']['status'])
            self.assertEqual('failed', data['ecosystems']['npm']['status'])
            self.assertEqual('passed', data['ecosystems']['pub']['status'])

    def test_interruption_cannot_leave_a_passed_partial_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            policy = root / 'scripts/security/policy.json'
            policy.parent.mkdir(parents=True)
            policy.write_text(json.dumps({'schemaVersion': 1, 'blockedSeverities': ['HIGH', 'CRITICAL', 'UNKNOWN']}))
            report = root / 'report.json'

            def interrupted(*args):
                raise KeyboardInterrupt()

            with patch('builtins.print'), self.assertRaises(KeyboardInterrupt):
                source.audit(root, report, {'nuget': lambda *args: ([], {}), 'pub': interrupted})
            data = json.loads(report.read_text())
            self.assertEqual('running', data['status'])
            self.assertEqual('passed', data['ecosystems']['nuget']['status'])
            self.assertEqual('not-run', data['ecosystems']['pub']['status'])

    def test_pnpm_uses_full_lockfile_with_neutral_configuration_and_no_ignore_flags(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'web').mkdir()
            (root / 'web/pnpm-lock.yaml').write_text('all workspace importers and dev dependencies')
            (root / 'web/package.json').write_text('{"packageManager":"pnpm@10.28.0"}')
            work = root / 'work'
            work.mkdir()
            data = self.pnpm()
            result = subprocess.CompletedProcess([], 1, json.dumps(data), '')
            with patch.object(source, 'run', return_value=result) as run:
                findings, coverage = source.scan_pnpm(root, work)
            command = run.call_args.args[0]
            self.assertNotIn('--prod', command)
            self.assertNotIn('--dev', command)
            self.assertIn('--ignore-unfixable=false', command)
            self.assertEqual((root / 'web/pnpm-lock.yaml').read_bytes(), (work / 'web/pnpm-lock.yaml').read_bytes())
            self.assertTrue(coverage['developmentAndOptionalDependencies'])
            self.assertEqual(1, len(findings))

    def test_pnpm_nonzero_with_clean_report_cannot_pass(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'web').mkdir()
            (root / 'web/pnpm-lock.yaml').write_text('lockfileVersion: 9')
            (root / 'web/package.json').write_text('{"packageManager":"pnpm@10.28.0"}')
            work = root / 'work'
            work.mkdir()
            data = {'advisories': {}, 'metadata': {'vulnerabilities':
                    dict(info=0, low=0, moderate=0, high=0, critical=0)}}
            result = subprocess.CompletedProcess([], 1, json.dumps(data), '')
            with patch.object(source, 'run', return_value=result), \
                 self.assertRaisesRegex(source.ScanError, 'exit status contradicts'):
                source.scan_pnpm(root, work)


class SourceToolTests(unittest.TestCase):
    def archive(self, directory):
        binary = bytearray(64)
        binary[:4] = b'\x7fELF'
        binary[18:20] = b'\x3e\x00'
        archive = directory / 'trivy.tar.gz'
        with tarfile.open(archive, 'w:gz') as bundle:
            member = tarfile.TarInfo('trivy')
            member.size = len(binary)
            bundle.addfile(member, io.BytesIO(binary))
        pins = directory / 'pins.json'
        pins.write_text(json.dumps({'trivy': {'sha256': hashlib.sha256(archive.read_bytes()).hexdigest()}}))
        return pins, binary

    def test_only_linux_x64_is_supported_and_never_silently_skipped(self):
        for system, machine in (('Darwin', 'x86_64'), ('Linux', 'aarch64'), ('Windows', 'AMD64')):
            with patch.object(source_tools.platform, 'system', return_value=system), \
                 patch.object(source_tools.platform, 'machine', return_value=machine), self.assertRaises(ValueError):
                source_tools.ensure_trivy(Path('/unused'))

    def test_cached_archive_and_executable_are_reverified_on_each_use(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            pins, binary = self.archive(directory)
            with patch.object(source_tools, 'PINS', pins), \
                 patch.object(source_tools.platform, 'system', return_value='Linux'), \
                 patch.object(source_tools.platform, 'machine', return_value='x86_64'):
                executable = source_tools.ensure_trivy(directory)
                self.assertEqual(binary, executable.read_bytes())
                executable.write_bytes(b'tampered')
                with self.assertRaises(ValueError):
                    source_tools.ensure_trivy(directory)
                executable.unlink()
                (directory / 'trivy.tar.gz').write_bytes(b'tampered')
                with self.assertRaises(ValueError):
                    source_tools.ensure_trivy(directory)


if __name__ == '__main__':
    unittest.main()
