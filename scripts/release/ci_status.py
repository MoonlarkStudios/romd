#!/usr/bin/env python3
"""Require the allowed branch's completed CI run for the exact release commit.

The release tag identifies a commit that already ran the complete `ci.yml` on a
push to the allowed branch (main pushes never narrow CI scope). Rerunning that
suite on the tag adds no coverage, only another chance for an unrelated flake
to block publication. This gate accepts only a successful push run of the
repository's own `ci.yml` for the exact commit whose latest attempt's `CI gate`
job succeeded. It waits while such a run is queued or in progress.
"""
import argparse
import json
import os
from pathlib import Path
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = '.github/workflows/ci.yml'
GATE = 'CI gate'
API = 'https://api.github.com'


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, *args):
        return None


def api_get(path, token):
    request = urllib.request.Request(API + path, headers={
        'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28'})
    try:
        with urllib.request.build_opener(NoRedirect).open(request, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        code = error.code
        error.close()
        raise ValueError(f'CI status lookup failed (HTTP {code})') from None


def matching_runs(payload, repository, revision, branch):
    runs = payload.get('workflow_runs') if isinstance(payload, dict) else None
    if not isinstance(runs, list):
        raise ValueError('CI status response is invalid')
    # The query filters are advisory; every identity field is rechecked here so
    # a pull-request, fork, dispatch or other workflow run can never qualify.
    return [run for run in runs if isinstance(run, dict)
            and run.get('head_sha') == revision and run.get('event') == 'push'
            and run.get('head_branch') == branch and run.get('path') == WORKFLOW
            and (run.get('repository') or {}).get('full_name') == repository
            and (run.get('head_repository') or {}).get('full_name') == repository]


def gate_passed(payload):
    jobs = payload.get('jobs') if isinstance(payload, dict) else None
    if not isinstance(jobs, list):
        raise ValueError('CI job response is invalid')
    gates = [job for job in jobs if isinstance(job, dict) and job.get('name') == GATE]
    return len(gates) == 1 and gates[0].get('status') == 'completed' and gates[0].get('conclusion') == 'success'


def evaluate(runs, jobs_for):
    """Return ('passed'|'pending'|'failed', run id or None)."""
    pending = None
    for run in sorted(runs, key=lambda item: item.get('id', 0), reverse=True):
        if run.get('status') != 'completed':
            pending = pending or run.get('id')
        elif run.get('conclusion') == 'success' and gate_passed(jobs_for(run['id'])):
            return 'passed', run['id']
    if pending is not None:
        return 'pending', pending
    return 'failed', runs[0].get('id') if runs else None


def check(repository, revision, branch, token, wait_seconds, interval=30, clock=time.monotonic, sleep=time.sleep):
    if (not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository)
            or not re.fullmatch('[0-9a-f]{40}', revision) or not re.fullmatch('[A-Za-z0-9/_-]+', branch)):
        raise ValueError('Invalid repository, revision or branch')
    query = urllib.parse.urlencode({'head_sha': revision, 'event': 'push', 'branch': branch, 'per_page': 100})
    deadline = clock() + wait_seconds
    while True:
        runs = matching_runs(api_get(f'/repos/{repository}/actions/workflows/ci.yml/runs?{query}', token),
                             repository, revision, branch)
        state, run = evaluate(runs, lambda run_id: api_get(
            f'/repos/{repository}/actions/runs/{run_id}/jobs?filter=latest&per_page=100', token))
        if state == 'passed':
            return run
        if state == 'failed' or clock() >= deadline:
            location = f'https://github.com/{repository}/actions/runs/{run}' if run else 'no matching run'
            reason = ('did not succeed' if state == 'failed' else 'did not finish in time') if run else 'was not found'
            raise ValueError(f'{branch} CI for {revision} {reason} ({location}). Diagnose or rerun it on '
                             f'{branch}, then rerun this release job; nothing has been published.')
        print(f'Waiting for {branch} CI run {run} on {revision[:12]}', flush=True)
        sleep(interval)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--branch', help='Defaults to the release policy\'s allowed branch')
    parser.add_argument('--wait-minutes', type=int, default=50)
    args = parser.parse_args()
    token = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
    if not token:
        parser.exit(1, 'CI status lookup requires the scoped workflow token\n')
    try:
        if args.branch is None:
            args.branch = json.loads((ROOT / '.github/release-policy.json').read_text())['allowedBranch']
        run = check(args.repository, args.revision, args.branch, token, args.wait_minutes * 60)
    except (ValueError, OSError, KeyError, urllib.error.URLError) as error:
        parser.exit(1, (str(error) if type(error) is ValueError else 'CI status lookup failed') + '\n')
    print(json.dumps(dict(result='passed', sourceCommit=args.revision, branch=args.branch,
                          ciRun=f'https://github.com/{args.repository}/actions/runs/{run}')))
    return 0


if __name__ == '__main__':
    sys.exit(main())
