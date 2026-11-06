#!/usr/bin/env python3
"""Execute ROMD's registered checks without shell evaluation; retain per-run evidence.

Each run writes <evidence>/results.json (schemaVersion 2) after every check, so an
interrupted run still leaves a truthful partial record. The record names the
selector, HEAD revision and whether the working tree was dirty (with content
hashes only, never diffs or file contents), per-check status and duration, the
overall status, and the selected checks that did not run.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import signal
import subprocess
import sys
import tempfile
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
from containers.runtime import ContainerError, Runtime

SCHEMA_VERSION = 2
# Overall statuses: running, passed, failed, interrupted, error.
# Check statuses: not-run, running, passed, failed, setup-failed, interrupted.


class Interrupted(BaseException):
    """Raised from the SIGTERM handler so the run records an interrupted status."""

    def __init__(self, signum):
        super().__init__(signum)
        self.signum = signum


def _now():
    return datetime.now(timezone.utc).isoformat()


def _git(root, *args):
    return subprocess.run(['git', *args], cwd=root, check=True, capture_output=True).stdout


def tree_state(root):
    """Describe the tested tree without embedding diffs, paths or contents."""
    try:
        revision = _git(root, 'rev-parse', '--verify', 'HEAD').decode().strip()
    except subprocess.CalledProcessError:
        revision = None
    diff = _git(root, 'diff', 'HEAD', '--binary', '--no-ext-diff', '--no-color') if revision else b''
    untracked = sorted(p for p in _git(root, 'ls-files', '--others', '--exclude-standard', '-z').split(b'\0') if p)
    untracked_hash = hashlib.sha256()
    for path in untracked:
        untracked_hash.update(path + b'\0')
        try:
            untracked_hash.update(hashlib.sha256((root / os.fsdecode(path)).read_bytes()).digest())
        except OSError:
            untracked_hash.update(b'unreadable')
    dirty = bool(diff or untracked or revision is None)
    state = {'revision': revision, 'dirty': dirty}
    if dirty:
        state['tree'] = {'diffSha256': hashlib.sha256(diff).hexdigest() if diff else None,
                         'untrackedCount': len(untracked),
                         'untrackedSha256': untracked_hash.hexdigest() if untracked else None}
    return state


def _write(evidence, result):
    target = evidence / 'results.json'
    temporary = evidence / '.results.json.tmp'
    temporary.write_text(json.dumps(result, indent=2) + '\n')
    os.replace(temporary, target)


def _finish(result, status, exit_code, started_clock, root):
    result['status'] = status
    result['exitCode'] = exit_code
    result['finished'] = _now()
    result['durationSeconds'] = round(time.monotonic() - started_clock, 3)
    result['notRun'] = [entry['id'] for entry in result['checks'] if entry['status'] == 'not-run']
    try:
        final = tree_state(root)
        result['treeChangedDuringRun'] = {k: final.get(k) for k in ('revision', 'dirty', 'tree')} != \
            {k: result.get(k) for k in ('revision', 'dirty', 'tree')}
    except (OSError, subprocess.CalledProcessError):
        result['treeChangedDuringRun'] = None


def _stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


def summarize(result, out=None):
    lines = ['', f'Validation {result["status"]} ({result.get("durationSeconds", 0):.1f}s)']
    for entry in result['checks']:
        duration = '' if entry.get('durationSeconds') is None else f' {entry["durationSeconds"]:.1f}s'
        lines.append(f'  {entry["status"]:<12} {entry["id"]}{duration}')
    if result.get('notRun'):
        lines.append(f'Not run: {", ".join(result["notRun"])}')
    if result.get('dirty'):
        revision = result.get('revision') or '(no commit)'
        lines.append(f'WARNING: results describe uncommitted changes on top of {revision}')
    if result.get('treeChangedDuringRun'):
        lines.append('WARNING: the working tree changed while checks ran')
    print('\n'.join(lines), file=out or sys.stdout, flush=True)


def execute(checks, root, evidence, ci=False, filter_value=None, keep_going=False, selector=None):
    """Run checks in order; return 0 or the first failing exit code. Always leaves results.json."""
    root, evidence = Path(root), Path(evidence)
    started_clock = time.monotonic()
    result = {'schemaVersion': SCHEMA_VERSION, 'status': 'running', 'exitCode': None,
              'selector': dict(selector or {}, ci=ci, filter=filter_value, keepGoing=keep_going),
              **tree_state(root),
              'environment': {'platform': platform.platform(), 'mode': 'ci' if ci else 'local'},
              'started': _now(), 'finished': None, 'durationSeconds': None, 'checks': [], 'notRun': []}
    values = dict(probe=str(root / 'tools/Romd.Media.Probe/bin' /
                            ('Release' if ci else 'Debug') / 'net10.0/Romd.Media.Probe.dll'),
                  evidence=str(evidence), filter=filter_value)
    for check in checks:
        template = check['ci' if ci else 'local']
        command = None if '{filter}' in template and filter_value is None else \
            [token.format(**values) for token in template]
        result['checks'].append(dict(id=check['id'], command=command, cwd=str(root / check['cwd']),
                                     artifact=None, status='not-run', exit_code=None,
                                     started=None, durationSeconds=None))
    missing = [entry['id'] for entry in result['checks'] if entry['command'] is None]
    if missing:
        result['error'] = f'{", ".join(missing)} requires --filter for its hosted CI partition'
        _finish(result, 'error', 2, started_clock, root)
        _write(evidence, result)
        raise ValueError(result['error'])
    _write(evidence, result)

    def on_term(signum, frame):
        raise Interrupted(signum)

    previous_term = signal.signal(signal.SIGTERM, on_term)
    runtime = None
    first_failure = 0
    try:
        for check, entry in zip(checks, result['checks']):
            if first_failure and not keep_going:
                break
            command, cwd = entry['command'], Path(entry['cwd'])
            print(f'[{check["id"]}] {command} (cwd={cwd})', flush=True)
            entry.update(artifact=str(evidence / (check['id'] + '.log')), status='running', started=_now())
            check_clock = time.monotonic()
            _write(evidence, result)
            process = None
            try:
                with Path(entry['artifact']).open('w') as log:
                    environment = os.environ.copy()
                    if check.get('container_environment'):
                        if ci:
                            environment['ROMD_CONTAINER_ENGINE'] = 'docker'
                        else:
                            try:
                                if runtime is None:
                                    runtime = Runtime(environment)
                                environment = runtime.child_environment(tests=True)
                            except ContainerError as error:
                                message = f'Container setup: {error}\n'
                                print(message, end='', flush=True)
                                log.write(message)
                                entry.update(status='setup-failed', exit_code=1)
                    if entry['status'] == 'running':
                        process = subprocess.Popen(command, cwd=cwd, env=environment, stdout=subprocess.PIPE,
                                                   stderr=subprocess.STDOUT, text=True, errors='replace')
                        # Not `with process:` -- its exit waits indefinitely for the child before
                        # an interrupted run could terminate it.
                        try:
                            for line in process.stdout:
                                print(line, end='', flush=True)
                                log.write(line)
                            entry['exit_code'] = process.wait()
                        finally:
                            process.stdout.close()
                        entry['status'] = 'failed' if entry['exit_code'] else 'passed'
            except (KeyboardInterrupt, Interrupted):
                if process is not None:
                    _stop(process)
                    entry['exit_code'] = process.returncode
                entry['status'] = 'interrupted'
                raise
            finally:
                entry['durationSeconds'] = round(time.monotonic() - check_clock, 3)
                if entry['status'] in ('failed', 'setup-failed') and not first_failure:
                    first_failure = entry['exit_code'] if entry['exit_code'] > 0 else 1
                _write(evidence, result)
        _finish(result, 'failed' if first_failure else 'passed', first_failure, started_clock, root)
        return first_failure
    except (KeyboardInterrupt, Interrupted) as error:
        signum = error.signum if isinstance(error, Interrupted) else signal.SIGINT
        _finish(result, 'interrupted', 128 + signum, started_clock, root)
        return 128 + signum
    except BaseException as error:
        result['error'] = f'{type(error).__name__}: {error}'
        _finish(result, 'error', 1, started_clock, root)
        raise
    finally:
        signal.signal(signal.SIGTERM, previous_term)
        _write(evidence, result)
        summarize(result)


def evidence_prefix(args):
    if args.group:
        label = args.group
    elif args.job:
        label = 'ci-' + args.job
    else:
        label = args.id[0] if len(args.id) == 1 else f'{len(args.id)}-checks'
    label = re.sub(r'[^A-Za-z0-9._-]+', '_', label)[:48]
    return f'{datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")}-{label}-'


def main(argv=None, registry=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    selector = parser.add_mutually_exclusive_group(required=True)
    selector.add_argument('--group', help='run every check registered in this local group')
    selector.add_argument('--job', help='run the required checks for this CI job (requires --ci)')
    selector.add_argument('--id', action='append', help='run one registered check; repeatable')
    parser.add_argument('--ci', action='store_true', help='use CI command templates')
    parser.add_argument('--filter', help='test filter for partitioned CI checks')
    parser.add_argument('--evidence-parent', type=Path, default=ROOT / '.artifacts/validation')
    parser.add_argument('--list', action='store_true', help='print the selected check IDs and exit')
    parser.add_argument('--keep-going', action='store_true',
                        help='continue after a failed check; the exit status still reports the first failure')
    args = parser.parse_args(argv)
    if registry is None:
        registry = json.loads((ROOT / 'scripts/validation/checks.json').read_text())
    checks = [check for check in registry['checks']
              if ((args.group and args.group in check['groups'])
                  or (args.job and args.job == check['ci_job'] and check['required_ci'])
                  or (args.id and check['id'] in args.id))]
    if not checks or (args.id and set(args.id) != {c['id'] for c in checks}):
        parser.error('selection contains no checks or an unknown check ID')
    if args.job and not args.ci:
        parser.error('--job requires --ci')
    if args.list:
        print('\n'.join(check['id'] for check in checks))
        return 0
    args.evidence_parent.mkdir(parents=True, exist_ok=True)
    evidence = Path(tempfile.mkdtemp(prefix=evidence_prefix(args), dir=args.evidence_parent)).resolve()
    print(f'Validation evidence: {evidence}', flush=True)
    return execute(checks, ROOT, evidence, args.ci, args.filter, keep_going=args.keep_going,
                   selector={'group': args.group, 'job': args.job, 'ids': args.id})


if __name__ == '__main__':
    sys.exit(main())
