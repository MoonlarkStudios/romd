#!/usr/bin/env python3
"""Scan new public commits, or full public history, with exact synthetic-fixture exceptions."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import tomllib

ROOT = Path(__file__).resolve().parents[2]
from install_scanner import ARCHIVE_NAME, MAX_ARCHIVE_BYTES, MAX_BINARY_BYTES, install, scanner_binary, scanner_pin

OID = re.compile(r'[0-9a-f]{40}')


def authenticate(scanner):
    if scanner.is_symlink() or not scanner.is_file():
        raise ValueError('Scanner must be a regular authenticated executable')
    package = scanner.with_name(ARCHIVE_NAME)
    if package.is_symlink() or not package.is_file():
        raise ValueError('Scanner archive must be a regular authenticated file')
    with package.open('rb') as stream:
        archive = stream.read(MAX_ARCHIVE_BYTES + 1)
    if len(archive) > MAX_ARCHIVE_BYTES or hashlib.sha256(archive).hexdigest() not in scanner_pin()['archives'].values():
        raise ValueError('Scanner archive does not match the reviewed pin')
    with scanner.open('rb') as stream:
        binary = stream.read(MAX_BINARY_BYTES + 1)
    if binary != scanner_binary(archive):
        raise ValueError('Scanner executable differs from its authenticated archive')


def log_scope(root, base, full=False):
    if full or base == '0' * 40:
        return '--all'
    if not OID.fullmatch(base):
        raise ValueError('Secret delta scan requires a full base commit')
    subprocess.run(['git', 'merge-base', '--is-ancestor', base, 'HEAD'], cwd=root, check=True,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return base + '..HEAD'


def reviewed(root, finding, exceptions):
    commit = finding.get('Commit', '')
    if not OID.fullmatch(commit):
        raise ValueError('Secret report lacks a full finding commit')
    for item in exceptions:
        if (finding.get('File') == item['path'] and finding.get('RuleID') == item['rule']
                and finding.get('StartLine') == item['startLine'] and finding.get('EndLine') == item['endLine']):
            blob = subprocess.check_output(['git', 'rev-parse', '--verify', commit + ':' + item['path']],
                                           cwd=root, stderr=subprocess.DEVNULL, text=True).strip()
            return blob == item['gitBlob']
    return False


def scan(root, scanner, scope):
    authenticate(scanner)
    profile = root / '.gitleaks.toml'
    configuration = tomllib.loads(profile.read_text())
    if set(configuration) != {'title', 'extend'} or configuration['extend'] != {'useDefault': True}:
        raise ValueError('Secret scanning profile must retain all default rules without allowlists')
    policy = json.loads((root / 'scripts/ci/secret-exceptions.json').read_text())
    if policy.get('version') != 1 or not isinstance(policy.get('exceptions'), list):
        raise ValueError('Unsupported exact secret exception policy')
    with tempfile.TemporaryDirectory(prefix='romd-secret-scan-') as temporary:
        report, ignore = Path(temporary) / 'report.json', Path(temporary) / 'empty.ignore'
        ignore.write_text('')
        # Ignore repository ignore files and inline directives. Do not echo scanner streams.
        environment = {key: value for key, value in os.environ.items() if not key.startswith('GITLEAKS_')}
        result = subprocess.run([str(scanner), 'git', str(root), '--config', str(profile),
                                 '--gitleaks-ignore-path', str(ignore), '--ignore-gitleaks-allow',
                                 '--redact=100', '--no-banner', '--no-color', '--report-format', 'json',
                                 '--max-decode-depth', '5', '--report-path', str(report), '--log-opts', scope],
                                capture_output=True, timeout=300, env=environment)
        if result.returncode not in (0, 1) or not report.is_file():
            raise ValueError('Secret scanner could not complete')
        findings = json.loads(report.read_text())
        if (not isinstance(findings, list) or any(not isinstance(item, dict) for item in findings)
                or bool(findings) != (result.returncode == 1)):
            raise ValueError('Secret scanner returned an inconsistent report')
        blocked = [item for item in findings if not reviewed(root, item, policy['exceptions'])]
        print(json.dumps({'scope': scope, 'findings': len(findings),
                          'reviewedSyntheticFindings': len(findings) - len(blocked), 'blocked': len(blocked)}))
        if blocked:
            # Paths/rules/locations are sufficient; raw secret/match fields never leave the private report.
            for item in blocked:
                print(json.dumps({'path': item.get('File'), 'rule': item.get('RuleID'),
                                  'line': item.get('StartLine')}), file=sys.stderr)
        return bool(blocked)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--gitleaks', type=Path)
    parser.add_argument('--base', default=os.environ.get('ROMD_CI_BASE', ''))
    parser.add_argument('--full', action='store_true')
    args = parser.parse_args()
    try:
        base = args.base
        if not base and not args.full and os.environ.get('GITHUB_ACTIONS') != 'true':
            base = subprocess.check_output(['git', 'rev-parse', '--verify', 'origin/main^{commit}'],
                                           cwd=ROOT, text=True, stderr=subprocess.DEVNULL).strip()
        scope = log_scope(ROOT, base, args.full)
        if args.gitleaks:
            return scan(ROOT, args.gitleaks, scope)
        with tempfile.TemporaryDirectory(prefix='romd-pinned-scanner-') as temporary:
            return scan(ROOT, install(Path(temporary) / 'scanner'), scope)
    except (ValueError, OSError, KeyError, TypeError, subprocess.SubprocessError):
        print('Secret scan failed closed; inspect the pinned tool, policy and source history.', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
