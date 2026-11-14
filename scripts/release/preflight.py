#!/usr/bin/env python3
"""Reject malformed tags and unapproved source revisions before release gates.

Prerelease tags are candidates: they must identify the current allowed-branch
tip and are built, rehearsed and qualified from source. Stable tags are
promotions: they must identify the same commit as a `-rc.N` candidate of the
same version that is reachable from the allowed branch, and they never rebuild.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
TAG = re.compile(r'v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?')
CANDIDATE = re.compile(r'(v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))-rc\.(0|[1-9][0-9]*)')


def valid_tag(tag):
    match = TAG.fullmatch(tag)
    return bool(match) and not any(part.isdigit() and len(part) > 1 and part[0] == '0'
                                   for part in (match.group(4) or '').split('.'))


def stable_tag(tag):
    return valid_tag(tag) and TAG.fullmatch(tag).group(4) is None


def promotion_source(tag, tags_at_revision):
    """Return the highest same-version `-rc.N` tag on the release commit, if any."""
    candidates = [(int(match.group(2)), name) for name in tags_at_revision
                  if (match := CANDIDATE.fullmatch(name)) and match.group(1) == tag]
    return max(candidates)[1] if candidates else None


def verify(root, tag, revision):
    if not valid_tag(tag) or not re.fullmatch('[0-9a-f]{40}', revision):
        raise ValueError('Release requires a valid v<SemVer> tag and full source commit')
    policy = json.loads((root / '.github/release-policy.json').read_text())
    if (policy.get('schemaVersion') != 1 or policy.get('platform') != 'linux/amd64'
            or policy.get('candidateOnly') is not True or policy.get('requireBranchTip') is not True):
        raise ValueError('Unsupported release policy; maintainer review required')
    branch = policy['allowedBranch']
    if not re.fullmatch('[A-Za-z0-9/_-]+', branch):
        raise ValueError('Invalid allowed release branch')
    def git(*args, check=True):
        result = subprocess.run(['git', *args], cwd=root, text=True, capture_output=True)
        if check and result.returncode:
            raise subprocess.CalledProcessError(result.returncode, args)
        return result
    def output(*args):
        return git(*args).stdout.strip()
    if output('rev-parse', '--verify', f'refs/tags/{tag}^{{commit}}') != revision:
        raise ValueError('Release tag does not identify the expected commit')
    if output('rev-parse', 'HEAD') != revision:
        raise ValueError('Checkout does not identify the release commit')
    if output('status', '--porcelain', '--untracked-files=all'):
        raise ValueError('Release checkout has uncommitted source changes')
    tip = output('rev-parse', '--verify', f'refs/remotes/origin/{branch}')
    result = dict(commit=revision, tag=tag, allowedBranch=branch, platform=policy['platform'], candidateOnly=True)
    if not stable_tag(tag):
        # Candidates build from source, so they must be the reviewed branch tip.
        if tip != revision:
            raise ValueError('Release candidate tag must identify the current allowed branch tip')
        return dict(result, mode='candidate', promotedFrom=None)
    source = promotion_source(tag, output('tag', '--points-at', revision).split())
    if source is None:
        raise ValueError(f'Stable releases are promoted, never rebuilt: tag {tag}-rc.N on this commit first')
    # Promotion reuses an already reviewed revision; the branch may have moved on.
    if git('merge-base', '--is-ancestor', revision, tip, check=False).returncode:
        raise ValueError('Promoted revision is not reachable from the allowed branch')
    return dict(result, mode='promote', promotedFrom=source)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tag', required=True)
    parser.add_argument('--revision', required=True)
    args = parser.parse_args()
    try:
        result = verify(ROOT, args.tag, args.revision)
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, str(error) + '\n' if isinstance(error, ValueError) else 'Release source policy could not be verified\n')
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write('commit=' + result['commit'] + '\n')
            output.write('mode=' + result['mode'] + '\n')
            output.write('promoted-from=' + (result['promotedFrom'] or '') + '\n')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
