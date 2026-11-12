#!/usr/bin/env python3
"""Publish a draft candidate without ever replacing an existing version digest.

A partial push is deliberately fail-closed on retry. Preserve the draft and
registry objects for maintainer disposition; use a new version for a new attempt.
No command in this tool promotes a candidate or creates a final release.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.parse
import urllib.request

from preflight import valid_tag

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


def registry_exists(reference):
    match = re.fullmatch(r'ghcr\.io/([a-z0-9][a-z0-9._-]*/romd-(?:admin|consumer|worker|player)):([0-9A-Za-z.-]+)', reference)
    token = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
    actor = os.environ.get('GITHUB_ACTOR')
    if not match or not token or not actor:
        raise ValueError('Authenticated exact-reference registry lookup is required')
    repository, tag = match.groups()
    opener = urllib.request.build_opener(NoRedirect)
    query = urllib.parse.urlencode({'service': 'ghcr.io', 'scope': f'repository:{repository}:pull,push'})
    basic = base64.b64encode((actor + ':' + token).encode()).decode()
    request = urllib.request.Request('https://ghcr.io/token?' + query,
                                     headers={'Authorization': 'Basic ' + basic})
    try:
        with opener.open(request, timeout=30) as response:
            bearer = json.load(response).get('token')
        if not isinstance(bearer, str) or not bearer:
            raise ValueError('Registry did not issue a scoped token')
        request = urllib.request.Request(f'https://ghcr.io/v2/{repository}/manifests/{tag}',
            headers={'Authorization': 'Bearer ' + bearer,
                     'Accept': 'application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json'})
        try:
            with opener.open(request, timeout=30) as response:
                if response.status != 200:
                    raise ValueError('Registry lookup returned an unexpected status')
                return True
        except urllib.error.HTTPError as error:
            # Only the authenticated exact manifest endpoint can establish
            # absence. Tool, credential, transport and generic 404 text cannot.
            with error:
                if error.code == 404:
                    payload = json.load(error)
                    codes = {item.get('code') for item in payload.get('errors', [])}
                    if codes and codes <= {'MANIFEST_UNKNOWN', 'NAME_UNKNOWN'}:
                        return False
            raise ValueError('Registry manifest state could not be verified') from None
    except (urllib.error.URLError, OSError, json.JSONDecodeError) as error:
        if isinstance(error, urllib.error.HTTPError):
            error.close()
        raise RuntimeError('Registry state lookup failed; no publication permitted') from None


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
    references = {service: f'{namespace}/romd-{service}:{args.tag}'
                  for service in ('admin', 'consumer', 'worker', 'player', 'postgres')}
    metadata = release_metadata(args.repository, args.tag)
    allow_retry(metadata, args.revision)
    # Inspect every remote version before pushing any image. Network/auth errors
    # are failures, never permission to overwrite. A partially published version
    # requires explicit maintainer disposition instead of an implicit rebuild.
    if any(registry_exists(reference) for reference in references.values()):
        raise ValueError('Version image already exists; retry cannot replace published digests')
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
    if metadata is None:
        notes = args.output / 'candidate-notes.txt'
        notes.write_text('Unqualified draft candidate. Packaged browser, recovery and security gates are pending.\n'
                         'Final production promotion requires the separate maintainer checkpoint.\n')
        run(['gh', 'release', 'create', args.tag, '--repo', args.repository, '--draft', '--prerelease',
             '--verify-tag', '--target', args.revision, '--title', args.tag, '--notes-file', notes])
    # Preserve GPL source before publishing the worker carrying chdman.
    run(['gh', 'release', 'upload', args.tag, args.source_archive, '--repo', args.repository])
    images = {}
    for service, reference in references.items():
        run(['docker', 'tag', f'romd-{service}:test', reference])
        run(['docker', 'push', reference])
        digest = json.loads(run(['docker', 'buildx', 'imagetools', 'inspect', reference,
                                 '--format', '{{json .Manifest.Digest}}'], True))
        if not re.fullmatch(r'sha256:[0-9a-f]{64}', digest):
            raise ValueError('Registry returned an invalid image digest')
        images[f'ROMD_{service.upper()}_IMAGE'] = reference.rsplit(':', 1)[0] + '@' + digest
    image_file = args.output / 'images.env'
    image_file.write_text(''.join(f'{key}={value}\n' for key, value in images.items()))
    bundle = args.output / f'romd-{args.tag}-deployment.tar.gz'
    run([ROOT / 'scripts/deployment/bundle.sh', image_file, bundle, args.revision, args.tag])
    state = args.output / 'candidate.json'
    state.write_text(json.dumps(dict(sourceCommit=args.revision, releaseTag=args.tag, images=images,
                                    bundleSha256=hashlib.sha256(bundle.read_bytes()).hexdigest(),
                                    result='draft-unqualified'), indent=2) + '\n')
    # Never --clobber release assets. Retrying a partial upload cannot silently
    # replace a source, digest manifest, deployment bundle or hash file.
    run(['gh', 'release', 'upload', args.tag, image_file, bundle, str(bundle) + '.sha256', state,
         '--repo', args.repository])
    # Expose an explicitly unqualified prerelease for the separate read-only
    # qualification runner. It remains a prerelease if any later gate fails.
    run(['gh', 'release', 'edit', args.tag, '--repo', args.repository, '--draft=false', '--prerelease'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ('repository', 'tag', 'revision'):
        parser.add_argument('--' + option, required=True)
    parser.add_argument('--source-archive', type=Path, required=True)
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
