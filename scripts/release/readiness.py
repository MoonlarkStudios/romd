#!/usr/bin/env python3
"""Build and rehearse a clean commit without creating a public tag or release."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time

from preflight import valid_tag
from rehearsal_evidence import verify_rehearsal

ROOT = Path(__file__).resolve().parents[2]
GATES = ('source', 'build-and-source-recovery', 'packaged-security-runtime-cleanup')


def verify_source(root, revision, tag):
    if not re.fullmatch('[0-9a-f]{40}', revision) or not valid_tag(tag):
        raise ValueError('Rehearsal requires a full source commit and strict v<SemVer> version')
    def git(*args):
        return subprocess.check_output(['git', *args], cwd=root, text=True, stderr=subprocess.DEVNULL).strip()
    if git('rev-parse', 'HEAD') != revision:
        raise ValueError('Rehearsal checkout differs from the requested source commit')
    if git('status', '--porcelain', '--untracked-files=all'):
        raise ValueError('Rehearsal requires a clean checkout; use a separate worktree')


def run_step(command, stage):
    """Keep arbitrary build output private and off the desktop's memory budget."""
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(('ROMD_', 'COMPOSE_', 'IGDB_', 'CLOUDFLARE_', 'ACTIONS_'))
           and key not in ('GH_TOKEN', 'GITHUB_TOKEN', 'GITHUB_OUTPUT', 'GITHUB_STEP_SUMMARY', 'DOCKER_AUTH_CONFIG')}
    started = time.monotonic()
    print('Release readiness: ' + stage, flush=True)
    # Child harnesses own their bounded operations and cleanup. Do not kill only
    # their parent process with an outer timeout and abandon owned containers.
    with tempfile.TemporaryFile() as private:
        result = subprocess.run([str(part) for part in command], cwd=ROOT, env=env,
                                stdin=subprocess.DEVNULL, stdout=private, stderr=subprocess.STDOUT, check=False)
        private.seek(0)
        return dict(stage=stage, exitCode=result.returncode, seconds=round(time.monotonic() - started, 3),
                    outputSha256=hashlib.file_digest(private, 'sha256').hexdigest())


def write_report(output, report):
    (output / 'readiness.json').write_text(json.dumps(report, indent=2) + '\n')
    lines = ['# Release rehearsal', '', f'Result: **{report["result"]}**', '',
             f'Source: `{report["sourceCommit"]}`', f'Intended version: `{report["releaseTag"]}`', '',
             '| Gate | Result |', '| --- | --- |']
    lines.extend(f'| {gate} | {status} |' for gate, status in report['gates'].items())
    if report.get('blockingStage'):
        lines += ['', f'Blocking stage: `{report["blockingStage"]}`']
    if report.get('images'):
        lines += ['', '| Image | Digest |', '| --- | --- |']
        lines.extend(f'| {role} | `{digest}` |' for role, digest in report['images'].items())
    lines += ['', 'Public downloads, provenance, final-digest verification and promotion are pending.',
              'This disposable rehearsal does not qualify a published release.', '']
    (output / 'summary.md').write_text('\n'.join(lines))


def readiness(output, revision, tag, *, command=run_step):
    verify_source(ROOT, revision, tag)
    output.mkdir(parents=True, mode=0o700, exist_ok=False)
    report = dict(schemaVersion=1, result='failed', evidenceType='disposable-readiness',
                  sourceCommit=revision, releaseTag=tag, platform='linux/amd64',
                  startedAt=datetime.now(timezone.utc).isoformat(), commands=[],
                  gates={gate: 'not run' for gate in GATES})
    report['gates']['source'] = 'passed'
    stage = GATES[1]
    try:
        for stage, argv in (
            (GATES[1], [sys.executable, ROOT / 'scripts/deployment/smoke.py', '--build', '--vfs',
                        '--source-revision', revision, '--release-tag', tag, '--evidence-dir', output / 'source']),
            (GATES[2], [sys.executable, ROOT / 'scripts/release/rehearsal.py', '--revision', revision,
                        '--tag', tag, '--output', output / 'rehearsal']),
        ):
            report['gates'][stage] = 'running'
            write_report(output, report)
            record = command(argv, stage)
            report['commands'].append(record)
            if record['exitCode'] != 0:
                raise RuntimeError('Rehearsal command failed')
            if stage == GATES[2]:
                # A subprocess exit zero or a self-declared result is insufficient.
                # Reuse publication's complete security/runtime/cleanup verifier.
                root = output / 'rehearsal'
                receipt = json.loads((root / 'receipt.json').read_text())
                pins = json.loads((ROOT / 'scripts/deployment/base-images.json').read_text())
                verified = verify_rehearsal(root / 'receipt.json', revision, tag, receipt.get('imageIds'), pins)
                report['images'] = {key: value.rsplit('@', 1)[1] for key, value in verified['images'].items()}
                report['receiptSha256'] = hashlib.sha256((root / 'receipt.json').read_bytes()).hexdigest()
            report['gates'][stage] = 'passed'
        report['result'] = 'passed'
    except (ValueError, RuntimeError, OSError, KeyError, TypeError, IndexError) as error:
        report['gates'][stage] = 'failed'
        report['blockingStage'] = stage
        # Exception text can contain paths or arbitrary child diagnostics.
        report['failureType'] = type(error).__name__
    finally:
        report['finishedAt'] = datetime.now(timezone.utc).isoformat()
        write_report(output, report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--tag', default='')
    parser.add_argument('--output', type=Path, required=True, help='New directory for sanitized evidence')
    args = parser.parse_args()
    tag = args.tag or 'v0.0.0-rehearsal.r' + args.revision[:12]
    try:
        report = readiness(args.output.resolve(), args.revision, tag)
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, str(error) + '\n' if type(error) is ValueError else 'Rehearsal inputs could not be verified\n')
    print('Release rehearsal: ' + report['result'] + '; public qualification is pending')
    return 0 if report['result'] == 'passed' else 1


if __name__ == '__main__':
    raise SystemExit(main())
