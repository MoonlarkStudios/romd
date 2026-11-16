#!/usr/bin/env python3
"""Verify a deployment bundle and every image it selects with `gh attestation verify`.

This is the same check an operator runs, using only the published bundle, its
checksum and the published attestation JSONL. It needs no GitHub login: each
attestation bundle is supplied locally, and images are resolved anonymously
from their public registries. The bundle's own checksum, manifest and
`images.env` are checked against an expected image map first, so a verified
bundle can only select the attested image digests.
"""
import argparse
from pathlib import Path
import re
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'security'))
from credentials import anonymous_environment
from qualify import canonical_digest, image_map, verify_bundle

PROVENANCE = 'https://slsa.dev/provenance/v1'
SBOM = 'https://cyclonedx.org/bom'


def attestations_name(tag):
    return f'romd-{tag}-attestations.jsonl'


def attestation_commands(bundle, images, attestations, repository, revision, tag, signer):
    common = ['--repo', repository, '--source-digest', revision, '--source-ref', 'refs/tags/' + tag,
              '--signer-workflow', signer, '--deny-self-hosted-runners', '--bundle', str(attestations)]
    subjects = [str(bundle)] + ['oci://' + canonical_digest(images[key]) for key in sorted(images)]
    return [['gh', 'attestation', 'verify', subject, *common, '--predicate-type', predicate]
            for subject in subjects for predicate in (PROVENANCE, SBOM)]


def verify_release(bundle, checksum, expected_images, attestations, repository, revision, tag, signer, runner=subprocess.run):
    if (not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository) or not re.fullmatch('[0-9a-f]{40}', revision)
            or not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?', tag)
            or signer != f'{repository}/.github/workflows/release.yml'):
        raise ValueError('Expected repository, full source commit, release tag and the release workflow signer')
    images = image_map(expected_images)
    with tempfile.TemporaryDirectory(prefix='romd-release-verify-') as temporary:
        work = Path(temporary)
        # Checksum, manifest, file inventory, revision, tag and images.env first.
        verify_bundle(Path(bundle), Path(checksum), expected_images, work / 'bundle', revision, tag)
        environment = anonymous_environment(work)
        for command in attestation_commands(Path(bundle).resolve(), images, Path(attestations).resolve(),
                                            repository, revision, tag, signer):
            result = runner(command, env=environment, cwd=work, capture_output=True, text=True, check=False)
            if result.returncode:
                subject = command[3].rsplit('/', 1)[-1]
                raise ValueError(f'Attestation verification failed for {subject} ({command[-1]}):\n{result.stderr.strip()}')
    return images


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    for name in ('bundle', 'checksum', 'images', 'attestations'):
        parser.add_argument('--' + name, type=Path, required=True)
    for name in ('repository', 'revision', 'tag'):
        parser.add_argument('--' + name, required=True)
    args = parser.parse_args()
    try:
        images = verify_release(args.bundle, args.checksum, args.images, args.attestations, args.repository,
                                args.revision, args.tag, f'{args.repository}/.github/workflows/release.yml')
    except (ValueError, OSError) as error:
        parser.exit(1, (str(error) if type(error) is ValueError else 'Release verification could not run') + '\n')
    print(f'Verified {args.bundle.name} and {len(images)} images: provenance and SBOM attestations from '
          f'{args.repository} release.yml at {args.tag} ({args.revision[:12]})')
    return 0


if __name__ == '__main__':
    sys.exit(main())
