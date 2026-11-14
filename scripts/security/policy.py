#!/usr/bin/env python3
"""Validate reviewed Trivy vulnerability exceptions and define the release severity gate.

Trivy enforces the gate itself. Scans apply `trivyignore.yaml` (and, for the
catalog reader, govulncheck VEX), then `trivy convert` fails for any remaining
UNKNOWN, HIGH or CRITICAL finding, including findings with no available fix.
Trivy does not cap an exception's lifetime or require an owner, so this module
checks the exception file before any scan uses it.

The exception file uses JSON syntax, which is valid YAML: Trivy reads it as
its YAML ignore format, and the gate validates it without a YAML parser.
"""
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import re

BLOCKED_SEVERITIES = ('UNKNOWN', 'HIGH', 'CRITICAL')
IGNORE_FILE = Path(__file__).with_name('trivyignore.yaml')
MAX_EXCEPTION_DAYS = 90
FIELDS = ('id', 'purls', 'statement', 'owner', 'expired_at')
# Trivy's own fatal errors exit 1; a distinct code separates blocked findings from tool failures.
GATE_EXIT_CODE = 3


def load_exceptions(path=IGNORE_FILE, now=None):
    now = now or datetime.now(timezone.utc)
    path = Path(path)
    try:
        document = json.loads(path.read_text())
    except json.JSONDecodeError as error:
        raise ValueError(f'{path.name} must use JSON syntax (valid YAML) so the gate can validate it') from error
    if (not isinstance(document, dict) or set(document) != {'vulnerabilities'}
            or not isinstance(document['vulnerabilities'], list)):
        raise ValueError('Exceptions must be one "vulnerabilities" list; path, misconfiguration and secret '
                         'exceptions are not accepted')
    seen = set()
    for entry in document['vulnerabilities']:
        if not isinstance(entry, dict) or set(entry) != set(FIELDS):
            raise ValueError('Each exception needs exactly: ' + ', '.join(FIELDS))
        identifier, purls, statement, owner, expires = (entry[key] for key in FIELDS)
        if not isinstance(identifier, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._:-]*', identifier):
            raise ValueError('Exception requires an exact vulnerability ID')
        if (not isinstance(purls, list) or not purls
                or any(not isinstance(purl, str) or not re.fullmatch(r'pkg:[a-z0-9.+-]+/[^@*\s]+@[^@*\s]+', purl)
                       for purl in purls)):
            raise ValueError('Exception requires exact, versioned package URLs; wildcards are rejected')
        if not isinstance(statement, str) or len(statement.strip()) < 20:
            raise ValueError('Exception requires a specific, reviewable statement')
        if not isinstance(owner, str) or not owner.strip():
            raise ValueError('Exception requires an accountable owner')
        if not isinstance(expires, str) or not re.fullmatch(r'[0-9]{4}-[0-9]{2}-[0-9]{2}T00:00:00Z', expires):
            raise ValueError('Exception expiry must be a UTC date written as YYYY-MM-DDT00:00:00Z')
        remaining = datetime.fromisoformat(expires.replace('Z', '+00:00')) - now
        if not timedelta(0) < remaining <= timedelta(days=MAX_EXCEPTION_DAYS):
            raise ValueError(f'Exception {identifier} is expired or exceeds the {MAX_EXCEPTION_DAYS}-day limit')
        for purl in purls:
            if (identifier, purl) in seen:
                raise ValueError('Duplicate vulnerability exception')
            seen.add((identifier, purl))
    return document['vulnerabilities']


def gate_command(trivy, report, config):
    """Exit with GATE_EXIT_CODE when a scan report still has a finding at a blocking severity."""
    return [trivy, 'convert', '--config', config, '--severity', ','.join(BLOCKED_SEVERITIES),
            '--exit-code', str(GATE_EXIT_CODE), '--format', 'table', report]


def suppressed(report):
    """Findings a scan suppressed through a reviewed exception or VEX statement, for the evidence record."""
    result = []
    for group in report.get('Results') or []:
        for item in group.get('ExperimentalModifiedFindings') or []:
            finding = item.get('Finding') or {}
            result.append({'id': finding.get('VulnerabilityID'), 'severity': finding.get('Severity'),
                           'purl': (finding.get('PkgIdentifier') or {}).get('PURL'),
                           'status': item.get('Status'), 'statement': item.get('Statement')})
    return result
