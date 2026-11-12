"""Security decisions must be specific, auditable and distinct from raw scans."""
import copy
from datetime import date
import json
from pathlib import Path
import tempfile
import unittest

from policy import evaluate, validate_policy
from vex import document
from verify import verify_policy_results

DIGEST = 'sha256:' + 'a' * 64
TAG = 'v0.1.0-rc.2'
TODAY = date(2026, 11, 12)


class ReviewPolicyTests(unittest.TestCase):
    def setUp(self):
        self.policy = dict(schemaVersion=2, blockedSeverities=['HIGH', 'CRITICAL', 'UNKNOWN'],
                           maxExceptionDays=90, assessments=[], exceptions=[])
        self.vulnerability = dict(VulnerabilityID='CVE-2026-12345', PkgName='example', InstalledVersion='1.2.3',
                                  Severity='HIGH', PkgIdentifier={'PURL': 'pkg:deb/debian/example@1.2.3'})
        self.report = dict(SchemaVersion=2, Results=[dict(Type='debian', Vulnerabilities=[self.vulnerability])])
        self.review = dict(id='CVE-2026-12345', component='pkg:deb/debian/example@1.2.3', installedVersion='1.2.3',
                           subjectDigest=DIGEST, severity='HIGH', owner='ROMD maintainers', reviewed='2026-11-12',
                           expires='2026-12-12', releaseTag=TAG,
                           evidence=['https://example.com/security/CVE-2026-12345'],
                           rationale='The vulnerable optional service is absent from this exact deployment image.',
                           justification='vulnerable_code_not_in_execute_path')

    def evaluate(self, **kwargs):
        return evaluate(self.report, DIGEST, self.policy, TODAY, release_tag=kwargs.get('tag', TAG))

    def accept_risk(self):
        self.review.pop('justification')
        self.review.update(fixedVersion=None, mitigations='Only authenticated application roles can reach the private database.',
                           followUp='Refresh the upstream image and reassess before the next release candidate.')
        self.policy['exceptions'] = [self.review]

    def test_non_applicability_preserves_raw_identity_severity_and_evidence(self):
        original = copy.deepcopy(self.report)
        self.policy['assessments'] = [self.review]
        result = self.evaluate()
        finding = result['findings'][0]
        self.assertEqual('passed', result['result'])
        self.assertEqual(original, self.report)
        self.assertTrue(finding['rawBlocked'])
        self.assertFalse(finding['excepted'])
        self.assertEqual('HIGH', finding['severity'])
        self.assertEqual('not_affected', finding['applicability']['status'])
        self.assertEqual(self.review['evidence'], finding['applicability']['evidence'])

    def test_accepted_risk_stays_affected_and_only_covers_the_reviewed_candidate(self):
        self.accept_risk()
        finding = self.evaluate()['findings'][0]
        self.assertFalse(finding['blocked'])
        self.assertTrue(finding['excepted'])
        self.assertEqual('affected', finding['applicability']['status'])
        self.assertEqual('accepted_risk', finding['riskAcceptance']['status'])
        for tag in ('v0.1.0', 'v0.1.0-rc.3', None):
            self.assertEqual('failed', self.evaluate(tag=tag)['result'])

    def test_available_fix_or_changed_severity_invalidates_risk_acceptance(self):
        self.accept_risk()
        self.vulnerability['FixedVersion'] = '1.2.4'
        self.assertEqual('failed', self.evaluate()['result'])
        del self.vulnerability['FixedVersion']
        self.vulnerability['Severity'] = 'CRITICAL'
        self.assertEqual('failed', self.evaluate()['result'])

    def test_go_stdlib_image_and_sbom_spellings_bind_only_the_same_version(self):
        self.review.update(component='pkg:golang/stdlib@v1.24.6', installedVersion='v1.24.6')
        self.policy['assessments'] = [self.review]
        self.vulnerability['PkgIdentifier']['PURL'] = 'pkg:golang/stdlib@1.24.6'
        self.vulnerability['InstalledVersion'] = '1.24.6'
        self.assertEqual('passed', self.evaluate()['result'])
        self.vulnerability['PkgIdentifier']['PURL'] = 'pkg:golang/stdlib@1.24.7'
        self.vulnerability['InstalledVersion'] = '1.24.7'
        self.assertEqual('failed', self.evaluate()['result'])

    def test_exact_subject_version_component_id_and_candidate_are_required(self):
        self.policy['assessments'] = [self.review]
        for key, value in (('subjectDigest', 'sha256:' + 'b' * 64), ('installedVersion', '1.2.4'),
                           ('component', 'pkg:deb/debian/other@1.2.3'), ('id', 'CVE-2026-54321'),
                           ('releaseTag', 'v0.1.0-rc.3')):
            with self.subTest(key=key):
                original = self.review[key]
                self.review[key] = value
                self.assertEqual('failed', self.evaluate()['result'])
                self.review[key] = original

    def test_incomplete_ambiguous_or_stale_review_fails_closed(self):
        for key, value in (('owner', ''), ('rationale', 'ignore'), ('subjectDigest', 'latest'), ('component', '*'),
                           ('expires', '2026-11-12'), ('expires', '2027-03-01'), ('reviewed', '2026-11-13'),
                           ('evidence', []), ('evidence', ['http://example.com/unsafe']),
                           ('evidence', ['https://user:password@example.com/advisory']),
                           ('justification', 'scanner-noise'), ('releaseTag', 'v0.1.0')):
            with self.subTest(key=key):
                review = dict(self.review, **{key: value})
                self.policy['assessments'] = [review]
                with self.assertRaises(ValueError):
                    self.evaluate()
        self.policy['assessments'] = [self.review, self.review]
        with self.assertRaisesRegex(ValueError, 'conflicting'):
            self.evaluate()

    def test_new_unfixed_finding_remains_under_investigation_and_blocks(self):
        self.policy['assessments'] = [self.review]
        self.report['Results'][0]['Vulnerabilities'].append(dict(self.vulnerability, VulnerabilityID='CVE-2026-99999'))
        result = self.evaluate()
        self.assertEqual('failed', result['result'])
        self.assertEqual(2, len(result['findings']))
        self.assertTrue(result['findings'][1]['blocked'])
        self.assertEqual('under_investigation', result['findings'][1]['applicability']['status'])

    def test_vex_scope_is_the_exact_deployment_and_accepted_risk_remains_affected(self):
        self.policy['assessments'] = [self.review]
        result = document([self.evaluate()], 'b' * 64, TAG)
        statement = result['statements'][0]
        self.assertEqual('not_affected', statement['status'])
        self.assertEqual('urn:romd:deployment:sha256:' + 'b' * 64, statement['products'][0]['@id'])
        self.assertIn(self.review['expires'], statement['impact_statement'])
        self.policy['assessments'] = []
        self.accept_risk()
        statement = document([self.evaluate()], 'b' * 64, TAG)['statements'][0]
        self.assertEqual('affected', statement['status'])
        self.assertIn(self.review['followUp'], statement['action_statement'])

    def test_downloaded_policy_replay_rejects_wrong_candidate_and_forged_go_resolution(self):
        self.accept_risk()
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary)
            (work / 'raw.json').write_text(json.dumps(self.report))
            result = self.evaluate()
            (work / 'policy.json').write_text(json.dumps(result))
            subject = dict(role='postgres', sha256=DIGEST.split(':')[1], scans=['raw.json'], policy='policy.json')
            verify_policy_results(work, subject, self.policy, TAG)
            with self.assertRaises(ValueError):
                verify_policy_results(work, subject, self.policy, 'v0.1.0-rc.3')
            self.policy['exceptions'] = []
            result['findings'][0].update(excepted=False, applicability={'status': 'affected-packages-absent', 'evidence': {'forged': True}})
            result['findings'][0].pop('riskAcceptance')
            (work / 'policy.json').write_text(json.dumps(result))
            with self.assertRaisesRegex(ValueError, 'Go package-absence'):
                verify_policy_results(work, subject, self.policy, TAG)

    def test_committed_policy_has_current_complete_nonconflicting_reviews(self):
        policy = json.loads((Path(__file__).parent / 'policy.json').read_text())
        validate_policy(policy)


if __name__ == '__main__':
    unittest.main()
