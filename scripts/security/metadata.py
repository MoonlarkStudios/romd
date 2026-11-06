#!/usr/bin/env python3
"""Generate final-digest SBOMs, vulnerability results and a hash-bound release index."""
import argparse
import copy
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import tempfile
from urllib.parse import quote

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'release'))
from qualify import image_map, verify_bundle, verify_publisher
from policy import evaluate
from tools import verify_installed
from credentials import anonymous_environment
import go_reader
from go_applicability import assess as assess_go_reader


def sha256(path):
    with Path(path).open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def json_write(path, value):
    Path(path).write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def merge_sbom(sbom, supplements, name, digest):
    merged = copy.deepcopy(sbom)
    if merged.get('bomFormat') != 'CycloneDX' or not isinstance(merged.get('components'), list):
        raise ValueError('Expected a CycloneDX component inventory')
    previous_root = merged.get('metadata', {}).get('component')
    if previous_root and previous_root.get('bom-ref') and not any(component.get('bom-ref') == previous_root['bom-ref'] for component in merged['components']):
        merged['components'].append(copy.deepcopy(previous_root))
    for supplement in supplements:
        if supplement.get('bomFormat') != 'CycloneDX' or not isinstance(supplement.get('components'), list):
            raise ValueError('Expected image-carried CycloneDX supplement')
        # Preserve Syft's component refs/dependency graph; supplement refs carry a
        # namespace so they cannot collide with the original inventory.
        for component in supplement['components']:
            component = copy.deepcopy(component)
            component['bom-ref'] = 'romd:supplement:' + component.get('purl', component['name'])
            merged['components'].append(component)
    merged.setdefault('metadata', {})['component'] = {
        'type': 'container' if '@sha256:' in name else 'file', 'name': name,
        'hashes': [{'alg': 'SHA-256', 'content': digest}],
        'bom-ref': 'romd:subject:' + digest,
    }
    merged.setdefault('dependencies', []).append({'ref': 'romd:subject:' + digest,
        'dependsOn': [component['bom-ref'] for component in merged['components'] if component.get('bom-ref')]})
    return merged


def native_supplement(name, manifest_path, binary_path):
    manifest = json.loads(manifest_path.read_text())
    if name == 'libchdr':
        expected, version, commit = (manifest.get(key) for key in ('sha256', 'upstreamVersion', 'upstreamCommit'))
        repository = 'rtissera/libchdr'
    else:
        expected = manifest.get('binary', {}).get('sha256')
        version, commit = (manifest.get('pin', {}).get(key) for key in ('version', 'commit'))
        repository = 'mamedev/mame'
    if (not isinstance(commit, str) or not re.fullmatch('[0-9a-f]{40}', commit)
            or not isinstance(version, str) or not version or sha256(binary_path) != expected):
        raise ValueError('Packaged native binary does not match its build receipt/source identity')
    component = {'type': 'library' if name == 'libchdr' else 'application', 'name': name, 'version': version,
                 'purl': f'pkg:github/{repository}@{commit}',
                 'hashes': [{'alg': 'SHA-256', 'content': expected}],
                 'properties': [{'name': 'romd:build-receipt-sha256', 'value': sha256(manifest_path)},
                                {'name': 'romd:source-commit', 'value': commit}]}
    return {'bomFormat': 'CycloneDX', 'components': [component]}


def catalog_supplement(receipt_path, binary_path):
    receipt = json.loads(receipt_path.read_text())
    commit = receipt.get('sourceCommit', '')
    if (receipt.get('schemaVersion') != 1 or receipt.get('module') != 'github.com/MoonlarkStudios/romd-dat-catalogs'
            or not re.fullmatch('[0-9a-f]{40}', commit) or sha256(binary_path) != receipt.get('binarySha256')):
        raise ValueError('Packaged catalog reader does not match its source/build receipt')
    return {'bomFormat': 'CycloneDX', 'components': [{'type': 'application', 'name': receipt['module'],
        'version': commit, 'purl': f'pkg:golang/{receipt["module"]}@{commit}',
        'hashes': [{'alg': 'SHA-256', 'content': receipt['binarySha256']}],
        'properties': [{'name': 'romd:source-commit', 'value': commit},
                       {'name': 'romd:build-receipt-sha256', 'value': sha256(receipt_path)}]}]}


def assert_coverage(sbom, role):
    components = sbom.get('components', [])
    names = {component.get('name', '').lower() for component in components}
    purls = [component.get('purl', '') for component in components]
    required = {'os': any(purl.startswith(('pkg:deb/', 'pkg:apk/', 'pkg:rpm/')) for purl in purls)}
    if role in ('admin', 'consumer', 'worker'):
        required.update(backend=any(purl.startswith('pkg:nuget/') for purl in purls)
                        and {'npgsql', 'microsoft.entityframeworkcore'} <= names,
                        libchdr='libchdr' in names)
    if role in ('admin', 'worker'):
        required['catalog-reader'] = any('romd-dat-catalogs' in component.get('name', '').lower()
            and component.get('purl', '').startswith('pkg:golang/') for component in components)
    if role in ('admin', 'consumer'):
        required.update(frontend={'react', '@mantine/core', 'oidc-client-ts'} <= names)
    if role == 'player':
        required['external-browser-runtime'] = 'emulatorjs' in names
    if role == 'worker':
        required['chdman'] = 'chdman' in names
    missing = [name for name, present in required.items() if not present]
    if missing:
        raise ValueError('Final-image SBOM coverage missing: ' + ', '.join(missing))
    return required


def assert_fresh_database(version, now=None):
    now = now or datetime.now(timezone.utc)
    updated = datetime.fromisoformat(version['VulnerabilityDB']['UpdatedAt'].replace('Z', '+00:00'))
    if updated.tzinfo is None or not -timedelta(hours=1) <= now - updated <= timedelta(hours=72):
        raise ValueError('Vulnerability database is stale or has an invalid future timestamp')


def assert_scanner_coverage(scan, role, mode):
    results = scan.get('Results', [])
    names = {package.get('Name', '').lower() for result in results for package in result.get('Packages', [])}
    if not any(result.get('Class') == 'os-pkgs' and result.get('Packages') for result in results):
        raise ValueError('Final-image/image-SBOM scan did not inventory OS packages')
    if role in ('admin', 'consumer', 'worker') and not {'npgsql', 'microsoft.entityframeworkcore'} <= names:
        raise ValueError('Final-image scan did not inventory expected backend dependencies')
    if mode == 'sbom' and role in ('admin', 'consumer') and not {'react', '@mantine/core', 'oidc-client-ts'} <= names:
        raise ValueError('Final-image SBOM scan did not inventory expected frontend dependencies')
    if mode == 'sbom' and role == 'player' and not {'react', '@mantine/core'} <= names:
        raise ValueError('Player SBOM scan did not inventory its installed production frontend dependency closure')
    if role in ('admin', 'worker') and not (any(name.startswith('github.com/sigstore/') for name in names)
            and any(name.startswith('github.com/theupdateframework/') for name in names)):
        raise ValueError('Catalog reader scan did not inventory linked Go trust dependencies')
    if scan.get('Metadata', {}).get('OS', {}).get('EOSL') is True:
        raise ValueError('Final-image operating system is outside security support')


class Commands:
    def __init__(self, work, output):
        self.work, self.output, self.records = work, output, []
        self.env = anonymous_environment(work)
        self.env['TRIVY_CACHE_DIR'] = str(work / 'trivy-cache')
        (work / 'empty-ignore').touch()
        json_write(work / 'empty-config.json', {})

    def run(self, command, stage, timeout=1200):
        record = {'stage': stage, 'command': [str(value).replace(str(self.work), '${WORK}')
                    for value in command], 'cwd': '${EMPTY_WORK}', 'environment': 'disposable-release/linux-amd64'}
        self.records.append(record)
        result = subprocess.run([str(value) for value in command], cwd=self.work, env=self.env,
                                capture_output=True, timeout=timeout)
        record.update(exitCode=result.returncode, stdoutSha256=hashlib.sha256(result.stdout).hexdigest(),
                      stderrSha256=hashlib.sha256(result.stderr).hexdigest())
        if result.returncode:
            raise RuntimeError(stage + ' failed; scanner/command output is not copied into public diagnostic logs')
        if stage == 'go-reader-govulncheck-binary-package' and result.stderr.strip():
            raise RuntimeError('govulncheck diagnostics require investigation')
        return result.stdout.decode()

    def extract(self, container, source, target):
        self.run(['docker', 'cp', container + ':' + source, target], 'extract-image-component-metadata')


def scan_packaged_reader(binary, receipt, artifacts, evidence, work, output, commands, role):
    go_reader.assert_final_binding(binary, receipt, evidence)
    scan_output = work / role
    try:
        return go_reader.scan(artifacts, evidence, scan_output, commands.run, binary=binary)
    finally:
        # Malformed/truncated/non-clean output is investigation evidence too.
        if scan_output.is_dir():
            json_write(output / f'{role}.go-reader.json', go_reader.retained_scan(scan_output))


def generate(args):
    if platform.system() != 'Linux' or platform.machine() not in ('x86_64', 'amd64'):
        raise ValueError('Release scanners require a native Linux amd64 runner')
    pins = verify_installed(args.tools_dir)
    syft, trivy = args.tools_dir / 'syft', args.tools_dir / 'trivy'
    args.output.mkdir(parents=True, exist_ok=False, mode=0o700)
    report = {'schemaVersion': 1, 'result': 'failed', 'sourceCommit': args.revision, 'releaseTag': args.tag,
              'tools': pins, 'subjects': [], 'commands': []}
    try:
        with tempfile.TemporaryDirectory(prefix='romd-release-metadata-') as temporary:
            work = Path(temporary)
            bundle = work / 'bundle'
            verify_publisher(args.bundle, args.publisher_state, args.images, args.revision, args.tag)
            verified = verify_bundle(args.bundle, args.checksum, args.images, bundle, args.revision, args.tag)
            commands = Commands(work, args.output)
            report['commands'] = commands.records
            policy = json.loads(args.policy.read_text())
            # A fresh BuildKit export uses exactly the Dockerfile source and compiler.
            # Keep the export and complete scanner output in the signed evidence index.
            go_work = work / 'go-reader'
            go_work.mkdir(mode=0o700)
            reader_artifacts, reader_evidence = go_reader.prepare(HERE.parents[1], go_work, commands.run)
            json_write(args.output / 'go-reader-provenance.json', go_reader.retained_provenance(reader_artifacts))
            for key, reference in sorted(verified['images'].items()):
                role = key[5:-6].lower()
                raw = args.output / f'{role}.raw.cdx.json'
                commands.run([syft, 'scan', 'registry:' + reference, '--platform', 'linux/amd64',
                              '--config', work / 'empty-config.json', '-o', 'cyclonedx-json=' + str(raw)], 'syft-' + role)
                supplements = []
                container = None
                try:
                    # Create does not execute the image entrypoint. Only this returned
                    # disposable container ID is ever removed.
                    commands.run(['docker', 'pull', '--platform', 'linux/amd64', reference], 'anonymous-pull-' + role)
                    container = commands.run(['docker', 'create', '--platform', 'linux/amd64', reference], 'metadata-container-' + role).strip()
                    if role in ('admin', 'consumer', 'player'):
                        frontend = work / f'{role}-frontend.cdx.json'
                        commands.extract(container, '/usr/share/romd/sbom/frontend.cdx.json', frontend)
                        supplements.append(json.loads(frontend.read_text()))
                    if role in ('admin', 'consumer', 'worker'):
                        for native in ('libchdr', 'chdman') if role == 'worker' else ('libchdr',):
                            receipt, binary = work / f'{role}-{native}.json', work / f'{role}-{native}.binary'
                            commands.extract(container, f'/app/native/{native}/build-manifest.json', receipt)
                            name = 'libmoonlark_chdr.so' if native == 'libchdr' else 'chdman'
                            commands.extract(container, f'/app/native/{native}/native/{name}', binary)
                            supplements.append(native_supplement(native, receipt, binary))
                        if role == 'worker':
                            inventory = work / 'chdman-source-inputs.json'
                            commands.extract(container, '/app/native/chdman/source-inputs.json', inventory)
                            report['chdmanSourceInventorySha256'] = sha256(inventory)
                    if role in ('admin', 'worker'):
                        receipt, binary = work / f'{role}-catalog-reader.json', work / f'{role}-catalog-reader'
                        commands.extract(container, '/usr/share/romd/sbom/catalog-reader.json', receipt)
                        commands.extract(container, '/app/catalog-reader', binary)
                        role_reader = scan_packaged_reader(binary, receipt, reader_artifacts, reader_evidence, go_work,
                                                          args.output, commands, role)
                        supplements.append(catalog_supplement(receipt, binary))
                finally:
                    if container:
                        commands.run(['docker', 'rm', container], 'cleanup-metadata-container-' + role)
                digest = reference.rsplit('@sha256:', 1)[1]
                sbom = merge_sbom(json.loads(raw.read_text()), supplements, reference, digest)
                coverage = assert_coverage(sbom, role)
                path = args.output / f'{role}.cdx.json'
                json_write(path, sbom)
                raw.unlink()
                evaluations, scans = [], []
                for mode, target in (('image', reference), ('sbom', path)):
                    scan = args.output / f'{role}.{mode}.trivy.json'
                    command = [trivy, mode, '--scanners', 'vuln', '--config', work / 'empty-config.json', '--ignorefile', work / 'empty-ignore',
                               '--format', 'json', '--output', scan, '--exit-code', '0', '--disable-telemetry', target]
                    if mode == 'image':
                        command[2:2] = ['--platform', 'linux/amd64']
                    commands.run(command, 'trivy-' + role + '-' + mode)
                    scan_result = json.loads(scan.read_text())
                    assert_scanner_coverage(scan_result, role, mode)
                    if role in ('admin', 'worker'):
                        go_reader.assert_inventory(scan_result, reader_evidence, selected=False)
                        evaluations.append(assess_go_reader(scan_result, 'sha256:' + digest, policy, role_reader, mode=mode, sbom=sbom))
                    else:
                        evaluations.append(evaluate(scan_result, 'sha256:' + digest, policy))
                    scans.append(scan.name)
                policy_path = args.output / f'{role}.policy.json'
                json_write(policy_path, evaluations)
                if any(result['result'] != 'passed' for result in evaluations):
                    raise ValueError('Release blocked by vulnerability severity/exception policy: ' + role)
                report['subjects'].append({'kind': 'image', 'role': role, 'name': reference, 'sha256': digest,
                                           'sbom': path.name, 'scans': scans, 'policy': policy_path.name, 'coverage': coverage})
            raw = work / 'bundle.cdx.json'
            commands.run([syft, 'scan', 'dir:' + str(bundle), '--config', work / 'empty-config.json',
                          '-o', 'cyclonedx-json=' + str(raw)], 'syft-bundle')
            images = [{'type': 'container', 'name': subject['name'], 'version': 'sha256:' + subject['sha256'],
                       'hashes': [{'alg': 'SHA-256', 'content': subject['sha256']}],
                       'purl': 'pkg:oci/' + quote(subject['role']) + '@sha256:' + subject['sha256']}
                      for subject in report['subjects']]
            bundle_sbom = merge_sbom(json.loads(raw.read_text()), [{'bomFormat': 'CycloneDX', 'components': images}],
                                     args.bundle.name, verified['sha256'])
            json_write(args.output / 'bundle.cdx.json', bundle_sbom)
            scan = args.output / 'bundle.trivy.json'
            commands.run([trivy, 'sbom', '--scanners', 'vuln', '--config', work / 'empty-config.json', '--ignorefile', work / 'empty-ignore',
                          '--format', 'json', '--output', scan, '--exit-code', '0', '--disable-telemetry',
                          args.output / 'bundle.cdx.json'], 'trivy-bundle')
            policy_result = evaluate(json.loads(scan.read_text()), 'sha256:' + verified['sha256'], policy)
            json_write(args.output / 'bundle.policy.json', policy_result)
            if policy_result['result'] != 'passed':
                raise ValueError('Release bundle blocked by vulnerability policy')
            report['subjects'].append({'kind': 'bundle', 'name': args.bundle.name, 'sha256': verified['sha256'],
                                       'sbom': 'bundle.cdx.json', 'scans': ['bundle.trivy.json'], 'policy': 'bundle.policy.json'})
            version = json.loads(commands.run([trivy, 'version', '--format', 'json'], 'scanner-database-version'))
            assert_fresh_database(version)
            json_write(args.output / 'trivy-version.json', version)
            report['result'] = 'passed'
    except (ValueError, RuntimeError, OSError, subprocess.SubprocessError, KeyError, TypeError) as error:
        report['failure'] = str(error) if type(error) in (ValueError, RuntimeError) else type(error).__name__
    report['files'] = {path.name: sha256(path) for path in sorted(args.output.iterdir()) if path.is_file()}
    json_write(args.output / 'release-metadata.json', report)
    return report['result'] == 'passed'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('bundle', 'checksum', 'images', 'publisher-state', 'tools-dir', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--revision', required=True)
    parser.add_argument('--tag', required=True)
    parser.add_argument('--policy', type=Path, default=HERE / 'policy.json')
    args = parser.parse_args()
    args.tools_dir = args.tools_dir.resolve()
    args.output = args.output.resolve()
    passed = generate(args)
    print('Release metadata: ' + ('passed' if passed else 'failed'))
    return 0 if passed else 1


if __name__ == '__main__':
    raise SystemExit(main())
