#!/usr/bin/env python3
"""Evaluate every Trivy finding without ignore-unfixed or scanner ignore files."""
from datetime import date, datetime, timezone
import re


def exception_key(finding):
    return tuple(finding[key] for key in ('id', 'component', 'installedVersion', 'subjectDigest'))


def validate_policy(policy, today=None):
    today = today or datetime.now(timezone.utc).date()
    if (policy.get('schemaVersion') != 1 or set(policy.get('blockedSeverities', [])) != {'CRITICAL', 'HIGH', 'UNKNOWN'}
            or policy.get('maxExceptionDays') != 90 or not isinstance(policy.get('exceptions'), list)):
        raise ValueError('Release security policy cannot weaken the required severity/expiry gate')
    seen = set()
    for exception in policy['exceptions']:
        required = ('id', 'component', 'installedVersion', 'subjectDigest', 'rationale', 'owner', 'expires')
        if any(not isinstance(exception.get(key), str) or not exception[key].strip() for key in required):
            raise ValueError('Exception requires vulnerability, exact component/version/digest, rationale, owner and expiry')
        if any('*' in exception[key] for key in required) or not re.fullmatch('sha256:[0-9a-f]{64}', exception['subjectDigest']):
            raise ValueError('Exception wildcards and mutable subjects are forbidden')
        if len(exception['rationale'].strip()) < 20:
            raise ValueError('Exception requires a specific reviewable rationale')
        expires = date.fromisoformat(exception['expires'])
        if not 0 < (expires - today).days <= policy['maxExceptionDays']:
            raise ValueError('Exception expired or exceeds the 90-day limit')
        key = exception_key(exception)
        if key in seen:
            raise ValueError('Duplicate vulnerability exception')
        seen.add(key)
    return seen


def evaluate(report, subject_digest, policy, today=None):
    exceptions = validate_policy(policy, today)
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
            finding['excepted'] = exception_key(finding) in exceptions
            finding['blocked'] = severity in policy['blockedSeverities'] and not finding['excepted']
            findings.append(finding)
    return {'result': 'failed' if any(item['blocked'] for item in findings) else 'passed', 'findings': findings}
