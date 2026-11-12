#!/usr/bin/env python3
"""Audit resolved source dependencies, retaining normalized results and Go proof.

Arbitrary diagnostics, environment, registry credentials and absolute checkout
paths are excluded from normalized results. Public-source Go JSON/provenance is
retained separately. Errors fail closed; final release digest scanning remains
a separate gate.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import tomllib
from urllib.parse import urlsplit

from source_tools import ensure_trivy
import go_reader
from go_applicability import assess

ROOT = Path(__file__).resolve().parents[2]
SEVERITIES = {'LOW', 'MEDIUM', 'HIGH', 'CRITICAL', 'UNKNOWN'}


class ScanError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ScanError(message)


def token(value):
    require(isinstance(value, str) and 0 < len(value) <= 300
            and re.fullmatch(r'[@A-Za-z0-9_.+:/~^<>=,*()| -]+', value), 'Invalid advisory field')
    return value


def severity(value):
    require(isinstance(value, str), 'Missing severity')
    result = {'MODERATE': 'MEDIUM'}.get(value.upper(), value.upper())
    require(result in SEVERITIES, 'Unrecognized advisory severity')
    return result


def advisory_url(value):
    require(isinstance(value, str) and all(32 < ord(c) < 127 for c in value), 'Invalid advisory URL')
    parsed = urlsplit(value)
    require(parsed.scheme == 'https' and parsed.hostname in
            ('github.com', 'www.npmjs.com', 'npmjs.com', 'nvd.nist.gov', 'osv.dev')
            and not parsed.username and not parsed.password and not parsed.query and not parsed.fragment
            and parsed.netloc == parsed.hostname and re.fullmatch(r'/[A-Za-z0-9_./-]+', parsed.path),
            'Unsupported advisory URL')
    return value


def finding(ecosystem, scope, package, version, advisory, rating):
    return dict(ecosystem=ecosystem, scope=token(scope), package=token(package),
                version=token(version), advisory=advisory_url(advisory), severity=severity(rating))


def run(command, cwd, *, accepted=(0,), environment=None):
    try:
        result = subprocess.run([str(t) for t in command], cwd=cwd, capture_output=True,
                                text=True, errors='replace', timeout=1200, env=environment)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ScanError('Scanner could not complete') from error
    # Do not echo arbitrary registry/scanner diagnostics: they can contain credentials.
    require(result.returncode in accepted, 'Scanner failed')
    return result


def decode(result):
    require(not result.stderr.strip(), 'Scanner diagnostics require investigation')
    try:
        data = json.loads(result.stdout)
    except (TypeError, json.JSONDecodeError) as error:
        raise ScanError('Scanner did not return JSON') from error
    require(isinstance(data, dict), 'Scanner report must be an object')
    return data


def relative_project(value, root):
    require(isinstance(value, str), 'Missing NuGet project path')
    try:
        path = Path(value)
        return (path if path.is_absolute() else root / path).resolve().relative_to(root.resolve()).as_posix()
    except ValueError as error:
        raise ScanError('NuGet project outside checkout') from error


def nuget_projects(data, root, expected):
    require(data.get('version') == 1 and not data.get('problems'), 'Invalid or incomplete NuGet report')
    projects = data.get('projects')
    require(isinstance(projects, list) and projects, 'Missing NuGet project coverage')
    result = {}
    for project in projects:
        require(isinstance(project, dict) and not project.get('problems'), 'NuGet project error')
        path = relative_project(project.get('path'), root)
        require(path not in result, 'Duplicate NuGet project')
        result[path] = project
    require(set(result) == set(expected), 'NuGet project coverage mismatch')
    return result


def parse_nuget(inventory, audit, root, expected):
    projects = nuget_projects(inventory, root, expected)
    audited = nuget_projects(audit, root, expected)
    require('--include-transitive' in inventory.get('parameters', ''), 'NuGet inventory omits transitives')
    require(all(flag in audit.get('parameters', '') for flag in ('--vulnerable', '--include-transitive')),
            'NuGet audit options missing')
    require(audit.get('sources'), 'NuGet advisory sources missing')
    all_packages, findings = set(), []
    for path, project in projects.items():
        frameworks = project.get('frameworks')
        require(isinstance(frameworks, list) and frameworks, 'NuGet inventory lacks framework coverage')
        resolved = set()
        framework_names = set()
        for framework in frameworks:
            name = token(framework.get('framework'))
            require(name not in framework_names, 'Duplicate NuGet framework')
            framework_names.add(name)
            for key in ('topLevelPackages', 'transitivePackages'):
                packages = framework.get(key, [])
                require(isinstance(packages, list), 'Invalid NuGet package collection')
                for package in packages:
                    item = (name, token(package.get('id')), token(package.get('resolvedVersion')))
                    resolved.add(item)
                    all_packages.add(item[1:])
        # NuGet omits clean frameworks/package lists from --vulnerable output.
        # A separate full inventory plus successful fresh restore establishes coverage.
        frameworks = audited[path].get('frameworks', [])
        require(isinstance(frameworks, list), 'Invalid NuGet audit frameworks')
        for framework in frameworks:
            name = token(framework.get('framework'))
            require(name in framework_names, 'Unknown NuGet audit framework')
            for key in ('topLevelPackages', 'transitivePackages'):
                packages = framework.get(key, [])
                require(isinstance(packages, list), 'Invalid NuGet audit package collection')
                for package in packages:
                    package_id, version = token(package.get('id')), token(package.get('resolvedVersion'))
                    require((name, package_id, version) in resolved, 'NuGet audit does not match resolved inventory')
                    vulnerabilities = package.get('vulnerabilities')
                    require(isinstance(vulnerabilities, list) and vulnerabilities, 'NuGet advisory details missing')
                    for vulnerability in vulnerabilities:
                        findings.append(finding('nuget', path, package_id, version,
                                                vulnerability.get('advisoryurl'), vulnerability.get('severity')))
    require(all_packages, 'NuGet resolved inventory is empty')
    return findings, len(all_packages)


def scan_nuget(root, work):
    inventory = run(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z', '*.csproj'], root)
    expected = set(filter(None, inventory.stdout.split('\0')))
    require(expected, 'No NuGet projects found')
    require(all((root / project).with_name('packages.lock.json').is_file() for project in expected),
            'NuGet project lacks its committed lockfile')
    solution_projects = {p.replace('\\', '/') for p in
                         re.findall(r'= "[^"]+", "([^"]+\.csproj)"', (root / 'Romd.sln').read_text())}
    require(solution_projects and solution_projects <= expected, 'Invalid solution project coverage')
    config = work / 'NuGet.Config'
    config.write_text('<?xml version="1.0"?><configuration><packageSources><clear/>'
                      '<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>'
                      '</packageSources><auditSources><clear/>'
                      '<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>'
                      '</auditSources></configuration>')
    findings, counts = [], set()
    for target, projects in [('Romd.sln', solution_projects),
                             *((p, {p}) for p in sorted(expected - solution_projects))]:
        restored = run(['dotnet', 'restore', target, '--locked-mode', '--force', '--no-http-cache',
                        '--disable-parallel', '--disable-build-servers', '-m:1',
                        '--configfile', config, '--verbosity', 'quiet',
                        '-p:NuGetAudit=true', '-p:NuGetAuditMode=all', '-warnaserror:NU1900,NU1905'], root)
        require(not re.search(r'\b(?:NU1900|NU1905)\b', restored.stdout + restored.stderr),
                'NuGet advisory source unavailable')
        base = ['dotnet', 'package', 'list', '--project', target, '--no-restore', '--include-transitive',
                '--format', 'json', '--output-version', '1']
        packages = decode(run(base, root))
        audit = decode(run([*base, '--vulnerable', '--config', config], root))
        found, count = parse_nuget(packages, audit, root, projects)
        findings.extend(found)
        counts.add((target, count))
    return findings, {'projects': len(expected), 'resolvedPackagesByTarget':
                      {target: count for target, count in sorted(counts)}}


def parse_pnpm(data):
    require(not data.get('error'), 'pnpm registry audit failed')
    advisories = data.get('advisories')
    totals = data.get('metadata', {}).get('vulnerabilities')
    require(isinstance(advisories, dict) and isinstance(totals, dict), 'Unsupported pnpm audit report')
    require(set(totals) == {'info', 'low', 'moderate', 'high', 'critical'}
            and all(type(v) is int and v >= 0 for v in totals.values()), 'Invalid pnpm audit totals')
    findings = []
    observed = Counter()
    for advisory in advisories.values():
        require(isinstance(advisory, dict), 'Invalid pnpm advisory')
        rating = severity(advisory.get('severity'))
        observed[{'MEDIUM': 'moderate'}.get(rating, rating.lower())] += 1
        occurrences = advisory.get('findings')
        require(isinstance(occurrences, list) and occurrences, 'Missing pnpm affected versions')
        for occurrence in occurrences:
            findings.append(finding('npm', 'web/pnpm-lock.yaml', advisory.get('module_name'),
                                    occurrence.get('version'), advisory.get('url'), advisory.get('severity')))
    require(all(observed[key] == value for key, value in totals.items())
            and not set(observed) - set(totals), 'pnpm audit severity totals lack matching advisory details')
    return findings


def npm_environment(work):
    environment = {k: v for k, v in os.environ.items()
                   if not k.lower().startswith(('npm_config_', 'pnpm_', 'npm_token'))}
    environment['NPM_CONFIG_USERCONFIG'] = str(work / 'empty.npmrc')
    environment['NPM_CONFIG_GLOBALCONFIG'] = str(work / 'empty-global.npmrc')
    (work / 'empty.npmrc').write_text('')
    (work / 'empty-global.npmrc').write_text('')
    environment['NPM_CONFIG_CACHE'] = str(work / 'npm-cache')
    return environment


def scan_pnpm(root, work):
    # Use only the committed lockfile and neutral config. Workspace importers and
    # all resolved prod/dev/optional packages remain in the copied lockfile.
    staged = work / 'web'
    staged.mkdir()
    shutil.copyfile(root / 'web/pnpm-lock.yaml', staged / 'pnpm-lock.yaml')
    package = json.loads((root / 'web/package.json').read_text())
    (staged / 'package.json').write_text(json.dumps({'private': True, 'packageManager': package['packageManager']}))
    result = run(['pnpm', 'audit', '--json', '--registry=https://registry.npmjs.org',
                  '--ignore-registry-errors=false', '--ignore-unfixable=false'], staged,
                 accepted=(0, 1), environment=npm_environment(work))
    findings = parse_pnpm(decode(result))
    require(result.returncode == int(bool(findings)), 'pnpm exit status contradicts advisory report')
    return findings, {'lockfile': 'web/pnpm-lock.yaml', 'developmentAndOptionalDependencies': True}


JAVASCRIPT_LOCKFILES = {'web/pnpm-lock.yaml', 'scripts/release/browser/package-lock.json'}
JAVASCRIPT_FILES = ('package.json', 'pnpm-lock.yaml', 'package-lock.json', 'npm-shrinkwrap.json',
                    'yarn.lock', 'bun.lock', 'bun.lockb')


def validate_javascript_coverage(paths, importers):
    """Every repository manifest must belong to an audited lockfile's inventory."""
    paths = set(paths)
    require({p for p in paths if Path(p).name != 'package.json'} == JAVASCRIPT_LOCKFILES,
            'JavaScript lockfile coverage changed; register every lockfile for auditing')
    require(isinstance(importers, dict) and '.' in importers, 'pnpm workspace importer coverage missing')
    covered = {'scripts/release/browser/package.json'}
    for importer in importers:
        require(isinstance(importer, str) and not Path(importer).is_absolute()
                and '..' not in Path(importer).parts and '\\' not in importer, 'Unsafe pnpm importer')
        covered.add((Path('web') / importer / 'package.json').as_posix())
    require({p for p in paths if Path(p).name == 'package.json'} == covered,
            'JavaScript manifest coverage changed; every package.json must have an audited lockfile')
    return sorted(covered)


def parse_npm(data, packages, scope):
    require(data.get('auditReportVersion') == 2 and not data.get('error'), 'Invalid npm audit report')
    vulnerabilities = data.get('vulnerabilities')
    metadata = data.get('metadata', {})
    totals = metadata.get('vulnerabilities')
    levels = {'info', 'low', 'moderate', 'high', 'critical'}
    require(isinstance(vulnerabilities, dict) and isinstance(totals, dict)
            and set(totals) == levels | {'total'}
            and all(type(v) is int and v >= 0 for v in totals.values()), 'Invalid npm audit totals')
    require(totals['total'] == sum(totals[level] for level in levels) == len(vulnerabilities),
            'npm audit totals lack advisory details')
    require(metadata.get('dependencies', {}).get('total') == len(packages) - 1,
            'npm audit package coverage differs from the lockfile')
    observed, findings = Counter(), []

    def advisories(name, parents=()):
        require(name in vulnerabilities and name not in parents, 'Invalid npm advisory dependency chain')
        via = vulnerabilities[name].get('via')
        require(isinstance(via, list) and via, 'Missing npm advisory details')
        result = []
        for item in via:
            if isinstance(item, str):
                result.extend(advisories(item, (*parents, name)))
            else:
                require(isinstance(item, dict), 'Invalid npm advisory')
                severity(item.get('severity'))
                result.append(advisory_url(item.get('url')))
        return result

    for name, item in vulnerabilities.items():
        require(isinstance(item, dict) and item.get('name') == name, 'Invalid npm vulnerability')
        rating = severity(item.get('severity'))
        observed[{'MEDIUM': 'moderate'}.get(rating, rating.lower())] += 1
        nodes = item.get('nodes')
        require(isinstance(nodes, list) and nodes and len(nodes) == len(set(nodes)), 'Missing npm affected packages')
        for node in nodes:
            require(node in packages and node.endswith('node_modules/' + name), 'npm advisory outside lockfile inventory')
            for url in advisories(name):
                findings.append(finding('npm', scope, name, packages[node].get('version'), url, rating))
    require(all(observed[level] == totals[level] for level in levels), 'npm audit severity totals mismatch')
    return findings


def scan_browser_npm(root, work):
    scope = 'scripts/release/browser/package-lock.json'
    lock = json.loads((root / scope).read_text())
    package = json.loads((root / 'scripts/release/browser/package.json').read_text())
    packages = lock.get('packages')
    require(lock.get('lockfileVersion') == 3 and isinstance(packages, dict)
            and '' in packages and len(packages) > 1, 'Missing browser npm lockfile inventory')
    fields = ('dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies')
    require(all(package.get(field, {}) == packages[''].get(field, {}) for field in fields),
            'Browser npm manifest differs from its lockfile')
    for path, item in packages.items():
        if path:
            require(path.startswith('node_modules/') and isinstance(item, dict)
                    and not item.get('link') and item.get('integrity'), 'Unsupported npm lockfile package')
            token(item.get('version'))
    staged = work / 'browser'
    staged.mkdir()
    shutil.copyfile(root / scope, staged / 'package-lock.json')
    (staged / 'package.json').write_text(json.dumps({'private': True, **{k: package[k] for k in fields if k in package}}))
    result = run(['npm', 'audit', '--json', '--package-lock-only', '--ignore-scripts',
                  '--include=dev', '--include=optional', '--include=peer', '--audit-level=low',
                  '--registry=https://registry.npmjs.org'], staged,
                 accepted=(0, 1), environment=npm_environment(work))
    findings = parse_npm(decode(result), packages, scope)
    require(result.returncode == int(bool(findings)), 'npm exit status contradicts advisory report')
    return findings, {'lockfile': scope, 'packages': len(packages) - 1, 'developmentAndOptionalDependencies': True}


def scan_javascript(root, work):
    import yaml
    paths = run(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z', '--',
                 *JAVASCRIPT_FILES, *('**/' + name for name in JAVASCRIPT_FILES)], root).stdout.split('\0')
    importers = yaml.safe_load((root / 'web/pnpm-lock.yaml').read_text()).get('importers')
    manifests = validate_javascript_coverage(filter(None, paths), importers)
    findings, coverage = [], []
    for name, scanner in (('web', scan_pnpm), ('browser', scan_browser_npm)):
        directory = work / name
        directory.mkdir()
        found, inventory = scanner(root, directory)
        findings.extend(found)
        coverage.append(inventory)
    return findings, {'manifests': manifests, 'lockfiles': coverage}


def parse_pub(data, expected):
    require(data.get('SchemaVersion') == 2, 'Unsupported Trivy report schema')
    results = data.get('Results')
    require(isinstance(results, list), 'Missing Trivy results')
    relevant = [r for r in results if r.get('Type') == 'pub' and r.get('Class') == 'lang-pkgs']
    require(len(relevant) == 1, 'Missing or duplicate Pub analyzer coverage')
    result = relevant[0]
    packages = result.get('Packages')
    require(isinstance(packages, list) and packages, 'Missing Pub package coverage')
    covered = {(p.get('Name'), p.get('Version')) for p in packages}
    require(expected and expected <= covered, 'Pub hosted package coverage incomplete')
    findings = []
    vulnerabilities = result.get('Vulnerabilities', [])
    require(isinstance(vulnerabilities, list), 'Invalid Pub advisory collection')
    for vulnerability in vulnerabilities:
        package, version = vulnerability.get('PkgName'), vulnerability.get('InstalledVersion')
        require((package, version) in covered, 'Pub advisory outside scanned inventory')
        advisory = vulnerability.get('PrimaryURL')
        findings.append(finding('pub', 'clients/romd_console/pubspec.lock', package, version,
                                advisory, vulnerability.get('Severity')))
    return findings


def scan_pub(root, work):
    # The YAML parser is the same hash-locked environment used by workflow policy.
    import yaml
    lockfile = root / 'clients/romd_console/pubspec.lock'
    lock = yaml.safe_load(lockfile.read_text())
    packages = lock.get('packages')
    require(isinstance(packages, dict) and packages, 'Pub lockfile missing packages')
    expected, sdk = set(), []
    for name, package in packages.items():
        require(package.get('source') in ('hosted', 'sdk'), 'Pub git/path dependencies need explicit advisory coverage')
        if package['source'] == 'sdk':
            sdk.append(token(name))
        else:
            require(package.get('description', {}).get('url') == 'https://pub.dev',
                    'Pub custom registry needs explicit advisory coverage')
            expected.add((token(name), token(package.get('version'))))
    staged = work / 'pub'
    staged.mkdir()
    shutil.copyfile(lockfile, staged / 'pubspec.lock')
    scanner = ensure_trivy(root / '.artifacts/source-security/tools')
    (work / 'empty-config.json').write_text('{}')
    (work / 'empty-ignore').write_text('')
    environment = {k: v for k, v in os.environ.items() if not k.startswith('TRIVY_')}
    data = decode(run([scanner, 'filesystem', '--scanners', 'vuln', '--config', work / 'empty-config.json',
                       '--ignorefile', work / 'empty-ignore', '--format', 'json', '--exit-code', '0',
                       '--list-all-pkgs', '--include-dev-deps', '--pkg-types', 'library',
                       '--disable-telemetry', '--quiet', '--cache-dir', root / '.artifacts/source-security/cache',
                       staged], work, environment=environment))
    return parse_pub(data, expected), {'hostedPackages': len(expected), 'sdkPackagesExcluded': sorted(sdk),
                                      'developmentDependencies': True}


def scan_go_reader(root, work):
    # This evidence directory is retained independently of normalized diagnostics, including on failure.
    def command(arguments, stage):
        result = run(arguments, work)
        if stage == 'go-reader-govulncheck-binary-package':
            require(not result.stderr.strip(), 'govulncheck diagnostics require investigation')
        return result.stdout
    try:
        artifact, evidence = go_reader.prepare(root, work, command)
        qualified = go_reader.scan(artifact, evidence, work, command)
    except ValueError as error:
        if str(error).startswith('Go reader evidence:'):
            raise ScanError(str(error)) from error
        raise
    sbom = go_reader.dependency_sbom(evidence)
    (work / 'reader.cdx.json').write_text(json.dumps(sbom, indent=2) + '\n')
    scanner = ensure_trivy(root / '.artifacts/source-security/tools')
    (work / 'empty-config.json').write_text('{}')
    (work / 'empty-ignore').touch()
    environment = {k: v for k, v in os.environ.items() if not k.startswith('TRIVY_')}
    run([scanner, 'sbom', '--scanners', 'vuln', '--config', 'empty-config.json', '--ignorefile', 'empty-ignore',
         '--format', 'json', '--output', 'reader.trivy.json', '--exit-code', '0', '--list-all-pkgs',
         '--disable-telemetry', '--quiet', '--cache-dir', root / '.artifacts/source-security/cache',
         'reader.cdx.json'], work, environment=environment)
    raw = json.loads((work / 'reader.trivy.json').read_text())
    coverage = go_reader.assert_inventory(raw, evidence)
    # Trivy's content freshness contract differs from Go's publication timestamp semantics.
    from metadata import assert_fresh_database
    version = decode(run([scanner, 'version', '--cache-dir', root / '.artifacts/source-security/cache', '--format', 'json'],
                         work, environment=environment))
    assert_fresh_database(version)
    (work / 'trivy-version.json').write_text(json.dumps(version, indent=2) + '\n')
    policy = json.loads((root / 'scripts/security/policy.json').read_text())
    assessment = assess(raw, 'sha256:' + evidence['hashes']['catalog-reader'], policy, qualified, mode='sbom', sbom=sbom)
    (work / 'reader.applicability.json').write_text(json.dumps(assessment, indent=2) + '\n')
    findings = []
    for item in assessment['findings']:
        normalized = finding('go-reader', 'Dockerfile/catalog-reader', item['component'], item['installedVersion'],
                             'https://osv.dev/vulnerability/' + item['id'], item['severity'])
        normalized.update(rawBlocked=item['rawBlocked'], blocked=item['blocked'], applicability=item['applicability'])
        findings.append(normalized)
    for item in assessment['goAdvisories']:
        normalized = finding('go-reader', 'Dockerfile/catalog-reader', item['module'], item['installedVersion'],
                             'https://osv.dev/vulnerability/' + item['id'], item['severity'])
        normalized.update(rawBlocked=True, blocked=item['blocked'], applicability=item['applicability'])
        findings.append(normalized)
    return findings, coverage


def audit(root, output, scanners=None):
    policy = json.loads((root / 'scripts/security/policy.json').read_text())
    require(policy.get('schemaVersion') == 1 and set(policy.get('blockedSeverities', [])) ==
            {'HIGH', 'CRITICAL', 'UNKNOWN'}, 'Unexpected security severity policy')
    scanners = scanners or {'nuget': scan_nuget, 'npm': scan_javascript, 'pub': scan_pub, 'go-reader': scan_go_reader}
    report = {'schemaVersion': 1, 'checkedAt': datetime.now(timezone.utc).isoformat(),
              'status': 'running', 'blockedSeverities': policy['blockedSeverities'],
              'ecosystems': {name: {'status': 'not-run'} for name in scanners}}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + '\n')
    output.chmod(0o600)
    with tempfile.TemporaryDirectory(prefix='romd-source-advisories-') as temporary:
        for name, scanner in scanners.items():
            try:
                work = output.parent / 'go-reader-evidence' if name == 'go-reader' else Path(temporary) / name
                work.mkdir(mode=0o700)
                findings, coverage = scanner(root, work)
                unique = sorted({json.dumps(f, sort_keys=True) for f in findings})
                findings = [json.loads(f) for f in unique]
                blocked = sum(f['severity'] in policy['blockedSeverities'] and (f['blocked'] if name == 'go-reader' else True) for f in findings)
                report['ecosystems'][name] = dict(status='failed' if blocked else 'passed',
                                                  coverage=coverage, findings=findings, blocked=blocked)
                print(f'{name}: {len(findings)} advisories, {blocked} blocking', flush=True)
            except (ValueError, OSError, KeyError, TypeError, AttributeError) as error:
                report['ecosystems'][name] = {'status': 'error', 'error': type(error).__name__}
                if isinstance(error, ScanError):
                    report['ecosystems'][name]['reason'] = str(error)
                print(f'{name}: scanner/coverage error ({type(error).__name__}); no clean result', flush=True)
            statuses = [r['status'] for r in report['ecosystems'].values()]
            report['status'] = ('failed' if any(s in ('failed', 'error') for s in statuses)
                                else 'passed' if all(s == 'passed' for s in statuses) else 'running')
            output.write_text(json.dumps(report, indent=2) + '\n')
            output.chmod(0o600)
    return 0 if report['status'] == 'passed' else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    tools = tomllib.loads((ROOT / '.mise.toml').read_text())['tools']
    for name in ('dotnet', 'node', 'pnpm'):
        actual = run([name, '--version'], ROOT).stdout.strip().removeprefix('v')
        require(actual == tools[name], 'Use the repository-pinned toolchain via mise exec')
    return audit(ROOT, args.output)


if __name__ == '__main__':
    raise SystemExit(main())
