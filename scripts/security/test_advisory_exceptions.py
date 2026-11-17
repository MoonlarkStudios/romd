import copy
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import advisory_exceptions as exceptions
import source_advisories as source

ADVISORY = 'https://github.com/advisories/GHSA-fc5q-39xc-vjpf'
NOW = datetime(2026, 11, 17, tzinfo=timezone.utc)


def entry(**changes):
    return dict(advisory=ADVISORY, ecosystem='npm', scope='web/pnpm-lock.yaml', package='picomatch', version='4.0.7',
                developmentRoots=['vite', 'vitest'], owner='maintainer',
                rationale='Reached only through build and test tooling; patterns come from repository configuration.',
                tracking='https://github.com/micromatch/picomatch/issues/234', expiresAt='2026-12-01T00:00:00Z') | changes


def lock():
    """A workspace whose only routes to picomatch are the vite and vitest development dependencies."""
    return {'lockfileVersion': '9.0',
            'importers': {
                '.': {'devDependencies': {'vitest': {'specifier': '^4', 'version': '4.1.12'}}},
                'packages/app': {'dependencies': {'react': {'specifier': '^19', 'version': '19.2.0'},
                                                  '@romd/ui': {'specifier': 'workspace:*', 'version': 'link:../ui'}},
                                 'devDependencies': {'vite': {'specifier': '^7', 'version': '7.3.7(jiti@2.7.0)'}}},
                'packages/ui': {'dependencies': {'react': {'specifier': '^19', 'version': '19.2.0'}}}},
            'snapshots': {
                'react@19.2.0': {},
                'vitest@4.1.12': {'dependencies': {'vite': '7.3.7(jiti@2.7.0)', 'picomatch': '4.0.7'}},
                'vite@7.3.7(jiti@2.7.0)': {'dependencies': {'tinyglobby': '0.2.15'}, 'optionalDependencies': {'jiti': '2.7.0'}},
                'tinyglobby@0.2.15': {'dependencies': {'fdir': '6.5.0(picomatch@4.0.7)', 'picomatch': '4.0.7'}},
                'fdir@6.5.0(picomatch@4.0.7)': {'optionalDependencies': {'picomatch': '4.0.7'}},
                'jiti@2.7.0': {}, 'picomatch@4.0.7': {}}}


def finding(package='picomatch', version='4.0.7', advisory=ADVISORY, rating='HIGH'):
    return source.finding('npm', 'web/pnpm-lock.yaml', package, version, advisory, rating)


class AdvisoryExceptionTests(unittest.TestCase):
    def test_committed_exception_is_the_one_reviewed_entry(self):
        committed = exceptions.load()
        self.assertEqual([(ADVISORY, 'picomatch', '4.0.7', '2026-12-01T00:00:00Z')],
                         [(item['advisory'], item['package'], item['version'], item['expiresAt']) for item in committed])
        self.assertEqual(['@vitejs/plugin-react', '@vitest/coverage-v8', 'vite', 'vitest'], committed[0]['developmentRoots'])
        self.assertEqual('https://github.com/micromatch/picomatch/issues/234', committed[0]['tracking'])
        self.assertTrue(committed[0]['owner'].strip())

    def test_development_only_package_is_excepted_and_stays_in_the_report(self):
        findings = exceptions.apply([finding()], lock(), [entry()], NOW)
        self.assertEqual(1, len(findings))
        self.assertFalse(findings[0]['blocked'])
        self.assertEqual('HIGH', findings[0]['severity'])
        self.assertEqual(('applied', 'maintainer', '2026-12-01T00:00:00Z'),
                         tuple(findings[0]['exception'][key] for key in ('status', 'owner', 'expiresAt')))

    def test_only_the_exact_advisory_package_and_version_match(self):
        other = 'https://github.com/advisories/GHSA-1234-1234-1234'
        for label, unmatched in (('another advisory for the package', finding(advisory=other)),
                                 ('another version', finding(version='4.0.6')),
                                 ('another package', finding(package='micromatch'))):
            with self.subTest(label):
                findings = exceptions.apply([finding(), unmatched], lock(), [entry()], NOW)
                self.assertEqual([False, True], [item['blocked'] for item in findings])
                self.assertNotIn('exception', findings[1])
        browser = source.finding('npm', 'scripts/release/browser/package-lock.json', 'picomatch', '4.0.7', ADVISORY, 'HIGH')
        with self.assertRaisesRegex(ValueError, 'matches no finding'):
            exceptions.apply([browser], lock(), [entry()], NOW)
        self.assertTrue(browser['blocked'])

    def test_expiry_blocks_from_the_first_instant(self):
        last = datetime(2026, 11, 30, 23, 59, 59, tzinfo=timezone.utc)
        self.assertFalse(exceptions.apply([finding()], lock(), [entry()], last)[0]['blocked'])
        for moment in (datetime(2026, 12, 1, tzinfo=timezone.utc), datetime(2027, 1, 1, tzinfo=timezone.utc)):
            with self.subTest(moment=moment):
                expired = exceptions.apply([finding()], lock(), [entry()], moment)[0]
                self.assertTrue(expired['blocked'])
                self.assertEqual(('refused', 'expired'), (expired['exception']['status'], expired['exception']['reason']))
        distant = exceptions.apply([finding()], lock(), [entry(expiresAt='2027-06-01T00:00:00Z')], NOW)[0]
        self.assertTrue(distant['blocked'])
        self.assertIn('90-day limit', distant['exception']['reason'])

    def test_production_use_blocks(self):
        direct, linked, transitive = lock(), lock(), lock()
        direct['importers']['packages/app']['dependencies']['picomatch'] = {'specifier': '^4', 'version': '4.0.7'}
        # A workspace package that the application links is audited as its own importer.
        linked['importers']['packages/ui']['dependencies']['tinyglobby'] = {'specifier': '^0.2', 'version': '0.2.15'}
        transitive['snapshots']['react@19.2.0'] = {'optionalDependencies': {'picomatch': '4.0.7'}}
        for label, changed in (('direct', direct), ('linked workspace package', linked), ('transitive', transitive)):
            with self.subTest(label):
                refused = exceptions.apply([finding()], changed, [entry()], NOW)[0]
                self.assertTrue(refused['blocked'])
                self.assertEqual('the package is a production dependency', refused['exception']['reason'])

    def test_unreviewed_development_path_blocks(self):
        changed = lock()
        changed['importers']['.']['devDependencies']['tinyglobby'] = {'specifier': '^0.2', 'version': '0.2.15'}
        refused = exceptions.apply([finding()], changed, [entry()], NOW)[0]
        self.assertTrue(refused['blocked'])
        self.assertEqual('reached through unreviewed development dependencies: tinyglobby', refused['exception']['reason'])

    def test_development_only_status_that_cannot_be_established_blocks(self):
        def mutated(change):
            changed = lock()
            change(changed)
            return changed
        cases = {
            'another lockfile version': lambda l: l.update(lockfileVersion='10.0'),
            'no importers': lambda l: l.update(importers={}),
            'no snapshots': lambda l: l.pop('snapshots'),
            'unrecognized dependency kind': lambda l: l['importers']['.'].update(peerDependencies={}),
            'unresolved dependency': lambda l: l['snapshots'].pop('tinyglobby@0.2.15'),
            'link outside the workspace': lambda l: l['importers']['packages/app']['dependencies']['@romd/ui'].update(version='link:../../elsewhere'),
            'package absent from the lockfile': lambda l: l['snapshots'].pop('picomatch@4.0.7'),
            'no development dependency reaches it': lambda l: (l['importers']['.'].pop('devDependencies'),
                                                              l['importers']['packages/app'].pop('devDependencies')),
        }
        for label, change in cases.items():
            with self.subTest(label):
                refused = exceptions.apply([finding()], mutated(change), [entry()], NOW)[0]
                self.assertTrue(refused['blocked'])
                self.assertIn('could not be established', refused['exception']['reason'])
        for unparsed in (None, 'lockfileVersion: 9', []):
            with self.subTest(lock=unparsed):
                self.assertTrue(exceptions.apply([finding()], unparsed, [entry()], NOW)[0]['blocked'])

    def test_peer_qualified_snapshots_of_the_version_count(self):
        changed = lock()
        changed['snapshots']['picomatch@4.0.7(peer@1.0.0)'] = {}
        changed['snapshots']['react@19.2.0'] = {'dependencies': {'picomatch': '4.0.7(peer@1.0.0)'}}
        self.assertTrue(exceptions.apply([finding()], changed, [entry()], NOW)[0]['blocked'])

    def test_unused_exception_must_be_removed(self):
        with self.assertRaisesRegex(ValueError, 'matches no finding; remove it'):
            exceptions.apply([], lock(), [entry()], NOW)
        self.assertEqual([], exceptions.apply([], lock(), [], NOW))

    def test_malformed_broad_or_duplicate_exceptions_are_rejected(self):
        changes = [dict(advisory='https://github.com/advisories'), dict(advisory='GHSA-fc5q-39xc-vjpf'),
                   dict(ecosystem='nuget'), dict(scope='scripts/release/browser/package-lock.json'),
                   dict(package='*'), dict(package=''), dict(version='4.x'), dict(version='<=4.0.7'), dict(version='*'),
                   dict(developmentRoots=[]), dict(developmentRoots=['*']), dict(developmentRoots=['vite', 'vite']),
                   dict(developmentRoots='vite'), dict(owner=' '), dict(rationale='dev only'),
                   dict(tracking='https://example.test/issue/1'), dict(tracking=''),
                   dict(expiresAt='2026-12-01'), dict(expiresAt='2026-12-01T12:00:00Z'), dict(expiresAt='2026-13-01T00:00:00Z')]
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'exceptions.json'
            for change in changes:
                with self.subTest(change=change):
                    path.write_text(json.dumps({'exceptions': [entry(**change)]}))
                    with self.assertRaises(ValueError):
                        exceptions.load(path)
            extra, missing = entry(note='x'), entry()
            del missing['owner']
            for document in ({'exceptions': [extra]}, {'exceptions': [missing]}, {'exceptions': [entry(), entry()]},
                             {'exceptions': {}}, {'vulnerabilities': []}, [entry()]):
                with self.subTest(document=document):
                    path.write_text(json.dumps(document))
                    with self.assertRaises(ValueError):
                        exceptions.load(path)
            path.write_text('exceptions: []')
            with self.assertRaises(ValueError):
                exceptions.load(path)
            path.write_text(json.dumps({'exceptions': [entry()]}))
            self.assertEqual([entry()], exceptions.load(path))


class GateDispositionTests(unittest.TestCase):
    """The one gate the pull-request and release advisory checks both run."""

    def audit(self, npm):
        with tempfile.TemporaryDirectory() as temporary:
            report = Path(temporary) / 'report.json'
            scanners = {'npm': lambda root, work: (copy.deepcopy(npm), {})}
            with patch('builtins.print') as output:
                status = source.audit(Path(temporary), report, scanners)
            return status, json.loads(report.read_text())['ecosystems']['npm'], output.call_args.args[0]

    def test_excepted_finding_passes_the_gate_and_is_reported(self):
        status, npm, line = self.audit(exceptions.apply([finding()], lock(), [entry()], NOW))
        self.assertEqual((0, 'passed', 0), (status, npm['status'], npm['blocked']))
        self.assertEqual(('picomatch', 'HIGH', 'applied'),
                         (npm['findings'][0]['package'], npm['findings'][0]['severity'], npm['findings'][0]['exception']['status']))
        self.assertEqual('npm: 1 advisories, 0 blocking, 1 under a reviewed exception', line)

    def test_other_findings_and_refused_exceptions_still_fail_the_gate(self):
        other = finding(package='esbuild', version='0.27.2', advisory='https://github.com/advisories/GHSA-1234-1234-1234')
        status, npm, line = self.audit(exceptions.apply([finding(), other], lock(), [entry()], NOW))
        self.assertEqual((1, 'failed', 1), (status, npm['status'], npm['blocked']))
        self.assertEqual('npm: 2 advisories, 1 blocking, 1 under a reviewed exception', line)
        expired = exceptions.apply([finding()], lock(), [entry()], datetime(2026, 12, 1, tzinfo=timezone.utc))
        status, npm, _ = self.audit(expired)
        self.assertEqual((1, 'failed', 1), (status, npm['status'], npm['blocked']))
        self.assertEqual('expired', npm['findings'][0]['exception']['reason'])
        # A finding without a disposition blocks, as before.
        self.assertEqual(1, self.audit([finding()])[0])

    def test_pnpm_scan_applies_exceptions_to_the_committed_lockfile_and_fails_closed_on_policy_errors(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'web').mkdir()
            (root / 'web/pnpm-lock.yaml').write_text(json.dumps(lock()))  # JSON is valid YAML
            (root / 'web/package.json').write_text('{"packageManager":"pnpm@10.28.0"}')
            data = {'advisories': {'1': {'module_name': 'picomatch', 'severity': 'high', 'url': ADVISORY,
                                         'findings': [{'version': '4.0.7'}]}},
                    'metadata': {'vulnerabilities': dict(info=0, low=0, moderate=0, high=1, critical=0)}}
            result = source.subprocess.CompletedProcess([], 1, json.dumps(data), '')

            def scan(loaded):
                work = Path(tempfile.mkdtemp(dir=root))
                with patch.object(source, 'run', return_value=result) as run, \
                        patch.object(exceptions, 'load', side_effect=loaded if isinstance(loaded, Exception) else None,
                                     return_value=loaded), \
                        patch.dict('sys.modules', yaml=type('yaml', (), {'safe_load': staticmethod(json.loads)})):
                    findings = source.scan_pnpm(root, work)[0]
                # The audit itself is unchanged: full lockfile, no production-only or ignore flags.
                self.assertFalse({'--prod', '--dev'} & set(run.call_args.args[0]))
                self.assertIn('--ignore-unfixable=false', run.call_args.args[0])
                return findings

            self.assertEqual([False], [item['blocked'] for item in scan([entry()])])
            self.assertEqual([True], [item['blocked'] for item in scan([])])
            self.assertEqual([True], [item['blocked'] for item in scan([entry(developmentRoots=['vitest'])])])
            for failure in (ValueError('Advisory exceptions must be JSON'), [entry(package='left-pad')]):
                with self.subTest(failure=failure), self.assertRaises(source.ScanError):
                    scan(failure)


if __name__ == '__main__':
    unittest.main()
