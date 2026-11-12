#!/usr/bin/env python3
"""Strict, hash-bound Go package applicability; raw module warnings are never removed."""
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import re
from urllib.parse import unquote

from policy import evaluate

MODULE = 'github.com/MoonlarkStudios/romd-dat-catalogs'
ENTRYPOINT = MODULE + '/cmd/distribution'
DB = 'https://vuln.go.dev'


def require(condition, message):
    if not condition:
        raise ValueError('Go reader evidence: ' + message)


def digest(path):
    with Path(path).open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def stream(raw):
    """Decode the entire JSON stream, rejecting duplicate keys and partial trailing messages."""
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, 'duplicate JSON key')
            result[key] = value
        return result
    def invalid_constant(_value):
        raise ValueError('Go reader evidence: nonfinite JSON value')
    decoder = json.JSONDecoder(object_pairs_hook=pairs, parse_constant=invalid_constant)
    result, offset = [], 0
    while offset < len(raw):
        while offset < len(raw) and raw[offset].isspace():
            offset += 1
        if offset == len(raw):
            break
        value, offset = decoder.raw_decode(raw, offset)
        require(isinstance(value, dict), 'JSON message is not an object')
        result.append(value)
    require(result, 'empty JSON stream')
    return result


def timestamp(value):
    require(isinstance(value, str), 'missing timestamp')
    result = datetime.fromisoformat(value.replace('Z', '+00:00'))
    require(result.tzinfo is not None, 'timestamp lacks timezone')
    return result


def semver(value):
    require(isinstance(value, str), 'missing Go module version')
    match = re.fullmatch(r'v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?', value)
    require(match is not None, 'unsupported Go module version')
    prerelease = tuple((0, int(part)) if part.isdigit() else (1, part) for part in match[4].split('.')) if match[4] else ((2, ''),)
    return tuple(int(match[i]) for i in (1, 2, 3)), prerelease


def version_key(module, version):
    # Go/Syft/Trivy spell the same compiler go1.x.y, 1.x.y or v1.x.y.
    # Ordinary module versions still require exact source-version correspondence.
    if module == 'stdlib' and isinstance(version, str) and re.fullmatch(r'(?:go|v)?[0-9]+\.[0-9]+\.[0-9]+', version):
        return version.removeprefix('go').removeprefix('v')
    return version


def affected_version(affected, version):
    ranges = affected.get('ranges')
    require(isinstance(ranges, list) and ranges, 'OSV affected ranges missing')
    included = False
    for item in ranges:
        require(isinstance(item, dict) and item.get('type') == 'SEMVER', 'unsupported OSV range')
        events = item.get('events')
        require(isinstance(events, list) and events, 'OSV range events missing')
        start = None
        for event in events:
            require(isinstance(event, dict) and len(event) == 1 and set(event) <= {'introduced', 'fixed'}, 'unsupported OSV event')
            if 'introduced' in event:
                require(start is None, 'overlapping OSV range events')
                start = semver('0.0.0' if event['introduced'] == '0' else event['introduced'])
            else:
                require(start is not None, 'OSV fixed event lacks introduced version')
                end = semver(event['fixed'])
                require(start < end, 'invalid OSV range order')
                included |= start <= semver(version) < end
                start = None
        if start is not None:
            included |= semver(version) >= start
    return included


def parse_go_mod(text):
    """The selected dependency graph is go.mod's require directives, never go.sum's history."""
    module, minimum, requirements, block = None, None, {}, False
    for raw in text.splitlines():
        line = raw.split('//', 1)[0].strip()
        if not line:
            continue
        if line == 'require (':
            require(not block, 'nested go.mod require block')
            block = True
            continue
        if line == ')':
            require(block, 'unexpected go.mod block end')
            block = False
            continue
        fields = line.split()
        if block or fields[0] == 'require':
            fields = fields if block else fields[1:]
            require(len(fields) == 2 and re.fullmatch(r'[A-Za-z0-9._~+/-]+', fields[0]), 'unsupported go.mod dependency')
            name, version = fields
            semver(version)
            require(name not in requirements, 'duplicate go.mod dependency')
            requirements[name] = version
        elif fields[0] == 'module':
            require(module is None and len(fields) == 2, 'invalid go.mod module')
            module = fields[1]
        elif fields[0] == 'go':
            require(minimum is None and len(fields) == 2, 'invalid go.mod minimum')
            minimum = fields[1]
        elif fields[0] == 'toolchain':
            require(len(fields) == 2 and re.fullmatch(r'go[0-9]+\.[0-9]+\.[0-9]+', fields[1]), 'invalid toolchain directive')
        else:
            raise ValueError('Go reader evidence: unsupported go.mod directive (replacements/exclusions need explicit coverage)')
    require(not block and module == MODULE and minimum and requirements, 'incomplete go.mod inventory')
    return minimum, requirements


def validate_artifacts(directory, pins):
    directory = Path(directory)
    receipt = json.loads((directory / 'qualification.json').read_text())
    require(receipt.get('schemaVersion') == 1 and receipt.get('source') == pins, 'source/compiler provenance mismatch')
    files = receipt.get('files')
    expected = {'catalog-reader', 'catalog-reader.json', '1.root.json', 'go.mod', 'go.sum', 'packages.json', 'buildinfo.json',
                'govulncheck', 'govulncheck-buildinfo.json', 'govulncheck-module.json', 'govulncheck-source.zip', 'reader.tar.gz'}
    require(isinstance(files, dict) and set(files) == expected, 'incomplete qualification artifact inventory')
    require(all(re.fullmatch(r'[a-f0-9]{64}', value) and digest(directory / name) == value for name, value in files.items()),
            'qualification artifact hash mismatch')
    require(files['1.root.json'] == pins['trustRootSha256'] and files['reader.tar.gz'] == pins['sourceArchiveSha256']
            and files['govulncheck-source.zip'] == pins['govulncheck']['sourceZipSha256'], 'archive/trust root differs from verified pin')
    build = json.loads((directory / 'buildinfo.json').read_text())
    require(build.get('GoVersion') == pins['goVersion'] and build.get('Path') == ENTRYPOINT
            and build.get('Main', {}).get('Path') == MODULE and build['Main'].get('Version') == '(devel)', 'reader build information mismatch')
    settings = {item['Key']: item['Value'] for item in build.get('Settings', [])}
    require(len(settings) == len(build.get('Settings', [])), 'duplicate binary build setting')
    require(all(settings.get(key) == value for key, value in
                {'GOOS': 'linux', 'GOARCH': 'amd64', 'CGO_ENABLED': '0', '-trimpath': 'true', '-buildmode': 'exe'}.items()),
            'reader platform/build settings mismatch')
    require(not any(key.startswith('vcs.') for key in settings) and '-ldflags' not in settings, 'unexpected VCS/stripped-symbol build')
    runtime_receipt = json.loads((directory / 'catalog-reader.json').read_text())
    require(runtime_receipt.get('schemaVersion') == 1 and runtime_receipt.get('module') == MODULE
            and runtime_receipt.get('sourceCommit') == pins['sourceCommit'] and runtime_receipt.get('goVersion') == pins['goVersion']
            and runtime_receipt.get('binarySha256') == files['catalog-reader'], 'runtime reader receipt mismatch')
    minimum, required = parse_go_mod((directory / 'go.mod').read_text())
    deps = build.get('Deps')
    require(isinstance(deps, list) and deps, 'binary linked module inventory missing')
    linked = {}
    for item in deps:
        require(isinstance(item, dict) and not item.get('Replace') and item.get('Path') not in linked, 'unsupported/duplicate linked module')
        name, version = item.get('Path'), item.get('Version')
        require(required.get(name) == version, 'linked module differs from exact source go.mod')
        linked[name] = version
    packages = stream((directory / 'packages.json').read_text())
    imports = {}
    graph_modules = {}
    for package in packages:
        name = package.get('ImportPath')
        require(isinstance(name, str) and name and name not in imports and not package.get('Incomplete')
                and not package.get('Error') and not package.get('DepsErrors'), 'incomplete/duplicate package graph')
        imports[name] = package
        info = package.get('Module')
        if info:
            require(isinstance(info, dict) and not info.get('Replace'), 'package graph module replacement')
            if info.get('Path') != MODULE:
                require(linked.get(info.get('Path')) == info.get('Version'), 'package graph module mismatch')
                graph_modules[info['Path']] = info['Version']
        else:
            require(package.get('Standard') is True, 'package graph lacks module/stdlib ownership')
    require(ENTRYPOINT in imports and set(imports[ENTRYPOINT].get('Deps', [])) == set(imports) - {ENTRYPOINT},
            'package graph root/dependency coverage incomplete')
    require(all(set(package.get('Imports', [])) <= set(imports) for package in packages), 'package import graph is truncated')
    require(graph_modules == linked and any(item.get('Standard') for item in packages), 'package graph module/stdlib coverage incomplete')
    tool = pins['govulncheck']
    module = json.loads((directory / 'govulncheck-module.json').read_text())
    require(module.get('Path') == 'golang.org/x/vuln' and module.get('Version') == tool['version']
            and module.get('Sum') == tool['goSum'] and module.get('GoModSum') == tool['goModSum'], 'govulncheck module checksum mismatch')
    tool_build = json.loads((directory / 'govulncheck-buildinfo.json').read_text())
    require(tool_build.get('GoVersion') == pins['goVersion'] and tool_build.get('Path') == 'golang.org/x/vuln/cmd/govulncheck'
            and tool_build.get('Main', {}).get('Version') == tool['version'] and tool_build['Main'].get('Sum') == tool['goSum'],
            'govulncheck executable build provenance mismatch')
    return dict(pins=pins, hashes=files, required=required, linked=linked, minimumGo=minimum, imports=set(imports), compiler=pins['goVersion'])


def validate_scan(raw, evidence, index, checked_at, now=None):
    now = now or datetime.now(timezone.utc)
    require(-timedelta(hours=1) <= now - timestamp(checked_at) <= timedelta(hours=24), 'Go database query evidence is stale/future')
    events = stream(raw)
    require(set(events[0]) == {'config'}, 'govulncheck config must be first')
    configurations, sboms, osvs, findings = [], [], {}, []
    for event in events:
        require(len(event) == 1 and set(event) <= {'config', 'progress', 'SBOM', 'osv', 'finding'}, 'unknown govulncheck JSON message')
        if 'config' in event:
            configurations.append(event['config'])
        elif 'SBOM' in event:
            sboms.append(event['SBOM'])
        elif 'osv' in event:
            osv = event['osv']
            require(isinstance(osv, dict) and isinstance(osv.get('id'), str), 'missing primary OSV')
            # The official scanner emits the same primary record once per module
            # for advisories affecting several modules. Conflicting copies are ambiguous.
            require(osv['id'] not in osvs or osvs[osv['id']] == osv, 'conflicting primary OSV records')
            osvs[osv['id']] = osv
        elif 'finding' in event:
            finding = event['finding']
            require(isinstance(finding, dict) and isinstance(finding.get('osv'), str)
                    and isinstance(finding.get('trace'), list) and len(finding['trace']) == 1, 'invalid package-precision finding')
            frame = finding['trace'][0]
            require(isinstance(frame, dict) and isinstance(frame.get('module'), str) and isinstance(frame.get('version'), str)
                    and not frame.get('function') and ('package' not in frame or isinstance(frame['package'], str) and frame['package']),
                    'unsupported finding precision')
            findings.append(finding)
        else:
            require(isinstance(event['progress'], dict) and isinstance(event['progress'].get('message'), str), 'invalid progress message')
    require(len(configurations) == len(sboms) == 1, 'missing/duplicate config or SBOM coverage')
    config, sbom = configurations[0], sboms[0]
    require(isinstance(config, dict) and config.get('protocol_version') == 'v1.0.0'
            and config.get('scanner_name') == 'govulncheck' and config.get('scanner_version') == evidence['pins']['govulncheck']['version']
            and config.get('db') == DB and config.get('scan_level') == 'package' and config.get('scan_mode') == 'binary',
            'govulncheck configuration/version mismatch')
    # This timestamp is publication time, not fetch freshness: an unchanged official DB can be older than 72h.
    modified = timestamp(config.get('db_last_modified'))
    require(isinstance(index, dict) and timestamp(index.get('modified')) == modified
            and modified <= now + timedelta(hours=1), 'official Go database index mismatch/future date')
    expected = {**evidence['linked'], MODULE: '(devel)', 'stdlib': evidence['compiler']}
    require(isinstance(sbom, dict) and sbom.get('go_version') == evidence['compiler'] and sbom.get('roots') == [MODULE], 'binary SBOM compiler/root mismatch')
    modules = sbom.get('modules')
    require(isinstance(modules, list) and modules and all(isinstance(item, dict) for item in modules), 'binary SBOM modules missing')
    observed = {item.get('path'): item.get('version') for item in modules}
    require(len(observed) == len(modules) and observed == expected, 'binary SBOM module coverage/version mismatch')
    for finding in findings:
        frame = finding['trace'][0]
        module = frame['module']
        require(finding['osv'] in osvs, 'finding lacks primary OSV coverage')
        require(module in expected and version_key(module, expected[module]) == version_key(module, frame['version']),
                'finding module/version differs from binary SBOM')
    return {**evidence, 'osvs': osvs, 'findings': findings, 'dbCheckedAt': checked_at,
            'dbLastModified': config['db_last_modified'], 'scanSha256': hashlib.sha256(raw.encode()).hexdigest()}


def absent_packages(identifier, module, version, evidence):
    matches = [osv for osv in evidence['osvs'].values() if identifier == osv['id'] or identifier in osv.get('aliases', [])]
    require(len(matches) == 1, 'unknown or ambiguous primary OSV/alias')
    osv = matches[0]
    require(osv.get('schema_version') == '1.3.1' and re.fullmatch(r'GO-[0-9]{4}-[0-9]+', osv['id'])
            and osv.get('database_specific', {}).get('review_status') == 'REVIEWED' and not osv.get('withdrawn'), 'unsupported/unreviewed OSV')
    require(timestamp(osv.get('modified')) <= timestamp(evidence['dbCheckedAt']) + timedelta(hours=1), 'OSV modified after evidence')
    aliases = osv.get('aliases', [])
    require(isinstance(aliases, list) and all(isinstance(alias, str) for alias in aliases), 'invalid OSV aliases')
    relevant = [item for item in osv.get('affected', []) if item.get('package', {}).get('ecosystem') == 'Go'
                and item['package'].get('name') == module and affected_version(item, version)]
    require(relevant, 'OSV does not affect the exact module/version')
    paths = set()
    for item in relevant:
        imports = item.get('ecosystem_specific', {}).get('imports')
        require(isinstance(imports, list) and imports, 'OSV affected import coverage missing')
        for imported in imports:
            path = imported.get('path')
            require(isinstance(path, str) and path and (module == 'stdlib' or path == module or path.startswith(module + '/')),
                    'OSV import outside affected module')
            # Include all affected imports, including other platform variants. Absence is the only resolution.
            paths.add(path)
    matching = [item for item in evidence['findings'] if item['osv'] == osv['id']
                and item['trace'][0]['module'] == module and item['trace'][0]['version'].removeprefix('go').removeprefix('v') == version.removeprefix('v')]
    require(matching, 'missing module-level govulncheck finding')
    if any(item['trace'][0].get('package') for item in matching):
        return None, 'govulncheck reports an affected package (including conservative symbol fallback)'
    present = sorted(paths & evidence['imports'])
    if present:
        return None, 'affected imports are present: ' + ', '.join(present)
    return dict(primaryOsv=osv['id'], affectedImports=sorted(paths), presentAffectedImports=[],
                binarySha256=evidence['hashes']['catalog-reader'], sourceCommit=evidence['pins']['sourceCommit'],
                sourceArchiveSha256=evidence['pins']['sourceArchiveSha256'], compilerImage=evidence['pins']['compilerImage'],
                platform='linux/amd64', govulncheckSha256=evidence['hashes']['govulncheck'],
                scanSha256=evidence['scanSha256'], dbCheckedAt=evidence['dbCheckedAt']), None


def sbom_bound(sbom, module, version, evidence):
    components = sbom.get('components', [])
    prefix = f'pkg:golang/{module}@'
    matches = []
    for component in components:
        purl = unquote(component.get('purl', '')).split('?', 1)[0]
        if (component.get('name') == module and version_key(module, component.get('version')) == version_key(module, version)
                and purl.startswith(prefix) and version_key(module, purl[len(prefix):]) == version_key(module, version)):
            matches.append(component)
    if not matches:
        return False
    for component in matches:
        properties = component.get('properties', [])
        locations = {item['value'] for item in properties if re.fullmatch(r'syft:location:[0-9]+:path', item.get('name', ''))}
        locations |= {item.get('location') for item in component.get('evidence', {}).get('occurrences', [])}
        if locations != {'/app/catalog-reader'}:
            return False
    return True


def assess(report, subject_digest, policy, evidence, *, mode, sbom=None, release_tag=None):
    """Keep each original warning/severity; attach engineering applicability separately from risk exceptions."""
    require(mode in ('image', 'sbom'), 'unsupported assessment mode')
    result = evaluate(report, subject_digest, policy, release_tag=release_tag)
    raw = [(group, vulnerability) for group in report['Results'] for vulnerability in group.get('Vulnerabilities') or []]
    for finding, (group, vulnerability) in zip(result['findings'], raw):
        if finding['applicability']['status'] == 'not_affected' or finding.get('riskAcceptance'):
            continue
        finding['applicability'] = {'status': 'unresolved'}
        if group.get('Class') != 'lang-pkgs' or group.get('Type') not in ('gobinary', 'gomod'):
            continue
        module, version = vulnerability['PkgName'], vulnerability['InstalledVersion']
        expected = evidence['compiler'].replace('go', 'v', 1) if module == 'stdlib' else evidence['required'].get(module)
        target = group.get('Target', '')
        bound = target in ('app/catalog-reader', '/app/catalog-reader') if mode == 'image' else sbom_bound(sbom or {}, module, version, evidence)
        if version_key(module, expected) != version_key(module, version) or not bound:
            finding['applicability']['reason'] = 'component/version is not bound exclusively to the assessed reader'
            continue
        try:
            proof, reason = absent_packages(vulnerability['VulnerabilityID'], module, version, evidence)
            if proof:
                finding['applicability'] = {'status': 'affected-packages-absent', 'evidence': proof}
                finding['blocked'] = False
            else:
                finding['applicability']['reason'] = reason
        except (ValueError, KeyError, TypeError, AttributeError) as error:
            finding['applicability']['reason'] = str(error) if type(error) is ValueError else 'invalid/incomplete OSV applicability evidence'
    go_advisories = []
    identities = {(item['osv'], item['trace'][0]['module'], item['trace'][0]['version']) for item in evidence['findings']}
    for identifier, module, version in sorted(identities):
        version = version.replace('go', 'v', 1) if module == 'stdlib' else version
        advisory = dict(id=identifier, module=module, installedVersion=version, severity='UNKNOWN', rawBlocked=True, blocked=True,
                        subjectDigest=subject_digest, applicability={'status': 'unresolved'})
        try:
            proof, reason = absent_packages(identifier, module, version, evidence)
            if proof:
                advisory['blocked'] = False
                advisory['applicability'] = {'status': 'affected-packages-absent', 'evidence': proof}
            else:
                advisory['applicability']['reason'] = reason
        except (ValueError, KeyError, TypeError, AttributeError):
            advisory['applicability']['reason'] = 'invalid/incomplete OSV applicability evidence'
        go_advisories.append(advisory)
    result['goAdvisories'] = go_advisories
    result['result'] = 'failed' if any(item['blocked'] for item in result['findings'] + go_advisories) else 'passed'
    return result
