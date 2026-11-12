import copy
import json
import unittest

from postgres_helper import primary_matches, validate

DIGEST = 'sha256:' + 'a' * 64
BINARY = 'b' * 64
TAG = 'v0.1.0-rc.2'
MODIFIED = '2026-11-11T02:51:11Z'


class PostgresHelperTests(unittest.TestCase):
    def setUp(self):
        self.policy = {'assessments': [dict(id='CVE-2026-12345', component='pkg:golang/stdlib@v1.24.6',
            installedVersion='v1.24.6', subjectDigest=DIGEST, releaseTag=TAG,
            reviewEvidence={'binarySha256': BINARY, 'primaryOsv': 'GO-2026-1234'})]}
        self.events = [
            {'config': dict(protocol_version='v1.0.0', scanner_name='govulncheck', scanner_version='v1.9.0',
                db='https://vuln.go.dev', db_last_modified=MODIFIED, scan_level='symbol', scan_mode='binary')},
            {'SBOM': dict(roots=['github.com/tianon/gosu'], go_version='go1.24.6', modules=[
                dict(path='github.com/tianon/gosu', version='v1.19.0'), dict(path='stdlib', version='go1.24.6')])},
            {'osv': dict(id='GO-2026-1234', aliases=['CVE-2026-12345'], database_specific={'review_status': 'REVIEWED'},
                affected=[dict(package={'name': 'stdlib'}, ranges=[dict(type='SEMVER', events=[{'introduced': '0'}, {'fixed': '1.25.12'}])],
                    ecosystem_specific={'imports': [{'path': 'os', 'symbols': ['Root.Open']} ]})])},
            {'finding': dict(osv='GO-2026-1234', trace=[dict(module='stdlib', version='v1.24.6', package='os')])}]

    def validate(self):
        return validate('\n'.join(json.dumps(event) for event in self.events), BINARY, self.policy, DIGEST, TAG,
                        'v1.9.0', {'modified': MODIFIED})

    def test_complete_exact_helper_scan_confirms_reviewed_absent_symbols(self):
        self.assertIn('GO-2026-1234', self.validate())

    def test_changed_binary_present_function_unknown_alias_and_missing_coverage_fail(self):
        original = copy.deepcopy(self.events)
        for change in ('binary', 'function', 'alias', 'symbols', 'module', 'config'):
            with self.subTest(change=change):
                self.events = copy.deepcopy(original)
                self.policy['assessments'][0]['reviewEvidence']['binarySha256'] = BINARY
                if change == 'binary':
                    self.policy['assessments'][0]['reviewEvidence']['binarySha256'] = 'c' * 64
                elif change == 'function':
                    self.events[-1]['finding']['trace'][0]['function'] = 'Root.Open'
                elif change == 'alias':
                    self.events[2]['osv']['aliases'] = ['CVE-2026-99999']
                elif change == 'symbols':
                    self.events[2]['osv']['affected'][0]['ecosystem_specific']['imports'][0]['symbols'] = []
                elif change == 'module':
                    self.events[-1]['finding']['trace'][0]['version'] = 'v1.25.0'
                else:
                    self.events[0]['config']['scan_level'] = 'module'
                with self.assertRaises(ValueError):
                    self.validate()

    def test_platform_exclusion_requires_primary_platform_constraints(self):
        self.events.pop()
        with self.assertRaises(ValueError):
            self.validate()
        self.events[2]['osv']['affected'][0]['ecosystem_specific']['imports'][0]['goos'] = ['windows']
        self.validate()

    def test_only_documented_unserialized_related_metadata_can_differ(self):
        scanned = self.events[2]['osv']
        self.assertTrue(primary_matches(scanned, dict(scanned, related=['CVE-2026-99999'])))
        self.assertFalse(primary_matches(scanned, dict(scanned, aliases=['CVE-2026-99999'])))
        self.assertFalse(primary_matches(scanned, dict(scanned, affected=[])))
        self.assertFalse(primary_matches(scanned, dict(scanned, unknownFutureField=[])))
        self.assertFalse(primary_matches(scanned, dict(scanned, related='not-a-list')))


if __name__ == '__main__':
    unittest.main()
