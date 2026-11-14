"""GHCR reads and tags through crane (go-containerregistry), with the workflow token.

`crane tag` stores the manifest bytes already held under a digest, so the
digest, its layers and every attestation bound to it are unchanged; each tag
is read back afterwards to confirm it. The binary comes from the checksum pin
in `scripts/security/tools.json` and is rechecked before use. Credentials go to
a private, temporary Docker config through stdin, never on a command line.
"""
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import weakref

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'security'))
from tools import verify_installed

SERVICES = ('admin', 'consumer', 'worker', 'player')
DIGEST = re.compile(r'sha256:[0-9a-f]{64}')
REFERENCE = re.compile(r'ghcr\.io/([a-z0-9][a-z0-9._-]*/romd-(?:admin|consumer|worker|player))([:@])([0-9A-Za-z._:-]+)')


class Crane:
    def __init__(self, binary=None, runner=subprocess.run, verify=verify_installed):
        token = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
        actor = os.environ.get('GITHUB_ACTOR')
        binary = binary or os.environ.get('ROMD_CRANE')
        if not token or not actor or not binary:
            raise ValueError('Registry access requires the workflow token, actor and pinned crane (ROMD_CRANE)')
        self.binary = Path(binary).resolve()
        verify(self.binary.parent, ('crane',))
        self.runner = runner
        config = tempfile.mkdtemp(prefix='romd-crane-')
        # The private credential directory is removed when the client is discarded.
        weakref.finalize(self, shutil.rmtree, config, True)
        self.environment = {'PATH': os.environ.get('PATH', '/usr/bin:/bin'), 'HOME': config, 'DOCKER_CONFIG': config}
        if self._run(['auth', 'login', 'ghcr.io', '--username', actor, '--password-stdin'], token).returncode:
            raise ValueError('crane could not authenticate to ghcr.io')

    def _run(self, arguments, stdin=None):
        return self.runner([str(self.binary), *arguments], input=stdin, capture_output=True, text=True,
                           env=self.environment, timeout=300, check=False)

    def digest(self, reference):
        """Return the digest a ROMD reference names, or None only when GHCR says it does not exist."""
        match = REFERENCE.fullmatch(reference)
        if not match:
            raise ValueError('Expected an exact ghcr.io ROMD image reference')
        result = self._run(['digest', reference])
        if result.returncode == 0:
            value = result.stdout.strip()
            if not DIGEST.fullmatch(value):
                raise ValueError('crane returned an invalid digest')
            return value
        # Only the registry's own codes from the exact manifest endpoint establish
        # absence. Credential, token, transport and tool errors never do.
        repository, _, tag = match.groups()
        lines = result.stderr.strip().splitlines()
        missing = re.fullmatch(rf'Error: GET https://ghcr\.io/v2/{re.escape(repository)}/manifests/{re.escape(tag)}: '
                               r'(?:MANIFEST_UNKNOWN|NAME_UNKNOWN): .*', lines[-1] if lines else '')
        if missing:
            return None
        raise ValueError('Registry state could not be verified; nothing may be published')

    def tag(self, repository, digest, tag):
        """Add a tag to an existing digest and confirm the tag resolves to exactly that digest."""
        if not DIGEST.fullmatch(digest):
            raise ValueError('Expected an image digest')
        result = self._run(['tag', f'ghcr.io/{repository}@{digest}', tag])
        if result.returncode or self.digest(f'ghcr.io/{repository}:{tag}') != digest:
            raise ValueError(f'ghcr.io/{repository}:{tag} does not resolve to the qualified digest')


def image_repositories(images, owner):
    repositories = {}
    for service in SERVICES:
        reference = images[f'ROMD_{service.upper()}_IMAGE']
        match = re.fullmatch(r'ghcr\.io/([a-z0-9][a-z0-9._-]*/romd-[a-z]+)@(sha256:[0-9a-f]{64})', reference)
        if not match or match.group(1) != f'{owner}/romd-{service}':
            raise ValueError('Candidate image is not this repository\'s published ROMD image')
        repositories[service] = match.groups()
    return repositories
