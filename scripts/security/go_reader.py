#!/usr/bin/env python3
"""Fresh Docker BuildKit reader/tool qualification shared by daily and final-image security gates."""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import platform
import re
import tarfile
import urllib.request

from go_applicability import DB, MODULE, digest, require, validate_artifacts, validate_scan, version_key


ARTIFACTS = ('catalog-reader', 'catalog-reader.json', '1.root.json', 'reader.tar.gz', 'go.mod', 'go.sum', 'packages.json',
             'buildinfo.json', 'govulncheck', 'govulncheck-buildinfo.json', 'govulncheck-module.json', 'govulncheck-source.zip')


def pins(root):
    text = (root / 'Dockerfile').read_text()
    compiler = re.findall(r'^FROM --platform=\$BUILDPLATFORM (golang:([0-9.]+)-bookworm@sha256:[a-f0-9]{64}) AS catalog-reader-build$', text, re.M)
    archive = re.findall(r'^ADD --checksum=sha256:([a-f0-9]{64}) (https://codeload.github.com/MoonlarkStudios/romd-dat-catalogs/tar.gz/([a-f0-9]{40})) /tmp/reader.tar.gz$', text, re.M)
    trust = re.findall(r"echo '([a-f0-9]{64})  trust/1.root.json'", text)
    require(len(compiler) == len(archive) == len(trust) == 1, 'ambiguous/missing Dockerfile source/compiler pins')
    require('ENV GOTOOLCHAIN=local' in text and f'test "$(go env GOVERSION)" = go{compiler[0][1]}' in text,
            'compiler assertion or local toolchain policy missing')
    tool = json.loads((root / 'scripts/security/tools.json').read_text())['govulncheck']
    require(tool.get('module') == 'golang.org/x/vuln' and re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+', tool['version']), 'invalid scanner module pin')
    require(all(value in text for value in (tool['version'], tool['goSum'], tool['goModSum'], tool['sourceZipSha256'])),
            'Dockerfile govulncheck pins differ from tool manifest')
    return dict(sourceCommit=archive[0][2], sourceArchiveSha256=archive[0][0], sourceUrl=archive[0][1],
                compilerImage=compiler[0][0], goVersion='go' + compiler[0][1], trustRootSha256=trust[0], govulncheck=tool)


def fetch(url):
    require(url == DB + '/index/db.json' or re.fullmatch(re.escape(DB) + r'/ID/GO-[0-9]{4}-[0-9]+\.json', url), 'unsupported official Go DB URL')
    request = urllib.request.Request(url, headers={'Cache-Control': 'no-cache', 'Accept': 'application/json'})
    with urllib.request.urlopen(request, timeout=60) as response:
        require(response.status == 200 and response.geturl() == url, 'unexpected official Go database response')
        raw = response.read(5 * 1024 * 1024 + 1)
    require(len(raw) <= 5 * 1024 * 1024, 'official Go database record exceeds limit')
    value = json.loads(raw)
    require(isinstance(value, dict), 'official Go database response is not an object')
    return value


def prepare(root, work, run):
    """Export a fresh target, never reuse a binary or receipt from a previous/local build."""
    require(platform.system() == 'Linux' and platform.machine().lower() in ('x86_64', 'amd64'), 'qualification requires native Linux amd64')
    require(os.environ.get('ROMD_CONTAINER_ENGINE', 'docker') == 'docker', 'Docker BuildKit/buildx is required; Podman export is not qualified (no skip)')
    source = pins(root)
    artifact = work / 'reader-artifacts'
    require(not artifact.exists(), 'fresh qualification output already exists')
    run(['docker', 'buildx', 'version'], 'go-reader-buildkit-prerequisite')
    builder = ['--builder', os.environ['ROMD_GO_READER_BUILDER']] if os.environ.get('ROMD_GO_READER_BUILDER') else []
    run(['docker', 'buildx', 'build', *builder, '--platform', 'linux/amd64', '--target', 'catalog-reader-security-artifacts',
         '--output', 'type=local,dest=' + str(artifact), '--file', str(root / 'Dockerfile'), root], 'go-reader-fresh-artifacts')
    require(all((artifact / name).is_file() and not (artifact / name).is_symlink() for name in ARTIFACTS), 'qualification export incomplete')
    hashes = {name: digest(artifact / name) for name in ARTIFACTS}
    require(hashes['reader.tar.gz'] == source['sourceArchiveSha256'], 'source archive checksum mismatch')
    # Independently bind exported go.mod/go.sum to the checksum-verified source archive, not a local checkout.
    with tarfile.open(artifact / 'reader.tar.gz', 'r:gz') as archive:
        for name in ('go.mod', 'go.sum'):
            path = 'romd-dat-catalogs-' + source['sourceCommit'] + '/' + name
            item = archive.getmember(path)
            require(item.isfile() and item.size <= 1024 * 1024 and archive.extractfile(item).read() == (artifact / name).read_bytes(),
                    'exported dependency manifest differs from verified source archive')
    receipt = dict(schemaVersion=1, source=source, files=hashes)
    (artifact / 'qualification.json').write_text(json.dumps(receipt, indent=2) + '\n')
    evidence = validate_artifacts(artifact, source)
    return artifact, evidence


def retained_provenance(artifact):
    """Flat public JSON envelope; binaries/archives are identified by their verified hashes."""
    names = ('qualification.json', 'catalog-reader.json', '1.root.json', 'go.mod', 'go.sum', 'packages.json',
             'buildinfo.json', 'govulncheck-buildinfo.json', 'govulncheck-module.json')
    return {name: (artifact / name).read_text() for name in names}


def retained_scan(output):
    # Preserve exact original stream bytes as a string, including unknown/raw OSV details.
    return {path.name: path.read_text() for path in sorted(output.iterdir()) if path.is_file()}


def scan(artifact, evidence, output, run, *, binary=None):
    """One complete binary/package scan, current official DB query, and re-fetched primary records."""
    output.mkdir(parents=True, exist_ok=True)
    binary = binary or artifact / 'catalog-reader'
    require(digest(binary) == evidence['hashes']['catalog-reader'], 'scanner input is not the qualified reader')
    index = fetch(DB + '/index/db.json')
    checked = datetime.now(timezone.utc).isoformat()
    raw = run([artifact / 'govulncheck', '-db=' + DB, '-scan=package', '-format=json', '-mode=binary', binary],
              'go-reader-govulncheck-binary-package')
    (output / 'govulncheck.jsonstream').write_text(raw)
    (output / 'go-database.json').write_text(json.dumps({'checkedAt': checked, 'url': DB, 'index': index}, indent=2) + '\n')
    validated = validate_scan(raw, evidence, index, checked)
    # A matching raw OSV event is necessary, but current independent primary-record agreement is also required.
    for finding in validated['findings']:
        identifier = finding['osv']
        fresh = fetch(DB + '/ID/' + identifier + '.json')
        require(fresh == validated['osvs'][identifier], 'fresh primary OSV differs from scanner evidence')
        (output / (identifier + '.osv.json')).write_text(json.dumps(fresh, indent=2) + '\n')
    return validated


def dependency_sbom(evidence):
    versions = {**evidence['required'], 'stdlib': evidence['compiler'].replace('go', 'v', 1)}
    return {'bomFormat': 'CycloneDX', 'specVersion': '1.6', 'version': 1, 'components': [
        {'type': 'library', 'name': name, 'version': version, 'purl': f'pkg:golang/{name}@{version}',
         'properties': [{'name': 'syft:location:0:path', 'value': '/app/catalog-reader'}]}
        for name, version in sorted(versions.items())]}


def assert_inventory(report, evidence, *, selected=True):
    require(report.get('SchemaVersion') == 2 and isinstance(report.get('Results'), list), 'unsupported Trivy Go report')
    results = [item for item in report['Results'] if item.get('Type') in ('gomod', 'gobinary') and item.get('Class') == 'lang-pkgs']
    require(results, 'Trivy Go analyzer coverage missing')
    covered = {(package.get('Name'), version_key(package.get('Name'), package.get('Version')))
               for result in results for package in result.get('Packages') or []}
    expected = set(evidence['required' if selected else 'linked'].items()) | {('stdlib', version_key('stdlib', evidence['compiler']))}
    require(expected <= covered, 'Trivy Go module/stdlib inventory incomplete')
    return dict(selectedGoModModules=len(evidence['required']), binaryLinkedModules=len(evidence['linked']),
                compiler=evidence['compiler'], goDirectiveIsMinimum=evidence['minimumGo'], platform='linux/amd64',
                sourceCommit=evidence['pins']['sourceCommit'], sourceArchiveSha256=evidence['pins']['sourceArchiveSha256'],
                compilerImage=evidence['pins']['compilerImage'], readerSha256=evidence['hashes']['catalog-reader'],
                importedPackages=len(evidence['imports']), standardLibraryInventoried=True)


def assert_final_binding(binary, receipt, evidence):
    require(digest(binary) == evidence['hashes']['catalog-reader'], 'final-image reader differs from freshly qualified binary')
    value = json.loads(receipt.read_text())
    require(value.get('binarySha256') == digest(binary) and value.get('sourceCommit') == evidence['pins']['sourceCommit']
            and value.get('goVersion') == evidence['compiler'] and value.get('module') == MODULE, 'final-image reader receipt mismatch')
