#!/usr/bin/env python3
"""Generate final-digest SBOMs, vulnerability results and a hash-bound release index.

Trivy applies the reviewed exceptions in `trivyignore.yaml` and, for the
catalog reader, govulncheck VEX, then gates each ROMD scan on its remaining
UNKNOWN, HIGH and CRITICAL findings. The official PostgreSQL image is reported,
not gated. ROMD-specific code here only adds image-carried inventory (frontend,
native and catalog reader components) and checks that each scan covered it.
"""
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
from urllib.parse import quote, unquote

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'release'))
from qualify import canonical_digest, image_map, rehearsal_images, verify_bundle, verify_publisher
from policy import GATE_EXIT_CODE, IGNORE_FILE, gate_command, load_exceptions, suppressed
from tools import verify_installed
from credentials import anonymous_environment
import go_vex


def sha256(path):
    with Path(path).open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def json_write(path, value):
    Path(path).write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def merge_sbom(sbom, supplements, name, digest):
    merged = copy.deepcopy(sbom)
    # CycloneDX permits omitting components; Syft does so for the package-free
    # deployment payload. Its reviewed image references still form an inventory.
    merged.setdefault('components', [])
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
    if not merged['components']:
        raise ValueError('Expected a nonempty merged CycloneDX component inventory')
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


def postgres_server(component):
    name, version, purl = (component.get(key) for key in ('name', 'version', 'purl'))
    if not isinstance(version, str) or not isinstance(purl, str):
        return False
    purl = unquote(purl).split('?', 1)[0]
    if name == 'postgresql':
        return bool(re.fullmatch(r'18\.[0-9]+', version)) and purl == 'pkg:generic/postgresql@' + version
    return (name == 'postgresql-18' and bool(re.fullmatch(r'18\.[0-9]+(?:-[0-9A-Za-z.+~]+)?', version))
            and purl == 'pkg:deb/debian/postgresql-18@' + version)


def assert_official_postgres(reference):
    if canonical_digest(reference).split('@', 1)[0] != 'docker.io/library/postgres':
        raise ValueError('Upstream-maintained reporting requires the Docker Official PostgreSQL image')


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
    if role == 'postgres':
        required['database-server'] = any(postgres_server(component) for component in components)
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
        if stage.startswith('govulncheck-') and result.stderr.strip():
            raise RuntimeError('govulncheck diagnostics require investigation')
        return result.stdout.decode()

    def gate(self, command, stage):
        """Run a Trivy severity gate; False means blocked findings, any other failure raises."""
        try:
            self.run(command, stage)
            return True
        except RuntimeError:
            if self.records[-1]['exitCode'] == GATE_EXIT_CODE:
                return False
            raise

    def extract(self, container, source, target):
        self.run(['docker', 'cp', container + ':' + source, target], 'extract-image-component-metadata')


def generate(args):
    if platform.system() != 'Linux' or platform.machine() not in ('x86_64', 'amd64'):
        raise ValueError('Release scanners require a native Linux amd64 runner')
    pins = verify_installed(args.tools_dir)
    syft, trivy = args.tools_dir / 'syft', args.tools_dir / 'trivy'
    args.output.mkdir(parents=True, exist_ok=False, mode=0o700)
    rehearsal = getattr(args, 'rehearsal', False)
    report = {'schemaVersion': 1, 'result': 'failed', 'sourceCommit': args.revision, 'releaseTag': args.tag,
              'evidenceType': 'disposable-rehearsal' if rehearsal else 'disposable-release',
              'tools': pins, 'subjects': [], 'commands': []}
    try:
        with tempfile.TemporaryDirectory(prefix='romd-release-metadata-') as temporary:
            work = Path(temporary)
            bundle = work / 'bundle'
            verify_publisher(args.bundle, args.publisher_state, args.images, args.revision, args.tag)
            if rehearsal:
                rehearsal_images(image_map(args.images))
            verified = verify_bundle(args.bundle, args.checksum, args.images, bundle, args.revision, args.tag)
            commands = Commands(work, args.output)
            report['commands'] = commands.records
            # Validate reviewed exceptions before any scan, and scan with this exact copy.
            ignore = work / 'trivyignore.yaml'
            ignore.write_bytes(args.ignore_file.read_bytes())
            load_exceptions(ignore)
            config, empty_ignore = work / 'empty-config.json', work / 'empty-ignore'
            # govulncheck comes from the Dockerfile's pinned, checksum-verified build.
            govulncheck = go_vex.export(HERE.parents[1], work, commands.run)['govulncheck']
            for key, reference in sorted(verified['images'].items()):
                role = key[5:-6].lower()
                raw = args.output / f'{role}.raw.cdx.json'
                # The rehearsal registry is deliberately plain loopback HTTP.
                # Scan its exact pulled Docker image without enabling insecure
                # registry transport or a fallback to another container engine.
                local = rehearsal and role != 'postgres'
                commands.run(['docker', 'pull', '--platform', 'linux/amd64', reference], 'anonymous-pull-' + role)
                commands.run([syft, 'scan', ('docker:' if local else 'registry:') + reference, '--platform', 'linux/amd64',
                              '--config', work / 'empty-config.json', '-o', 'cyclonedx-json=' + str(raw)], 'syft-' + role)
                supplements = []
                vex = None
                container = None
                try:
                    # Create does not execute the image entrypoint. Only this returned
                    # disposable container ID is ever removed.
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
                        supplements.append(catalog_supplement(receipt, binary))
                        vex = args.output / f'{role}.vex.json'
                        vulnerable = go_vex.scan(govulncheck, binary, vex, commands.run, 'govulncheck-' + role)
                        if vulnerable:
                            raise ValueError(f'Catalog reader in {role} contains vulnerable code: ' + ', '.join(vulnerable))
                finally:
                    if container:
                        commands.run(['docker', 'rm', container], 'cleanup-metadata-container-' + role)
                digest = reference.rsplit('@sha256:', 1)[1]
                sbom = merge_sbom(json.loads(raw.read_text()), supplements, reference, digest)
                coverage = assert_coverage(sbom, role)
                path = args.output / f'{role}.cdx.json'
                json_write(path, sbom)
                raw.unlink()
                if role == 'postgres':
                    assert_official_postgres(reference)
                records, scans, blocked = [], [], False
                for mode, target in (('image', reference), ('sbom', path)):
                    scan = args.output / f'{role}.{mode}.trivy.json'
                    # Upstream PostgreSQL findings are reported in full; ROMD exceptions never hide them.
                    command = [trivy, mode, '--scanners', 'vuln', '--list-all-pkgs', '--show-suppressed', '--config', config,
                               '--ignorefile', empty_ignore if role == 'postgres' else ignore,
                               '--format', 'json', '--output', scan, '--exit-code', '0', '--disable-telemetry']
                    if vex:
                        command += ['--vex', vex]
                    if mode == 'image':
                        command[2:2] = ['--platform', 'linux/amd64']
                        if local:
                            command[2:2] = ['--image-src', 'docker']
                    commands.run(command + [target], 'trivy-' + role + '-' + mode)
                    scan_result = json.loads(scan.read_text())
                    assert_scanner_coverage(scan_result, role, mode)
                    if vex and mode == 'image':
                        go_vex.assert_only_reader(scan_result)
                    if role == 'postgres':
                        record = {'result': 'reported', 'enforcement': 'upstream-managed'}
                    else:
                        passed = commands.gate(gate_command(trivy, scan, config), f'vulnerability-gate-{role}-{mode}')
                        blocked = blocked or not passed
                        record = {'result': 'passed' if passed else 'failed', 'enforcement': 'blocking'}
                    records.append(dict(record, scan=scan.name, suppressed=suppressed(scan_result)))
                    scans.append(scan.name)
                policy_path = args.output / f'{role}.policy.json'
                json_write(policy_path, records)
                if blocked:
                    raise ValueError('Release blocked by vulnerability severity policy: ' + role)
                report['subjects'].append({'kind': 'image', 'role': role, 'name': reference, 'sha256': digest,
                                           'sbom': path.name, 'scans': scans, 'policy': policy_path.name, 'coverage': coverage,
                                           'enforcement': 'upstream-managed' if role == 'postgres' else 'blocking',
                                           **({'vex': vex.name} if vex else {})})
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
            commands.run([trivy, 'sbom', '--scanners', 'vuln', '--show-suppressed', '--config', config, '--ignorefile', ignore,
                          '--format', 'json', '--output', scan, '--exit-code', '0', '--disable-telemetry',
                          args.output / 'bundle.cdx.json'], 'trivy-bundle')
            passed = commands.gate(gate_command(trivy, scan, config), 'vulnerability-gate-bundle')
            json_write(args.output / 'bundle.policy.json', [{'result': 'passed' if passed else 'failed', 'enforcement': 'blocking',
                       'scan': scan.name, 'suppressed': suppressed(json.loads(scan.read_text()))}])
            if not passed:
                raise ValueError('Release bundle blocked by vulnerability severity policy')
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
    parser.add_argument('--ignore-file', type=Path, default=IGNORE_FILE, help='Reviewed Trivy vulnerability exceptions')
    parser.add_argument('--rehearsal', action='store_true', help='Disposable local images; never public qualification evidence')
    args = parser.parse_args()
    args.tools_dir = args.tools_dir.resolve()
    args.output = args.output.resolve()
    passed = generate(args)
    print('Release metadata: ' + ('passed' if passed else 'failed'))
    return 0 if passed else 1


if __name__ == '__main__':
    raise SystemExit(main())
