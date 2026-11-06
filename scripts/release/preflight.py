#!/usr/bin/env python3
"""Reject malformed tags and unapproved source revisions before release gates."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
TAG = re.compile(r'v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?')


def valid_tag(tag):
    match = TAG.fullmatch(tag)
    return bool(match) and not any(part.isdigit() and len(part) > 1 and part[0] == '0'
                                   for part in (match.group(4) or '').split('.'))


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
    def git(*args):
        return subprocess.check_output(['git', *args], cwd=root, text=True, stderr=subprocess.DEVNULL).strip()
    if git('rev-parse', '--verify', f'refs/tags/{tag}^{{commit}}') != revision:
        raise ValueError('Release tag does not identify the expected commit')
    if git('rev-parse', 'HEAD') != revision:
        raise ValueError('Checkout does not identify the release commit')
    if git('rev-parse', '--verify', f'refs/remotes/origin/{branch}') != revision:
        raise ValueError('Release tag must identify the current allowed branch tip')
    if git('status', '--porcelain', '--untracked-files=all'):
        raise ValueError('Release checkout has uncommitted source changes')
    return dict(commit=revision, tag=tag, allowedBranch=branch, platform=policy['platform'], candidateOnly=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tag', required=True)
    parser.add_argument('--revision', required=True)
    args = parser.parse_args()
    try:
        result = verify(ROOT, args.tag, args.revision)
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, str(error) if isinstance(error, ValueError) else 'Release source policy could not be verified\n')
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write('commit=' + result['commit'] + '\n')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
