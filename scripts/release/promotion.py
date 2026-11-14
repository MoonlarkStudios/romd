#!/usr/bin/env python3
"""Promote a qualified release candidate to a stable version without rebuilding.

The stable release reuses the candidate's exact, already published image
digests. Before any public write this tool proves that:

- the candidate's release workflow run on the same commit succeeded, including
  public-evidence completion;
- the candidate is a published, immutable GitHub release whose bundle matches
  GitHub's release attestation;
- the candidate's bundle and every image it selects verify with
  `gh attestation verify` against the release workflow, its commit and its tag;
- a bundle rebuilt from the same commit for the stable tag differs from the
  candidate bundle only in its manifest's `releaseTag`.

It then adds the stable version tag to each existing image manifest (copying the
exact manifest bytes, so digests and attestations are unchanged) and creates a
draft prerelease with the GPL source and the promoted bundle. The draft then
passes the same qualification, final-digest security metadata, attestation and
publication gates as a built candidate. Marking it the latest full release
remains a maintainer decision.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import tarfile
import tempfile
import urllib.error
import urllib.parse
import urllib.request

import publication
from publication import NoRedirect, allow_retry, release_metadata, run
from verify_release import attestations_name, verify_release
from preflight import CANDIDATE, stable_tag, valid_tag
from qualify import image_map, verify_bundle
from release_notes import render

ROOT = Path(__file__).resolve().parents[2]
SERVICES = ('admin', 'consumer', 'worker', 'player')
# Must equal the publish-complete job name in release.yml; a qualified candidate
# is one whose run completed this job.
COMPLETION_JOB = 'Verify attestations and publish the immutable qualified prerelease'
MANIFEST_TYPES = ('application/vnd.oci.image.index.v1+json', 'application/vnd.docker.distribution.manifest.list.v2+json',
                  'application/vnd.oci.image.manifest.v1+json', 'application/vnd.docker.distribution.manifest.v2+json')


def api_get(path):
    token = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
    if not token:
        raise ValueError('Promotion requires the scoped workflow token')
    request = urllib.request.Request('https://api.github.com' + path, headers={
        'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28'})
    try:
        with urllib.request.build_opener(NoRedirect).open(request, timeout=30) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        code = error.code
        error.close()
        raise ValueError(f'Candidate workflow lookup failed (HTTP {code}); no promotion permitted') from None


def qualified_candidate_run(repository, candidate, revision, get=api_get):
    """Return the successful same-commit release run that completed the candidate."""
    query = urllib.parse.urlencode({'head_sha': revision, 'event': 'push', 'per_page': 100})
    payload = get(f'/repos/{repository}/actions/workflows/release.yml/runs?{query}')
    runs = payload.get('workflow_runs') if isinstance(payload, dict) else None
    if not isinstance(runs, list):
        raise ValueError('Candidate workflow response is invalid')
    for item in sorted((item for item in runs if isinstance(item, dict)), key=lambda item: item.get('id', 0), reverse=True):
        if (item.get('head_sha') != revision or item.get('head_branch') != candidate or item.get('event') != 'push'
                or item.get('path') != '.github/workflows/release.yml'
                or (item.get('repository') or {}).get('full_name') != repository
                or item.get('status') != 'completed' or item.get('conclusion') != 'success'):
            continue
        jobs = get(f'/repos/{repository}/actions/runs/{item["id"]}/jobs?filter=latest&per_page=100')
        jobs = jobs.get('jobs') if isinstance(jobs, dict) else None
        if not isinstance(jobs, list):
            raise ValueError('Candidate job response is invalid')
        completion = [job for job in jobs if isinstance(job, dict) and job.get('name') == COMPLETION_JOB]
        if len(completion) == 1 and completion[0].get('conclusion') == 'success':
            return item['id']
    raise ValueError(f'{candidate} has no successful release workflow run on {revision}; only a qualified '
                     'candidate can be promoted')


def candidate_assets(candidate, source_name):
    bundle = f'romd-{candidate}-deployment.tar.gz'
    return {bundle, bundle + '.sha256', attestations_name(candidate), source_name}


def check_candidate_release(metadata, candidate, revision, source_name):
    if metadata is None:
        raise ValueError(f'{candidate} has no GitHub release')
    if metadata.get('draft') or metadata.get('immutable') is not True or metadata.get('target_commitish') != revision:
        raise ValueError(f'{candidate} is not a published immutable release of this commit')
    names = {asset.get('name') for asset in metadata.get('assets', []) if isinstance(asset, dict)}
    if names != candidate_assets(candidate, source_name):
        raise ValueError(f'{candidate} release does not carry exactly the qualified public assets')


def compare_bundles(candidate_bundle, promoted_bundle, images, revision, candidate, tag, work):
    """Prove the rebuilt bundle changes nothing but the manifest's release tag."""
    original = verify_bundle(candidate_bundle, Path(str(candidate_bundle) + '.sha256'), images,
                             work / 'candidate-bundle', revision, candidate)
    promoted = verify_bundle(promoted_bundle, Path(str(promoted_bundle) + '.sha256'), images,
                             work / 'promoted-bundle', revision, tag)
    if original['files'] != promoted['files'] or original['images'] != promoted['images']:
        raise ValueError('Promoted bundle files or image map differ from the qualified candidate')
    for relative in original['files']:
        a, b = work / 'candidate-bundle' / relative, work / 'promoted-bundle' / relative
        if a.read_bytes() != b.read_bytes() or (a.stat().st_mode & 0o111) != (b.stat().st_mode & 0o111):
            raise ValueError('Promoted bundle content or modes differ from the qualified candidate')
    before = json.loads((work / 'candidate-bundle/manifest.json').read_text())
    after = json.loads((work / 'promoted-bundle/manifest.json').read_text())
    if dict(before, releaseTag=tag) != after:
        raise ValueError('Promoted manifest differs from the candidate beyond its release tag')


class Registry:
    """Minimal authenticated registry client that tags existing manifests by exact bytes."""

    def __init__(self, repository, opener=None):
        token = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
        actor = os.environ.get('GITHUB_ACTOR')
        if not token or not actor:
            raise ValueError('Authenticated registry promotion requires the workflow token and actor')
        self.repository = repository
        self.opener = opener or urllib.request.build_opener(NoRedirect)
        query = urllib.parse.urlencode({'service': 'ghcr.io', 'scope': f'repository:{repository}:pull,push'})
        basic = base64.b64encode((actor + ':' + token).encode()).decode()
        with self.opener.open(urllib.request.Request('https://ghcr.io/token?' + query,
                              headers={'Authorization': 'Basic ' + basic}), timeout=30) as response:
            bearer = json.load(response).get('token')
        if not isinstance(bearer, str) or not bearer:
            raise ValueError('Registry did not issue a scoped token')
        self.bearer = bearer

    def request(self, method, reference, data=None, content_type=None):
        headers = {'Authorization': 'Bearer ' + self.bearer}
        if data is None:
            headers['Accept'] = ', '.join(MANIFEST_TYPES)
        else:
            headers['Content-Type'] = content_type
        return self.opener.open(urllib.request.Request(
            f'https://ghcr.io/v2/{self.repository}/manifests/{reference}', data=data, headers=headers, method=method),
            timeout=60)

    def manifest(self, digest):
        with self.request('GET', digest) as response:
            body, kind = response.read(), response.headers.get('Content-Type', '').split(';')[0].strip()
        if 'sha256:' + hashlib.sha256(body).hexdigest() != digest or kind not in MANIFEST_TYPES:
            raise ValueError('Registry returned a manifest that does not match the qualified digest')
        return body, kind

    def tagged_digest(self, tag):
        try:
            with self.request('HEAD', tag) as response:
                digest = response.headers.get('Docker-Content-Digest', '')
        except urllib.error.HTTPError as error:
            with error:
                if error.code == 404:
                    return None
            raise ValueError('Registry tag state could not be verified') from None
        if not re.fullmatch(r'sha256:[0-9a-f]{64}', digest):
            raise ValueError('Registry returned an invalid tag digest')
        return digest

    def tag(self, digest, tag):
        body, kind = self.manifest(digest)
        with self.request('PUT', tag, body, kind) as response:
            if response.headers.get('Docker-Content-Digest') != digest:
                raise ValueError('Registry stored a different digest for the promoted tag')
        if self.tagged_digest(tag) != digest:
            raise ValueError('Promoted image tag does not resolve to the qualified digest')


def image_repositories(images, owner):
    repositories = {}
    for service in SERVICES:
        reference = images[f'ROMD_{service.upper()}_IMAGE']
        match = re.fullmatch(r'ghcr\.io/([a-z0-9][a-z0-9._-]*/romd-[a-z]+)@(sha256:[0-9a-f]{64})', reference)
        if not match or match.group(1) != f'{owner}/romd-{service}':
            raise ValueError('Candidate image is not this repository\'s published ROMD image')
        repositories[service] = match.groups()
    return repositories


def bundle_images(bundle, destination):
    """Copy the bundle's own image map; the bundle's attestation then authenticates it."""
    with tarfile.open(bundle, 'r:gz') as archive:
        members = [member for member in archive if member.name in ('images.env', './images.env')]
        if len(members) != 1 or not members[0].isfile() or members[0].size > 4096:
            raise ValueError('Candidate bundle must contain exactly one images.env')
        destination.write_bytes(archive.extractfile(members[0]).read())
    return destination


def verify_candidate(args, source_name, work):
    """Authenticate the candidate's published assets; return its verified paths."""
    run_id = qualified_candidate_run(args.repository, args.candidate, args.revision)
    check_candidate_release(release_metadata(args.repository, args.candidate), args.candidate, args.revision, source_name)
    download = work / 'candidate'
    bundle = f'romd-{args.candidate}-deployment.tar.gz'
    attestations = attestations_name(args.candidate)
    run(['gh', 'release', 'download', args.candidate, '--repo', args.repository, '--dir', download,
         '--pattern', bundle, '--pattern', bundle + '.sha256', '--pattern', attestations])
    # GitHub's immutable-release attestation binds these bytes to the candidate release.
    run(['gh', 'release', 'verify-asset', args.candidate, download / bundle, '--repo', args.repository])
    images = bundle_images(download / bundle, work / 'candidate-images.env')
    verify_release(download / bundle, download / (bundle + '.sha256'), images, download / attestations,
                   args.repository, args.revision, args.candidate, f'{args.repository}/.github/workflows/release.yml')
    return run_id, download / bundle, images


def check_source_archive(path):
    source = json.loads((ROOT / 'native/chdman/mame.json').read_text())
    with path.open('rb') as stream:
        if (path.stat().st_size != source['sourceBytes']
                or hashlib.file_digest(stream, 'sha256').hexdigest() != source['sourceSha256']):
            raise ValueError('Worker source archive does not match the reviewed source pin')


def promote(args):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', args.repository) or not stable_tag(args.tag):
        raise ValueError('Promotion requires a repository and a stable v<SemVer> tag')
    match = CANDIDATE.fullmatch(args.candidate) if valid_tag(args.candidate) else None
    if not match or match.group(1) != args.tag:
        raise ValueError('Promotion source must be a -rc.N candidate of the same version')
    if not re.fullmatch('[0-9a-f]{40}', args.revision):
        raise ValueError('Promotion revision must be a full commit')
    check_source_archive(args.source_archive)
    with tempfile.TemporaryDirectory(prefix='romd-promotion-') as temporary:
        work = Path(temporary)
        run_id, candidate_bundle, candidate_images = verify_candidate(args, args.source_archive.name, work)
        # Reuse the verified candidate bundle's own image map byte-for-byte.
        verify_bundle(candidate_bundle, Path(str(candidate_bundle) + '.sha256'), candidate_images,
                      work / 'candidate-source', args.revision, args.candidate)
        images = image_map(work / 'candidate-source/images.env')
        repositories = image_repositories(images, args.repository.split('/')[0].lower())
        image_file = args.output / 'images.env'
        shutil.copyfile(work / 'candidate-source/images.env', image_file)
        bundle = args.output / f'romd-{args.tag}-deployment.tar.gz'
        run([ROOT / 'scripts/deployment/bundle.sh', image_file, bundle, args.revision, args.tag])
        compare_bundles(candidate_bundle, bundle, image_file, args.revision, args.candidate, args.tag, work)
    # Everything above is read-only. Inspect all public state before the first write.
    metadata = release_metadata(args.repository, args.tag)
    allow_retry(metadata, args.revision)
    registries = {service: Registry(repository) for service, (repository, _) in repositories.items()}
    for service, (_, digest) in repositories.items():
        existing = registries[service].tagged_digest(args.tag)
        if existing not in (None, digest):
            raise ValueError('Version image tag already identifies a different digest; promotion cannot replace it')
    if metadata is None:
        notes = args.output / 'candidate-notes.txt'
        notes.write_text(render(args.repository, args.tag, args.revision, args.source_archive.name,
                                promoted_from=args.candidate))
        run(['gh', 'release', 'create', args.tag, '--repo', args.repository, '--draft', '--prerelease',
             '--verify-tag', '--target', args.revision, '--title', args.tag, '--notes-file', notes])
    run(['gh', 'release', 'upload', args.tag, args.source_archive, '--repo', args.repository])
    for service, (_, digest) in repositories.items():
        registries[service].tag(digest, args.tag)
    (args.output / 'candidate.json').write_text(json.dumps(dict(
        sourceCommit=args.revision, releaseTag=args.tag, images=images,
        bundleSha256=hashlib.sha256(bundle.read_bytes()).hexdigest(), result='draft-unqualified',
        promotedFrom=args.candidate,
        candidateRun=f'https://github.com/{args.repository}/actions/runs/{run_id}'), indent=2) + '\n')
    # The release stays a draft until every downstream gate passes, then
    # completion publishes it once, immutably.
    run(['gh', 'release', 'upload', args.tag, bundle, str(bundle) + '.sha256', '--repo', args.repository])


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    for option in ('repository', 'tag', 'revision', 'candidate'):
        parser.add_argument('--' + option, required=True)
    parser.add_argument('--source-archive', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    try:
        promote(args)
    except (ValueError, RuntimeError, OSError, KeyError, urllib.error.URLError) as error:
        parser.exit(1, (str(error) if isinstance(error, (ValueError, RuntimeError)) else 'Promotion failed') + '\n')
    finally:
        (args.output / 'publication.json').write_text(json.dumps(dict(
            sourceCommit=args.revision, releaseTag=args.tag, promotedFrom=args.candidate,
            commands=publication.COMMANDS, environment='hosted/linux-amd64'), indent=2) + '\n')
    print(f'Promoted {args.candidate} to draft {args.tag} without rebuilding; qualification follows')


if __name__ == '__main__':
    main()
