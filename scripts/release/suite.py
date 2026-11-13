#!/usr/bin/env python3
"""The same positive/negative packaged runtime suite before and after publication."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import tempfile
from types import SimpleNamespace

from assert_failure import assert_injected_failure
from qualify import argument_parser, image_map, qualify, validate_arguments, verify_bundle, verify_publisher


CASES = ('positive', 'portal-asset', 'player-startup')
STATES = ('serverInstanceSha256', 'adminUserSha256', 'librarySha256', 'titleSha256', 'releaseSha256', 'contentSha256')
CHECKS = ('adminProfile', 'adminCallback', 'adminAuthenticatedView', 'consumerProfile', 'consumerCallback',
          'consumerAuthenticatedView', 'playerHandshake', 'playerReady', 'playerGameStarted',
          'playerBodyPlaying', 'consumerPlaying')
REPORTS = [f'{case}/{name}' for case in CASES for name in
           ('qualification.json', 'lifecycle.json', 'browser-initial/browser-diagnostics.json')]
REPORTS += [f'positive/browser-{phase}/browser-diagnostics.json' for phase in ('after-restart', 'after-restore')]


def digest(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def evidence_type(args):
    return 'disposable-rehearsal' if getattr(args, 'rehearsal', False) else 'disposable-release'


def read_report(root, name):
    path = PurePosixPath(name)
    if path.is_absolute() or '..' in path.parts or '\\' in name:
        raise ValueError('Unsafe suite report path')
    target = root / name
    if target.is_symlink() or not target.resolve().is_relative_to(root.resolve()) or target.stat().st_size > 2 * 1024 * 1024:
        raise ValueError('Unsafe or oversized suite report')
    return json.loads(target.read_text())


def verify_suite(root, args):
    verify_publisher(args.bundle, args.publisher_state, args.expected_images, args.expected_revision, args.expected_tag)
    with tempfile.TemporaryDirectory(prefix='romd-suite-binding-') as temporary:
        verified = verify_bundle(args.bundle, args.checksum, args.expected_images, Path(temporary) / 'bundle',
                                 args.expected_revision, args.expected_tag)
    summary = read_report(root, 'suite.json')
    expected = image_map(args.expected_images)
    if (summary.get('schemaVersion') != 1 or summary.get('result') != 'passed'
            or summary.get('evidenceType') != evidence_type(args)
            or summary.get('sourceCommit') != args.expected_revision or summary.get('releaseTag') != args.expected_tag
            or summary.get('bundleSha256') != digest(args.bundle) or summary.get('images') != expected
            or set(summary.get('files', {})) != set(REPORTS)):
        raise ValueError('Packaged suite is incomplete or belongs to different inputs/evidence type')
    for name, checksum in summary['files'].items():
        read_report(root, name)
        if not re.fullmatch('[0-9a-f]{64}', checksum) or digest(root / name) != checksum:
            raise ValueError('Packaged suite report checksum mismatch')
    positive = read_report(root, 'positive/qualification.json')
    if (positive.get('result') != 'passed' or positive.get('evidenceType') != evidence_type(args)
            or positive.get('bundle') != verified):
        raise ValueError('Positive packaged suite is not bound to the bundle')
    initial, restored = positive.get('runningImages', {}), positive.get('restoredRunningImages', {})
    services = {f'ROMD_{role.upper()}_IMAGE': 'romd-' + role for role in ('admin', 'consumer', 'worker', 'player')}
    services['ROMD_POSTGRES_IMAGE'] = 'postgres'
    for key, service in services.items():
        for images in (initial, restored):
            entry = images.get(service, {})
            if (entry.get('reference') != expected[key] or entry.get('platform') != 'linux/amd64'
                    or not re.fullmatch('sha256:[0-9a-f]{64}', entry.get('imageId', ''))):
                raise ValueError('Positive suite did not run all expected image digests')
        if initial[service]['imageId'] != restored[service]['imageId']:
            raise ValueError('Restored image differs from the initial image')
    lifecycle = read_report(root, 'positive/lifecycle.json')
    if (lifecycle.get('result') != 'passed' or lifecycle.get('complete') is not True
            or lifecycle.get('errors') or lifecycle.get('cleanupErrors')
            or positive.get('lifecycle', {}).get('result') != 'passed'
            or positive.get('lifecycle', {}).get('complete') is not True):
        raise ValueError('Positive suite did not complete a clean lifecycle')
    identity = None
    for phase in ('initial', 'after-restart', 'after-restore'):
        browser = read_report(root, f'positive/browser-{phase}/browser-diagnostics.json')
        state = browser.get('persistedState', {})
        if (browser.get('result') != 'passed' or browser.get('failures') != 0
                or not all(browser.get('checks', {}).get(key) is True for key in CHECKS)
                or set(state) != set(STATES) or any(not re.fullmatch('[0-9a-f]{64}', str(state[key])) for key in STATES)
                or (identity is not None and identity != state)):
            raise ValueError('Positive suite did not prove browser playback and persistent identity')
        identity = state
    for fault in CASES[1:]:
        assert_injected_failure(root / fault, fault, evidence_type=evidence_type(args))
        negative = read_report(root, f'{fault}/qualification.json')
        if negative.get('bundle') != verified or negative.get('runningImages') != initial:
            raise ValueError('Negative drill did not run the same bundle/images as the positive drill')
    return summary


def run_suite(args, root, *, run_case=qualify):
    summary = dict(schemaVersion=1, result='failed', evidenceType=evidence_type(args),
                   sourceCommit=args.expected_revision, releaseTag=args.expected_tag,
                   bundleSha256=digest(args.bundle), images=image_map(args.expected_images), files={})
    if args.verify_only or args.inject_fault != 'none':
        raise ValueError('A suite must execute the complete positive and negative runtime checks')
    try:
        for case in CASES:
            work = root / case
            work.mkdir(mode=0o700)
            selected = SimpleNamespace(**dict(vars(args), inject_fault='none' if case == 'positive' else case))
            report = run_case(selected, work)
            (work / 'qualification.json').write_text(json.dumps(report, indent=2) + '\n')
            if case == 'positive' and report.get('result') != 'passed':
                raise ValueError('Positive packaged runtime rehearsal failed')
            if case != 'positive':
                assert_injected_failure(work, case, evidence_type=evidence_type(args))
        summary['result'] = 'passed'
    except (ValueError, RuntimeError, OSError, KeyError, TypeError) as error:
        summary['failure'] = str(error) if type(error) in (ValueError, RuntimeError) else type(error).__name__
    for name in REPORTS:
        if (root / name).is_file():
            summary['files'][name] = digest(root / name)
    path = root / 'suite.json'
    path.write_text(json.dumps(summary, indent=2) + '\n')
    if summary['result'] == 'passed':
        try:
            verify_suite(root, args)
        except (ValueError, OSError, KeyError, TypeError) as error:
            summary.update(result='failed', failure=str(error) if type(error) is ValueError else type(error).__name__)
            path.write_text(json.dumps(summary, indent=2) + '\n')
    return summary


def main():
    parser = argument_parser()
    parser.add_argument('--verify-suite', action='store_true', help='Verify retained reports without rerunning containers')
    args = parser.parse_args()
    validate_arguments(parser, args)
    root = args.evidence_dir.resolve()
    if args.verify_suite:
        verify_suite(root, args)
        return 0
    root.mkdir(mode=0o700, parents=True, exist_ok=False)
    report = run_suite(args, root)
    print(f'Packaged suite: {report["result"]}; evidence: {root}')
    return 0 if report['result'] == 'passed' else 1


if __name__ == '__main__':
    raise SystemExit(main())
