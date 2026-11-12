#!/usr/bin/env python3
"""Retain every finding; apply exact reviewed applicability and risk decisions."""
from datetime import date, datetime, timezone
import re
from urllib.parse import urlsplit

JUSTIFICATIONS = {'component_not_present', 'vulnerable_code_not_present',
                  'vulnerable_code_not_in_execute_path', 'vulnerable_code_cannot_be_controlled_by_adversary',
                  'inline_mitigations_already_exist'}


def exception_key(finding):
    component, version = finding['component'], finding['installedVersion']
    # Syft uses 1.x.y while Trivy image inventory uses v1.x.y for Go stdlib.
    # Only these equivalent compiler spellings normalize; real version changes,
    # other components and PURL qualifiers still require another review.
    match = re.fullmatch(r'pkg:golang/stdlib@(?:go|v)?([0-9]+\.[0-9]+\.[0-9]+)', component)
    if match and re.fullmatch(r'(?:go|v)?' + re.escape(match[1]), version):
        component, version = 'pkg:golang/stdlib@' + match[1], match[1]
    return finding['id'], component, version, finding['subjectDigest']


def validate_policy(policy, today=None):
    today = today or datetime.now(timezone.utc).date()
    if (policy.get('schemaVersion') != 2 or set(policy.get('blockedSeverities', [])) != {'CRITICAL', 'HIGH', 'UNKNOWN'}
            or policy.get('maxExceptionDays') != 90 or not isinstance(policy.get('exceptions'), list)
            or not isinstance(policy.get('assessments'), list)):
        raise ValueError('Release security policy requires applicability review and the severity/expiry gate')
    minimum = policy.get('minimumPostgresVersion')
    if not isinstance(minimum, str) or not re.fullmatch(r'18\.[0-9]+', minimum) or int(minimum.split('.')[1]) < 7:
        raise ValueError('Release security policy requires PostgreSQL 18.7 or a later reviewed minor release')
    decisions = {}
    for kind, review in [('not_affected', item) for item in policy['assessments']] + [('accepted_risk', item) for item in policy['exceptions']]:
        required = ('id', 'component', 'installedVersion', 'subjectDigest', 'rationale', 'owner', 'reviewed', 'expires', 'severity')
        if not isinstance(review, dict) or any(not isinstance(review.get(key), str) or not review[key].strip() for key in required):
            raise ValueError('Review requires exact vulnerability/component/version/digest, severity, rationale, owner and dates')
        if any('*' in review[key] for key in required) or not re.fullmatch('sha256:[0-9a-f]{64}', review['subjectDigest']):
            raise ValueError('Review wildcards and mutable subjects are forbidden')
        if len(review['rationale'].strip()) < 40 or review['severity'] not in ('UNKNOWN', 'LOW', 'MEDIUM', 'HIGH', 'CRITICAL'):
            raise ValueError('Review requires a specific rationale and recognized severity')
        reviewed, expires = (date.fromisoformat(review[key]) for key in ('reviewed', 'expires'))
        if not reviewed <= today < expires or not 0 < (expires - reviewed).days <= policy['maxExceptionDays']:
            raise ValueError('Review expired, future-dated or exceeds the 90-day limit')
        evidence = review.get('evidence')
        if not isinstance(evidence, list) or not evidence or any(not isinstance(url, str) or urlsplit(url).scheme != 'https'
                or not urlsplit(url).hostname or urlsplit(url).username or urlsplit(url).password or '*' in url for url in evidence):
            raise ValueError('Review requires specific primary-source HTTPS evidence')
        if not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+-[0-9A-Za-z.-]+', review.get('releaseTag', '')):
            raise ValueError('Review requires an exact prerelease deployment context')
        if kind == 'not_affected':
            if review.get('justification') not in JUSTIFICATIONS:
                raise ValueError('Non-applicability requires a recognized VEX justification')
        elif (review.get('fixedVersion', 'missing') is not None
                or any(not isinstance(review.get(key), str) or len(review[key].strip()) < 30 for key in ('mitigations', 'followUp'))):
            raise ValueError('Accepted risk requires an exact prerelease, no vendor fix, mitigations and follow-up')
        key = exception_key(review)
        if key in decisions:
            raise ValueError('Duplicate or conflicting vulnerability review')
        decisions[key] = dict(review, status=kind)
    return decisions


def evaluate(report, subject_digest, policy, today=None, *, release_tag=None):
    decisions = validate_policy(policy, today)
    if not re.fullmatch('sha256:[0-9a-f]{64}', subject_digest):
        raise ValueError('Scanner result requires an immutable subject digest')
    if report.get('SchemaVersion') != 2 or not isinstance(report.get('Results'), list):
        raise ValueError('Trivy report is missing the expected result schema')
    findings = []
    for result in report['Results']:
        if result.get('Misconfigurations') or result.get('Secrets'):
            raise ValueError('Unexpected scanner type in vulnerability-only gate')
        vulnerabilities = result.get('Vulnerabilities') or []
        if not isinstance(vulnerabilities, list):
            raise ValueError('Malformed scanner vulnerability list')
        for vulnerability in vulnerabilities:
            severity = vulnerability.get('Severity')
            version, name, identifier = (vulnerability.get(key) for key in ('InstalledVersion', 'PkgName', 'VulnerabilityID'))
            if severity not in ('UNKNOWN', 'LOW', 'MEDIUM', 'HIGH', 'CRITICAL') or not all(isinstance(value, str) and value for value in (version, name, identifier)):
                raise ValueError('Malformed vulnerability identity or severity')
            component = (vulnerability.get('PkgIdentifier') or {}).get('PURL') or f'{result.get("Type", "unknown")}:{name}'
            finding = {'id': identifier, 'component': component, 'installedVersion': version,
                       'subjectDigest': subject_digest, 'severity': severity,
                       'fixedVersion': vulnerability.get('FixedVersion') or None}
            finding['rawBlocked'] = severity in policy['blockedSeverities']
            finding['applicability'] = {'status': 'under_investigation' if finding['rawBlocked'] else 'not_reviewed'}
            finding['excepted'] = False
            finding['blocked'] = finding['rawBlocked']
            review = decisions.get(exception_key(finding))
            if review and review['severity'] == severity and review['releaseTag'] == release_tag:
                if review['status'] == 'not_affected':
                    finding['applicability'] = review
                    finding['blocked'] = False
                elif review['releaseTag'] == release_tag and finding['fixedVersion'] is None:
                    # Accepted risk remains affected; never label it a false positive.
                    finding['riskAcceptance'] = review
                    finding['applicability'] = {'status': 'affected'}
                    finding['excepted'], finding['blocked'] = True, False
            findings.append(finding)
    return {'result': 'failed' if any(item['blocked'] for item in findings) else 'passed', 'findings': findings}
