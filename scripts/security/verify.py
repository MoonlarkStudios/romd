"""Verify generated security metadata files and bind them to a deployment bundle.

Used for pre-publication rehearsal evidence and to select attestation subjects.
Published releases are verified with `gh attestation verify`
(scripts/release/verify_release.py).
"""
import json
from pathlib import Path
import re
import tempfile

from metadata import assert_coverage, assert_fresh_database, assert_scanner_coverage, sha256
from qualify import verify_bundle


def verify_files(directory, bundle, revision, tag, *, rehearsal=False):
    index = json.loads((directory / 'release-metadata.json').read_text())
    if index.get('schemaVersion') != 1 or index.get('result') != 'passed' or index.get('sourceCommit') != revision or index.get('releaseTag') != tag:
        raise ValueError('Release metadata result/revision/tag mismatch')
    expected_type = 'disposable-rehearsal' if rehearsal else 'disposable-release'
    if index.get('evidenceType', 'disposable-release') != expected_type:
        raise ValueError('Security metadata evidence type mismatch')
    files = index.get('files')
    if not isinstance(files, dict) or not files:
        raise ValueError('Release metadata file inventory is missing')
    for name, expected in files.items():
        if Path(name).name != name or not re.fullmatch('[0-9a-f]{64}', expected) or sha256(directory / name) != expected:
            raise ValueError('Release metadata file checksum mismatch or unsafe path')
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
        if subject['kind'] == 'image' and not subject.get('name', '').endswith('@sha256:' + subject['sha256']):
            raise ValueError('Image subject is mutable or has a mismatched digest')
        if rehearsal:
            role = subject.get('role')
            scans = subject.get('scans')
            expected_scans = ([f'{role}.image.trivy.json', f'{role}.sbom.trivy.json']
                              if subject['kind'] == 'image' else ['bundle.trivy.json'])
            if scans != expected_scans or any(name not in files for name in expected_scans):
                raise ValueError('Rehearsal image/SBOM/bundle scans are missing')
            if subject['kind'] == 'image':
                assert_coverage(sbom, role)
                for mode, name in zip(('image', 'sbom'), scans):
                    assert_scanner_coverage(json.loads((directory / name).read_text()), role, mode)
            if subject.get('policy') not in files:
                raise ValueError('Rehearsal security policy evidence is missing')
            if role in ('admin', 'worker') and (subject.get('vex') != f'{role}.vex.json' or subject['vex'] not in files):
                raise ValueError('Rehearsal catalog reader VEX evidence is missing')
            records = json.loads((directory / subject['policy']).read_text())
            expected = 'reported' if role == 'postgres' else 'passed'
            if (not isinstance(records, list) or [item.get('scan') for item in records] != subject['scans']
                    or any(item.get('result') != expected for item in records)):
                raise ValueError('Rehearsal security policy did not pass all image/SBOM/bundle gates')
    if rehearsal:
        commands = index.get('commands')
        if not isinstance(commands, list) or not commands or any(item.get('exitCode') != 0 for item in commands):
            raise ValueError('Rehearsal security commands are missing or failed')
        if 'trivy-version.json' not in files:
            raise ValueError('Rehearsal vulnerability database evidence is missing')
        assert_fresh_database(json.loads((directory / 'trivy-version.json').read_text()))
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
