#!/usr/bin/env python3
"""Select verified release subjects and assemble offline attestation JSONL."""
import argparse
import json
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'security'))
from verify import verify_files
from qualify import canonical_digest

ROLES = ('admin', 'consumer', 'worker', 'player', 'postgres', 'bundle', 'index')


def select_subject(root, role, revision, tag):
    directory = root / 'security-metadata'
    preliminary = json.loads((directory / 'release-metadata.json').read_text())
    bundles = [item for item in preliminary.get('subjects', []) if item.get('kind') == 'bundle']
    if len(bundles) != 1 or Path(bundles[0]['name']).name != bundles[0]['name']:
        raise ValueError('Expected exactly one safe deployment bundle name')
    bundle = root / 'release-files' / bundles[0]['name']
    index = verify_files(directory, bundle, revision, tag)
    if role == 'index':
        return {'kind': 'file', 'path': str(directory / 'release-metadata.json')}
    if role == 'bundle':
        return {'kind': 'file', 'path': str(bundle), 'sbom': str(directory / bundles[0]['sbom'])}
    if role not in ROLES:
        raise ValueError('Unknown attestation role')
    subject = next(item for item in index['subjects'] if item.get('role') == role and item['kind'] == 'image')
    name, digest = canonical_digest(subject['name']).split('@', 1)
    return {'kind': 'image', 'name': name, 'digest': digest, 'sbom': str(directory / subject['sbom'])}


def read_bundle(path):
    value = json.loads(path.read_text())
    if not isinstance(value, dict) or not value:
        raise ValueError('Expected a nonempty Sigstore bundle object')
    return value


def append_bundle(bundle, output):
    value = read_bundle(bundle)
    # Each action writes one JSON object, with no promised trailing newline.
    # Re-encode it rather than concatenating action files into invalid JSONL.
    if output.exists() and output.stat().st_size and not output.read_bytes().endswith(b'\n'):
        raise ValueError('Existing attestation output has an incomplete JSONL record')
    with output.open('a') as destination:
        destination.write(json.dumps(value, separators=(',', ':')) + '\n')


def merge_bundles(root, output):
    expected = {role + '.jsonl' for role in ROLES}
    if {path.name for path in root.iterdir()} != expected:
        raise ValueError('Expected exactly one attestation part for each release role')
    records = []
    for role in ROLES:
        values = [json.loads(line) for line in (root / (role + '.jsonl')).read_text().splitlines()]
        if len(values) != (1 if role == 'index' else 2) or any(not isinstance(value, dict) or not value for value in values):
            raise ValueError('Missing or malformed provenance/SBOM attestation part: ' + role)
        records.extend(values)
    with output.open('x') as destination:
        destination.write(''.join(json.dumps(value, separators=(',', ':')) + '\n' for value in records))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest='command', required=True)
    select = subparsers.add_parser('subject')
    select.add_argument('--root', type=Path, required=True)
    select.add_argument('--role', choices=ROLES, required=True)
    select.add_argument('--revision', required=True)
    select.add_argument('--tag', required=True)
    append = subparsers.add_parser('append')
    append.add_argument('--bundle', type=Path, required=True)
    append.add_argument('--output', type=Path, required=True)
    merge = subparsers.add_parser('merge')
    merge.add_argument('--root', type=Path, required=True)
    merge.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.command == 'subject':
        values = select_subject(args.root.resolve(), args.role, args.revision, args.tag)
        if any('\n' in value or '\r' in value for value in values.values()):
            raise ValueError('Multiline action outputs are forbidden')
        with Path(os.environ['GITHUB_OUTPUT']).open('a') as destination:
            destination.write(''.join(f'{key}={value}\n' for key, value in values.items()))
    elif args.command == 'append':
        append_bundle(args.bundle, args.output)
    else:
        merge_bundles(args.root, args.output)


if __name__ == '__main__':
    main()
