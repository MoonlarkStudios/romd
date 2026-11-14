"""Minimal authenticated GHCR client that tags existing manifests by their exact bytes.

Tagging copies the manifest bytes already stored under a digest, so the digest,
its layers and every attestation bound to it are unchanged.
"""
import base64
import hashlib
import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request

SERVICES = ('admin', 'consumer', 'worker', 'player')
MANIFEST_TYPES = ('application/vnd.oci.image.index.v1+json', 'application/vnd.docker.distribution.manifest.list.v2+json',
                  'application/vnd.oci.image.manifest.v1+json', 'application/vnd.docker.distribution.manifest.v2+json')


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, *args):
        return None


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
                raise ValueError('Registry stored a different digest for the version tag')
        if self.tagged_digest(tag) != digest:
            raise ValueError('Version tag does not resolve to the qualified digest')


def image_repositories(images, owner):
    repositories = {}
    for service in SERVICES:
        reference = images[f'ROMD_{service.upper()}_IMAGE']
        match = re.fullmatch(r'ghcr\.io/([a-z0-9][a-z0-9._-]*/romd-[a-z]+)@(sha256:[0-9a-f]{64})', reference)
        if not match or match.group(1) != f'{owner}/romd-{service}':
            raise ValueError('Candidate image is not this repository\'s published ROMD image')
        repositories[service] = match.groups()
    return repositories
