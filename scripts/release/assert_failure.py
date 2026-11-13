#!/usr/bin/env python3
"""Reject a negative drill that failed before it actually exercised its fault."""
import argparse
import json
from pathlib import Path


def assert_injected_failure(evidence, fault, *, evidence_type=None):
    qualification = json.loads((evidence / 'qualification.json').read_text())
    browser = json.loads((evidence / 'browser-initial/browser-diagnostics.json').read_text())
    if evidence_type is not None and qualification.get('evidenceType') != evidence_type:
        raise ValueError('Negative drill evidence type mismatch')
    if (qualification.get('result') != 'failed' or qualification.get('injectedFault') != fault
            or len(qualification.get('runningImages', {})) != 5
            or browser.get('result') != 'failed' or browser.get('injectedFault') != fault):
        raise ValueError('Negative drill did not reach the verified packaged browser')
    cleanup = [command for command in qualification.get('commands', [])
               if command.get('stage', '').startswith('cleanup-')]
    if (not any(command.get('stage') == 'cleanup-disposable-project' and command.get('exitCode') == 0
                for command in cleanup) or any(command.get('exitCode') != 0 for command in cleanup)):
        raise ValueError('Negative drill did not clean up its disposable resources')
    lifecycle = json.loads((evidence / 'lifecycle.json').read_text())
    if (lifecycle.get('result') != 'passed' or lifecycle.get('complete') is not True
            or lifecycle.get('errors') or lifecycle.get('cleanupErrors')
            or qualification.get('lifecycle', {}).get('result') != 'passed'
            or qualification.get('lifecycle', {}).get('complete') is not True):
        raise ValueError('Negative drill did not prove a complete clean container lifecycle')
    records = browser.get('records', [])
    if not any(record.get('kind') == 'injected-fault' and record.get('fault') == fault for record in records):
        raise ValueError('Negative drill did not deliver the requested fault')
    asset_failures = [record for record in records if record.get('kind') == 'required-response-failed'
                      and record.get('status') == 503]
    checks = browser.get('checks', {})
    if fault == 'portal-asset':
        if not any(record.get('surface') == 'admin' for record in asset_failures):
            raise ValueError('Broken portal asset did not block qualification')
    elif fault == 'player-startup':
        if (not all(checks.get(key) is True for key in ('adminCallback', 'adminAuthenticatedView',
                    'consumerCallback', 'consumerAuthenticatedView', 'playerHandshake'))
                or checks.get('playerGameStarted') or not asset_failures):
            raise ValueError('Player fault did not block real post-handshake startup')
    else:
        raise ValueError('Unsupported fault')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence-dir', type=Path, required=True)
    parser.add_argument('--fault', choices=('portal-asset', 'player-startup'), required=True)
    args = parser.parse_args()
    assert_injected_failure(args.evidence_dir, args.fault)
    print(f'Injected {args.fault} blocked qualification and retained sanitized diagnostics')


if __name__ == '__main__':
    main()
