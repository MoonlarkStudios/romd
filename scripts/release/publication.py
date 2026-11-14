#!/usr/bin/env python3
"""Publish a draft candidate without ever replacing an existing version digest.

A partial push is deliberately fail-closed on retry. Preserve the draft and
registry objects for maintainer disposition; use a new version for a new attempt.
No command in this tool promotes a candidate or creates a final release.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.request

from crane import Crane
from preflight import valid_tag
from rehearsal_evidence import verify_rehearsal, verify_published_bundle
from release_notes import render

ROOT = Path(__file__).resolve().parents[2]
COMMANDS = []


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, *args):
        return None


def release_metadata(repository, tag):
    token = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
    if not token:
        raise ValueError('Release publishing requires the scoped workflow token')
    matches = []
    opener = urllib.request.build_opener(NoRedirect)
    for page in range(1, 101):
        # Authenticated listing includes draft state. A tag lookup only exposes
        # published releases and cannot establish a safe partial-publish retry.
        request = urllib.request.Request(f'https://api.github.com/repos/{repository}/releases?per_page=100&page={page}',
            headers={'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
                     'X-GitHub-Api-Version': '2022-11-28'})
        try:
            with opener.open(request, timeout=30) as response:
                releases = json.load(response)
        except urllib.error.HTTPError as error:
            code = error.code
            error.close()
            raise ValueError(f'Release state lookup failed (HTTP {code}); no publication permitted') from None
        if not isinstance(releases, list):
            raise ValueError('Release state response is invalid')
        matches.extend(release for release in releases if release.get('tag_name') == tag)
        if len(matches) > 1:
            raise ValueError('Ambiguous release tag state; no publication permitted')
        if len(releases) < 100:
            return matches[0] if matches else None
    raise ValueError('Release state pagination incomplete; no publication permitted')


def allow_retry(metadata, revision):
    if metadata is None:
        return
    if not metadata.get('draft') or not metadata.get('prerelease'):
        raise ValueError('An exposed release already exists; automatic replacement is prohibited')
    if metadata.get('target_commitish') != revision or metadata.get('assets'):
        raise ValueError('Existing candidate has state/assets; maintainer disposition required before retry')


def run(command, capture=False):
    result = subprocess.run([str(part) for part in command], cwd=ROOT, check=False,
                            capture_output=True, text=True)
    COMMANDS.append(dict(command=[str(part) for part in command], exitCode=result.returncode,
                         stdoutSha256=hashlib.sha256(result.stdout.encode()).hexdigest(),
                         stderrSha256=hashlib.sha256(result.stderr.encode()).hexdigest()))
    if result.returncode:
        # Docker/GitHub failures may contain credentials or private addresses.
        raise RuntimeError(f'{Path(command[0]).name} failed (exit {result.returncode}); candidate remains unqualified')
    return result.stdout if capture else None


def staging_tag(revision):
    if not re.fullmatch('[0-9a-f]{40}', revision):
        raise ValueError('Staging tags require a full source commit')
    return 'sha-' + revision


def publish(args):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', args.repository) or not valid_tag(args.tag):
        raise ValueError('Invalid release repository or tag')
    if not re.fullmatch('[0-9a-f]{40}', args.revision):
        raise ValueError('Release revision must be a full commit')
    source = json.loads((ROOT / 'native/chdman/mame.json').read_text())
    with args.source_archive.open('rb') as stream:
        source_sha256 = hashlib.file_digest(stream, 'sha256').hexdigest()
    if (args.source_archive.stat().st_size != source['sourceBytes']
            or source_sha256 != source['sourceSha256']):
        raise ValueError('Worker source archive does not match the reviewed source pin')
    namespace = 'ghcr.io/' + args.repository.split('/')[0].lower()
    # Candidates are pushed only under a staging tag. Completion adds the version
    # tag to these exact digests after every gate passes, so a failed candidate
    # never leaves a version-tagged image.
    versions = {service: f'{namespace}/romd-{service}:{args.tag}'
                for service in ('admin', 'consumer', 'worker', 'player')}
    references = {service: f'{namespace}/romd-{service}:{staging_tag(args.revision)}' for service in versions}
    image_ids = {}
    for service in references:
        local = json.loads(run(['docker', 'image', 'inspect', f'romd-{service}:test'], True))[0]
        if (local['Os'], local['Architecture']) != ('linux', 'amd64'):
            raise ValueError('Only Linux amd64 candidate images may be published')
        labels = local['Config'].get('Labels', {}) or {}
        if (labels.get('org.opencontainers.image.revision') != args.revision
                or labels.get('org.opencontainers.image.version') != args.tag):
            raise ValueError('Built image source/version labels do not match release policy')
        # GHCR links a new package to this repository (and grants its workflow access) only
        # when the label names the repository before the first push.
        if labels.get('org.opencontainers.image.source', '').lower() != f'https://github.com/{args.repository}'.lower():
            raise ValueError('Built image source repository label does not match the publishing repository')
        image_ids[service] = local['Id']
    rehearsal = verify_rehearsal(getattr(args, 'rehearsal', None), args.revision, args.tag, image_ids,
                                json.loads((ROOT / 'scripts/deployment/base-images.json').read_text()))
    # Registry location changes no manifest content. Prepare and compare the
    # final payload before the first public write, using the rehearsed digests.
    images = {f'ROMD_{service.upper()}_IMAGE': reference.rsplit(':', 1)[0] + '@'
              + rehearsal['images'][f'ROMD_{service.upper()}_IMAGE'].rsplit('@', 1)[1]
              for service, reference in references.items()}
    images['ROMD_POSTGRES_IMAGE'] = rehearsal['images']['ROMD_POSTGRES_IMAGE']
    image_file = args.output / 'images.env'
    image_file.write_text(''.join(f'{key}={value}\n' for key, value in images.items()))
    bundle = args.output / f'romd-{args.tag}-deployment.tar.gz'
    run([ROOT / 'scripts/deployment/bundle.sh', image_file, bundle, args.revision, args.tag])
    verify_published_bundle(rehearsal, bundle, image_file, args.revision, args.tag)
    metadata = release_metadata(args.repository, args.tag)
    allow_retry(metadata, args.revision)
    # Inspect every remote version before pushing any image. Network/auth errors
    # are failures, never permission to overwrite a partially published version.
    crane = Crane()
    if any(crane.digest(reference) is not None for reference in versions.values()):
        raise ValueError('Version image already exists; retry cannot replace published digests')
    if metadata is None:
        notes = args.output / 'candidate-notes.txt'
        notes.write_text(render(args.repository, args.tag, args.revision, args.source_archive.name))
        run(['gh', 'release', 'create', args.tag, '--repo', args.repository, '--draft', '--prerelease',
             '--verify-tag', '--target', args.revision, '--title', args.tag, '--notes-file', notes])
    # Preserve GPL source before publishing the worker carrying chdman.
    run(['gh', 'release', 'upload', args.tag, args.source_archive, '--repo', args.repository])
    for service, reference in references.items():
        run(['docker', 'tag', image_ids[service], reference])
        run(['docker', 'push', reference])
        digest = crane.digest(reference)
        if digest != rehearsal['images'][f'ROMD_{service.upper()}_IMAGE'].rsplit('@', 1)[1]:
            raise ValueError('Staged image digest differs from the rehearsed image')
    state = args.output / 'candidate.json'
    state.write_text(json.dumps(dict(sourceCommit=args.revision, releaseTag=args.tag, images=images,
                                    bundleSha256=hashlib.sha256(bundle.read_bytes()).hexdigest(),
                                    result='draft-unqualified'), indent=2) + '\n')
    # Never --clobber release assets. Retrying a partial upload cannot silently
    # replace a source, digest manifest, deployment bundle or hash file.
    # The original image map, state and bundle stay in the same-run publisher
    # artifact, which downstream gates use as their trusted input.
    run(['gh', 'release', 'upload', args.tag, bundle, str(bundle) + '.sha256',
         '--repo', args.repository])
    # The release stays a draft: qualification reads the bundle from this run's
    # publisher artifact, and completion publishes the release once, immutably,
    # only after every gate passes. A failed candidate never becomes public.


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('repository', 'tag', 'revision'):
        parser.add_argument('--' + option, required=True)
    parser.add_argument('--source-archive', type=Path, required=True)
    parser.add_argument('--rehearsal', type=Path, required=True, help='Passed receipt for these exact built images')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    try:
        publish(args)
    except (ValueError, RuntimeError, OSError, urllib.error.URLError) as error:
        parser.exit(1, str(error) if isinstance(error, (ValueError, RuntimeError)) else 'Candidate publication failed\n')
    finally:
        (args.output / 'publication.json').write_text(json.dumps(dict(
            sourceCommit=args.revision, releaseTag=args.tag, commands=COMMANDS,
            environment='hosted/linux-amd64'), indent=2) + '\n')


if __name__ == '__main__':
    main()
