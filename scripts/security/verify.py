#!/usr/bin/env python3
"""Verify public release metadata and offline GitHub attestation bundles without credentials."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

from metadata import assert_postgres_version, sha256
from qualify import verify_bundle
from credentials import anonymous_environment
from policy import evaluate, validate_policy


def verify_policy_results(directory, subject, policy, tag):
    evaluations = json.loads((directory / subject['policy']).read_text())
    if isinstance(evaluations, dict):
        evaluations = [evaluations]
    if not isinstance(evaluations, list) or len(evaluations) != len(subject['scans']):
        raise ValueError('Release subject policy/scan coverage mismatch')
    for scan, result in zip(subject['scans'], evaluations):
        raw = json.loads((directory / scan).read_text())
        replay = evaluate(raw, 'sha256:' + subject['sha256'], policy, release_tag=tag)
        if result.get('result') != 'passed' or not isinstance(result.get('findings'), list) or len(result['findings']) != len(replay['findings']):
            raise ValueError('Release subject has unresolved or incomplete policy results')
        for expected, recorded in zip(replay['findings'], result['findings']):
            keys = ('id', 'component', 'installedVersion', 'subjectDigest', 'severity', 'fixedVersion', 'rawBlocked', 'excepted')
            if any(recorded.get(key) != expected.get(key) for key in keys) or recorded.get('blocked') is not False:
                raise ValueError('Release subject policy differs from its raw findings')
            # The separately attested reader proof may resolve Go package absence.
            # A manual decision must replay exactly against this tag and snapshot.
            if recorded.get('applicability', {}).get('status') == 'affected-packages-absent':
                proof = recorded['applicability'].get('evidence')
                if subject.get('role') not in ('admin', 'worker') or not recorded['component'].startswith('pkg:golang/') or not isinstance(proof, dict):
                    raise ValueError('Go package-absence proof missing')
                provenance = json.loads((directory / 'go-reader-provenance.json').read_text())
                qualified = json.loads(provenance['qualification.json'])
                if (proof.get('binarySha256') != qualified['files']['catalog-reader']
                        or any(proof.get(key) != qualified['source'].get(key)
                               for key in ('sourceCommit', 'sourceArchiveSha256', 'compilerImage'))):
                    raise ValueError('Go package-absence proof differs from qualified reader provenance')
            elif expected['blocked'] or recorded.get('riskAcceptance') != expected.get('riskAcceptance'):
                raise ValueError('Release subject has unreviewed vulnerability decisions')
        if any(finding.get('blocked') is not False for finding in result.get('goAdvisories', [])):
            raise ValueError('Release subject has blocked Go advisory results')


def verify_files(directory, bundle, revision, tag):
    index = json.loads((directory / 'release-metadata.json').read_text())
    if index.get('schemaVersion') != 1 or index.get('result') != 'passed' or index.get('sourceCommit') != revision or index.get('releaseTag') != tag:
        raise ValueError('Release metadata result/revision/tag mismatch')
    files = index.get('files')
    if not isinstance(files, dict) or not files:
        raise ValueError('Release metadata file inventory is missing')
    for name, expected in files.items():
        if Path(name).name != name or not re.fullmatch('[0-9a-f]{64}', expected) or sha256(directory / name) != expected:
            raise ValueError('Release metadata file checksum mismatch or unsafe path')
    if index.get('policy') not in files or index.get('vex') not in files:
        raise ValueError('Release metadata lacks hash-bound security policy or VEX decisions')
    policy = json.loads((directory / index['policy']).read_text())
    validate_policy(policy)
    subjects = index.get('subjects', [])
    images = [subject for subject in subjects if subject.get('kind') == 'image']
    bundles = [subject for subject in subjects if subject.get('kind') == 'bundle']
    if (len(images) != 5 or {subject.get('role') for subject in images} != {'admin', 'consumer', 'worker', 'player', 'postgres'}
            or len(bundles) != 1 or len(subjects) != 6):
        raise ValueError('Release metadata must cover all five images and one bundle')
    for subject in subjects:
        if not re.fullmatch('[0-9a-f]{64}', subject.get('sha256', '')) or subject.get('sbom') not in files:
            raise ValueError('Release metadata subject is not hash bound')
        sbom = json.loads((directory / subject['sbom']).read_text())
        hashes = sbom.get('metadata', {}).get('component', {}).get('hashes', [])
        if {'alg': 'SHA-256', 'content': subject['sha256']} not in hashes:
            raise ValueError('SBOM subject digest mismatch')
        if subject.get('role') == 'postgres':
            assert_postgres_version(sbom, policy)
        if subject['kind'] == 'image' and not subject.get('name', '').endswith('@sha256:' + subject['sha256']):
            raise ValueError('Image subject is mutable or has a mismatched digest')
        if subject.get('policy') not in files or not subject.get('scans') or any(name not in files for name in subject['scans']):
            raise ValueError('Release subject lacks hash-bound scans or policy results')
        verify_policy_results(directory, subject, policy, tag)
    if bundle.name != bundles[0]['name'] or sha256(bundle) != bundles[0]['sha256']:
        raise ValueError('Downloaded deployment bundle checksum mismatch')
    # Independent valid attestations do not prove that these image subjects are
    # the images selected by this bundle. Verify that semantic binding too.
    with tempfile.TemporaryDirectory(prefix='romd-verify-subject-binding-') as temporary:
        work = Path(temporary)
        expected_images = work / 'publisher-images.env'
        expected_images.write_text(''.join(f'ROMD_{subject["role"].upper()}_IMAGE={subject["name"]}\n'
                                          for subject in images))
        checksum = work / 'bundle.sha256'
        checksum.write_text(f'{bundles[0]["sha256"]}  {bundle.name}\n')
        verify_bundle(bundle, checksum, expected_images, work / 'extracted', revision, tag)
    return index


def matching_predicate(verified, expected):
    return any(result.get('verificationResult', {}).get('statement', {}).get('predicate') == expected for result in verified)


def verify_attestations(directory, bundle, index, args):
    with tempfile.TemporaryDirectory(prefix='romd-public-attestation-') as temporary:
        # Explicitly discard local registry/GitHub credentials. Public access is
        # a release requirement, and offline bundles avoid authenticated API use.
        environment = anonymous_environment(Path(temporary))
        common = ['--repo', args.repository, '--source-digest', args.revision,
                  '--source-ref', 'refs/tags/' + args.tag, '--signer-workflow', args.signer_workflow,
                  '--deny-self-hosted-runners', '--format', 'json']

        def verify(subject, predicate):
            command = ['gh', 'attestation', 'verify', str(subject), *common, '--bundle', args.attestations,
                       '--predicate-type', predicate]
            result = subprocess.run(command, env=environment, cwd=temporary, capture_output=True, check=True, text=True)
            verified = json.loads(result.stdout)
            if not isinstance(verified, list) or not verified:
                raise ValueError('No verified attestation returned')
            return verified

        provenance = 'https://slsa.dev/provenance/v1'
        verify(directory / 'release-metadata.json', provenance)
        verify(bundle, provenance)
        for subject in index['subjects']:
            artifact = 'oci://' + subject['name'] if subject['kind'] == 'image' else str(bundle)
            if subject['kind'] == 'image':
                verify(artifact, provenance)
            verified = verify(artifact, 'https://cyclonedx.org/bom')
            expected = json.loads((directory / subject['sbom']).read_text())
            if not matching_predicate(verified, expected):
                raise ValueError('Verified SBOM predicate differs from the downloaded SBOM')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--metadata-dir', type=Path, required=True)
    parser.add_argument('--bundle', type=Path, required=True)
    parser.add_argument('--attestations', type=Path, required=True, help='Downloaded JSONL containing all provenance and SBOM attestation bundles')
    parser.add_argument('--repository', required=True)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--tag', required=True)
    parser.add_argument('--signer-workflow', required=True, help='owner/repo/.github/workflows/release.yml')
    args = parser.parse_args()
    if (not re.fullmatch('[0-9a-f]{40}', args.revision) or not re.fullmatch('[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', args.repository)
            or not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?', args.tag)):
        parser.error('Expected repository identity and full source commit')
    args.metadata_dir = args.metadata_dir.resolve()
    args.bundle = args.bundle.resolve()
    args.attestations = args.attestations.resolve()
    index = verify_files(args.metadata_dir, args.bundle, args.revision, args.tag)
    verify_attestations(args.metadata_dir, args.bundle, index, args)
    print('Verified bundle, all image digests, SBOM predicates, metadata checksums and release provenance')


if __name__ == '__main__':
    main()
