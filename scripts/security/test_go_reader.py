import copy
from datetime import datetime, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import go_applicability as applicability
import go_reader
import source_advisories as source
import metadata

MODULE = applicability.MODULE
ROOT = applicability.ENTRYPOINT
DEP = 'golang.org/x/crypto'
VERSION = 'v0.58.0'
GO = 'go1.27.3'
IDENTIFIER = 'GO-2026-5932'
NOW = datetime(2026, 10, 19, 18, tzinfo=timezone.utc)
CHECKED = NOW.isoformat()
PUBLISHED = '2026-10-08T22:31:09Z'
POLICY = {'schemaVersion': 2, 'blockedSeverities': ['HIGH', 'CRITICAL', 'UNKNOWN'], 'maxExceptionDays': 90,
          'exceptions': [], 'assessments': []}


def encoded(messages):
    return '\n'.join(json.dumps(item) for item in messages) + '\n'


def osv():
    return dict(schema_version='1.3.1', id=IDENTIFIER, aliases=['CVE-2026-12345'], modified=PUBLISHED,
                database_specific={'review_status': 'REVIEWED'}, affected=[{
                    'package': {'ecosystem': 'Go', 'name': DEP},
                    'ranges': [{'type': 'SEMVER', 'events': [{'introduced': '0'}]}],
                    'ecosystem_specific': {'imports': [{'path': DEP + '/openpgp', 'symbols': ['ReadMessage']},
                                                       {'path': DEP + '/openpgp/packet', 'goos': ['windows']}]}}])


def messages():
    return [{'config': dict(protocol_version='v1.0.0', scanner_name='govulncheck', scanner_version='v1.9.0',
                            db=applicability.DB, db_last_modified=PUBLISHED, scan_level='package', scan_mode='binary')},
            {'SBOM': {'go_version': GO, 'roots': [MODULE], 'modules': [
                {'path': MODULE, 'version': '(devel)'}, {'path': DEP, 'version': VERSION}, {'path': 'stdlib', 'version': GO}]}},
            {'osv': osv()}, {'finding': {'osv': IDENTIFIER, 'trace': [{'module': DEP, 'version': VERSION}]}}]


def trivy(identifier=IDENTIFIER, target='app/catalog-reader', package=DEP, version=VERSION, severity='UNKNOWN'):
    return {'SchemaVersion': 2, 'Results': [{'Class': 'lang-pkgs', 'Type': 'gobinary', 'Target': target,
            'Packages': [{'Name': DEP, 'Version': VERSION}, {'Name': 'stdlib', 'Version': GO.replace('go', 'v')}],
            'Vulnerabilities': [{'VulnerabilityID': identifier, 'PkgName': package, 'InstalledVersion': version,
                                 'Severity': severity, 'PkgIdentifier': {'PURL': f'pkg:golang/{package}@{version}'}}]}]}


class GoEvidenceFixture(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.work = Path(self.temporary.name)
        self.artifact = self.work / 'artifacts'
        self.artifact.mkdir()
        self.pins = dict(sourceCommit='a' * 40, sourceArchiveSha256='', sourceUrl='https://example.test/archive',
                         compilerImage='golang:1.27.3-bookworm@sha256:' + 'b' * 64, goVersion=GO, trustRootSha256='',
                         govulncheck=dict(module='golang.org/x/vuln', version='v1.9.0', goSum='h1:tool',
                                         goModSum='h1:toolmod', sourceZipSha256=''))
        self.go_mod = f'module {MODULE}\n\ngo 1.26.0\nrequire (\n {DEP} {VERSION} // indirect\n)\n'
        self.build = dict(GoVersion=GO, Path=ROOT, Main={'Path': MODULE, 'Version': '(devel)'},
                          Deps=[{'Path': DEP, 'Version': VERSION}], Settings=[{'Key': key, 'Value': value} for key, value in
                          {'GOOS': 'linux', 'GOARCH': 'amd64', 'CGO_ENABLED': '0', '-trimpath': 'true', '-buildmode': 'exe'}.items()])
        self.graph = [{'ImportPath': 'fmt', 'Standard': True},
                      {'ImportPath': DEP + '/ssh', 'Module': {'Path': DEP, 'Version': VERSION}, 'Imports': ['fmt']},
                      {'ImportPath': ROOT, 'Module': {'Path': MODULE, 'Main': True},
                       'Imports': [DEP + '/ssh'], 'Deps': ['fmt', DEP + '/ssh']}]
        values = {'catalog-reader': b'controlled-reader-binary', '1.root.json': b'{}',
                  'go.mod': self.go_mod.encode(), 'go.sum': b'unselected.example/history v1.0.0 h1:old\n',
                  'packages.json': encoded(self.graph).encode(), 'buildinfo.json': json.dumps(self.build).encode(),
                  'govulncheck': b'controlled-scanner-binary', 'govulncheck-source.zip': b'controlled-module-archive',
                  'govulncheck-module.json': json.dumps(dict(Path='golang.org/x/vuln', Version='v1.9.0', Sum='h1:tool', GoModSum='h1:toolmod')).encode(),
                  'govulncheck-buildinfo.json': json.dumps(dict(GoVersion=GO, Path='golang.org/x/vuln/cmd/govulncheck',
                                                              Main={'Version': 'v1.9.0', 'Sum': 'h1:tool'})).encode()}
        with tarfile.open(self.artifact / 'reader.tar.gz', 'w:gz') as archive:
            for name in ('go.mod', 'go.sum'):
                item = tarfile.TarInfo('romd-dat-catalogs-' + self.pins['sourceCommit'] + '/' + name)
                item.size = len(values[name])
                archive.addfile(item, io.BytesIO(values[name]))
        for name, value in values.items():
            (self.artifact / name).write_bytes(value)
        self.pins.update(sourceArchiveSha256=applicability.digest(self.artifact / 'reader.tar.gz'),
                         trustRootSha256=applicability.digest(self.artifact / '1.root.json'))
        self.pins['govulncheck']['sourceZipSha256'] = applicability.digest(self.artifact / 'govulncheck-source.zip')
        self.write('catalog-reader.json', dict(schemaVersion=1, module=MODULE, sourceCommit=self.pins['sourceCommit'],
                                              binarySha256=applicability.digest(self.artifact / 'catalog-reader'), goVersion=GO))
        self.receipt()

    def write(self, name, value):
        (self.artifact / name).write_text(json.dumps(value))

    def receipt(self):
        self.write('qualification.json', dict(schemaVersion=1, source=self.pins,
            files={name: applicability.digest(self.artifact / name) for name in go_reader.ARTIFACTS}))

    def evidence(self):
        return applicability.validate_artifacts(self.artifact, self.pins)

    def scanned(self, events=None):
        return applicability.validate_scan(encoded(events or messages()), self.evidence(), {'modified': PUBLISHED}, CHECKED, NOW)

    def assess(self, raw=None, evidence=None, mode='image', sbom=None):
        return applicability.assess(raw or trivy(), 'sha256:' + 'd' * 64, POLICY, evidence or self.scanned(), mode=mode, sbom=sbom)


class GoApplicabilityTests(GoEvidenceFixture):
    def test_exact_source_graph_inventories_indirect_modules_and_explicit_compiler_not_minimum(self):
        evidence = self.evidence()
        self.assertEqual({DEP: VERSION}, evidence['required'])
        self.assertEqual('1.26.0', evidence['minimumGo'])
        self.assertEqual(GO, evidence['compiler'])
        sbom = go_reader.dependency_sbom(evidence)
        self.assertEqual({DEP, 'stdlib'}, {item['name'] for item in sbom['components']})
        self.assertNotIn('unselected.example/history', json.dumps(sbom))
        self.assertTrue(go_reader.assert_inventory(trivy(), evidence)['standardLibraryInventoried'])

    def test_retains_raw_unknown_warning_while_proving_all_affected_packages_absent(self):
        raw = trivy('CVE-2026-12345')
        original = copy.deepcopy(raw)
        result = self.assess(raw)
        finding = result['findings'][0]
        self.assertEqual('passed', result['result'])
        self.assertEqual(original, raw)
        self.assertEqual(('UNKNOWN', True, False, False), (finding['severity'], finding['rawBlocked'], finding['blocked'], finding['excepted']))
        proof = finding['applicability']['evidence']
        self.assertEqual(IDENTIFIER, proof['primaryOsv'])
        self.assertEqual([DEP + '/openpgp', DEP + '/openpgp/packet'], proof['affectedImports'])
        self.assertEqual(applicability.digest(self.artifact / 'catalog-reader'), proof['binarySha256'])
        self.assertEqual(self.pins['compilerImage'], proof['compilerImage'])
        self.assertEqual('UNKNOWN', result['goAdvisories'][0]['severity'])

    def test_imported_package_blocks_even_without_any_called_vulnerable_symbol(self):
        evidence = self.scanned()
        evidence['imports'].add(DEP + '/openpgp')
        self.assertEqual('failed', self.assess(evidence=evidence)['result'])
        # A conservative binary-symbol fallback package finding also blocks even
        # when the source graph does not contain that import.
        events = messages()
        events[-1]['finding']['trace'][0]['package'] = DEP + '/openpgp'
        result = self.assess(evidence=self.scanned(events))
        self.assertTrue(result['findings'][0]['blocked'])
        self.assertTrue(result['goAdvisories'][0]['blocked'])

    def test_zero_exit_json_finding_cannot_pass_when_trivy_returns_no_findings(self):
        events = messages()
        events[-1]['finding']['trace'][0]['package'] = DEP + '/openpgp'
        result = self.assess({'SchemaVersion': 2, 'Results': []}, self.scanned(events))
        self.assertEqual('failed', result['result'])
        self.assertEqual([], result['findings'])
        self.assertTrue(result['goAdvisories'][0]['blocked'])

    def test_other_binary_non_go_and_unbound_sbom_components_cannot_inherit_resolution(self):
        raw = trivy(target='app/other-tool')
        self.assertEqual('failed', self.assess(raw)['result'])
        # A root-level /catalog-reader is a different image component from the
        # exact /app/catalog-reader extracted and hashed for this proof.
        self.assertEqual('failed', self.assess(trivy(target='catalog-reader'))['result'])
        self.assertEqual('failed', self.assess(trivy(target='/catalog-reader'))['result'])
        raw = trivy()
        raw['Results'][0]['Type'] = 'npm'
        self.assertEqual('failed', self.assess(raw)['result'])
        sbom = go_reader.dependency_sbom(self.evidence())
        self.assertEqual('passed', self.assess(mode='sbom', sbom=sbom)['result'])
        sbom['components'][0]['properties'].append({'name': 'syft:location:1:path', 'value': '/app/other-tool'})
        self.assertEqual('failed', self.assess(mode='sbom', sbom=sbom)['result'])
        self.assertEqual('failed', self.assess(mode='sbom', sbom={'components': []})['result'])
        self.assertEqual('failed', self.assess(trivy(target='../../app/catalog-reader'))['result'])
        sbom = go_reader.dependency_sbom(self.evidence())
        sbom['components'][0]['purl'] = 'pkg:golang/example.test/wrong@' + VERSION
        self.assertEqual('failed', self.assess(mode='sbom', sbom=sbom)['result'])

    def test_real_syft_stdlib_and_trivy_version_spellings_bind_to_same_exact_compiler(self):
        evidence = self.evidence()
        sbom = {'components': [{'name': 'stdlib', 'version': GO, 'purl': 'pkg:golang/stdlib@1.27.3',
                                'properties': [{'name': 'syft:location:0:path', 'value': '/app/catalog-reader'}]}]}
        self.assertTrue(applicability.sbom_bound(sbom, 'stdlib', '1.27.3', evidence))
        self.assertFalse(applicability.sbom_bound(sbom, 'stdlib', '1.27.2', evidence))
        raw = trivy()
        raw['Results'][0]['Packages'][-1]['Version'] = '1.27.3'
        self.assertTrue(go_reader.assert_inventory(raw, evidence)['standardLibraryInventoried'])

    def test_unknown_ambiguous_alias_wrong_module_version_and_osv_ranges_remain_blocking(self):
        for identifier, package, version in (('GO-9999-9999', DEP, VERSION), (IDENTIFIER, DEP, 'v0.59.0'),
                                              (IDENTIFIER, 'example.test/other', VERSION)):
            with self.subTest(identifier=identifier, package=package, version=version):
                self.assertEqual('failed', self.assess(trivy(identifier, package=package, version=version))['result'])
        changes = [lambda value: value.update(schema_version='2.0'),
                   lambda value: value.update(withdrawn=PUBLISHED),
                   lambda value: value.update(database_specific={'review_status': 'UNREVIEWED'}),
                   lambda value: value['affected'][0].pop('ecosystem_specific'),
                   lambda value: value['affected'][0].update(ranges=[{'type': 'SEMVER', 'events': [{'introduced': '0'}, {'fixed': '0.50.0'}]}]),
                   lambda value: value['affected'][0].update(ranges=[{'type': 'GIT', 'events': [{'introduced': '0'}]}])]
        for change in changes:
            evidence = self.scanned()
            change(evidence['osvs'][IDENTIFIER])
            with self.subTest(change=change):
                self.assertEqual('failed', self.assess(evidence=evidence)['result'])
        evidence = self.scanned()
        evidence['osvs']['GO-2026-9999'] = dict(osv(), id='GO-2026-9999')
        self.assertEqual('failed', self.assess(trivy('CVE-2026-12345'), evidence)['result'])
        evidence = self.scanned()
        evidence['findings'] = []
        self.assertEqual('failed', self.assess(evidence=evidence)['result'])

    def test_complete_stream_required_and_missing_compiler_inventory_or_protocol_cannot_pass(self):
        for raw in ('', encoded(messages()) + '{', '[]', '{"config":{},"config":{}}', '{"x":NaN}'):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                applicability.validate_scan(raw, self.evidence(), {'modified': PUBLISHED}, CHECKED, NOW)
        changes = [lambda events: events.pop(1),
                   lambda events: events.append(copy.deepcopy(events[1])),
                   lambda events: events[0]['config'].update(scan_level='symbol'),
                   lambda events: events[0]['config'].update(scan_mode='source'),
                   lambda events: events[0]['config'].update(scanner_version='v9.9.9'),
                   lambda events: events[1]['SBOM'].update(go_version='go1.26.0'),
                   lambda events: events[1]['SBOM']['modules'].pop(),
                   lambda events: events[1]['SBOM']['modules'][1].update(version='v0.57.0'),
                   lambda events: events.append({'error': 'scanner failed'}),
                   lambda events: events[-1]['finding']['trace'][0].update(function='ReadMessage')]
        for change in changes:
            events = messages()
            change(events)
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.scanned(events)

    def test_identical_repeated_osv_for_multiple_modules_is_unambiguous_but_conflicts_fail(self):
        events = messages() + [{'osv': osv()}]
        self.assertEqual(1, len(self.scanned(events)['osvs']))
        events[-1]['osv']['aliases'] = ['CVE-2026-99999']
        with self.assertRaisesRegex(ValueError, 'conflicting primary OSV'):
            self.scanned(events)

    def test_stdlib_finding_version_spellings_preserve_package_findings_and_block(self):
        identifier = 'GO-2026-9998'
        primary = dict(osv(), id=identifier, affected=[{
            'package': {'ecosystem': 'Go', 'name': 'stdlib'},
            'ranges': [{'type': 'SEMVER', 'events': [{'introduced': '0'}, {'fixed': '1.27.4'}]}],
            'ecosystem_specific': {'imports': [{'path': 'net/http'}]}}])
        for version in ('v1.27.3', 'go1.27.3', '1.27.3'):
            events = messages() + [{'osv': primary},
                {'finding': {'osv': identifier, 'trace': [{'module': 'stdlib', 'version': version}]}},
                {'finding': {'osv': identifier, 'trace': [{'module': 'stdlib', 'version': version, 'package': 'net/http'}]}}]
            with self.subTest(version=version):
                evidence = self.scanned(events)
                self.assertEqual(events[-2:], [{'finding': item} for item in evidence['findings'][-2:]])
                result = self.assess({'SchemaVersion': 2, 'Results': []}, evidence)
                self.assertEqual('failed', result['result'])
                advisory = next(item for item in result['goAdvisories'] if item['id'] == identifier)
                self.assertTrue(advisory['blocked'])
                self.assertIn('affected package', advisory['applicability']['reason'])

    def test_finding_versions_cannot_change_compiler_or_normalize_other_modules(self):
        for module, version in (('stdlib', 'v1.27.2'), ('stdlib', 'v1.27.30'),
                                ('stdlib', 'go1.27.3-extra'), (DEP, 'go0.58.0'),
                                ('unlisted.example/module', VERSION)):
            events = messages()
            events[-1]['finding']['trace'][0].update(module=module, version=version)
            with self.subTest(module=module, version=version), self.assertRaises(ValueError):
                self.scanned(events)

    def test_fresh_query_accepts_old_publication_but_rejects_stale_future_or_mismatched_query(self):
        self.scanned()  # Actual official Go DB publication may be older than 72h.
        for checked, index in (('2026-10-01T00:00:00Z', PUBLISHED), ('2026-10-20T00:00:00Z', PUBLISHED),
                               (CHECKED, '2026-10-09T00:00:00Z'), (CHECKED, 'not-a-time')):
            with self.subTest(checked=checked, index=index), self.assertRaises(ValueError):
                applicability.validate_scan(encoded(messages()), self.evidence(), {'modified': index}, checked, NOW)

    def test_missing_or_rewritten_inventory_and_stripped_vcs_or_wrong_compiler_fail(self):
        for mutate in (lambda value: value.update(GoVersion='go1.26.0'),
                       lambda value: value.update(Deps=[]),
                       lambda value: value['Deps'][0].update(Version='v0.57.0'),
                       lambda value: value['Settings'].append({'Key': '-ldflags', 'Value': '-s -w'}),
                       lambda value: value['Settings'].append({'Key': 'vcs.revision', 'Value': 'c' * 40}),
                       lambda value: value['Settings'].append({'Key': 'GOARCH', 'Value': 'arm64'})):
            build = copy.deepcopy(self.build)
            mutate(build)
            self.write('buildinfo.json', build)
            self.receipt()
            with self.subTest(mutate=mutate), self.assertRaises(ValueError):
                self.evidence()
        self.write('buildinfo.json', self.build)
        for graph in (self.graph[:-1], self.graph[1:], self.graph + [self.graph[0]],
                      [dict(self.graph[0], Incomplete=True), *self.graph[1:]]):
            (self.artifact / 'packages.json').write_text(encoded(graph))
            self.receipt()
            with self.subTest(graph=graph), self.assertRaises(ValueError):
                self.evidence()

    def test_wrong_actual_binary_source_receipt_tool_hash_and_missing_stdlib_fail(self):
        evidence = self.evidence()
        go_reader.assert_final_binding(self.artifact / 'catalog-reader', self.artifact / 'catalog-reader.json', evidence)
        receipt = json.loads((self.artifact / 'catalog-reader.json').read_text())
        for field, value in (('sourceCommit', 'c' * 40), ('goVersion', 'go1.26.0'), ('binarySha256', 'e' * 64)):
            self.write('catalog-reader.json', dict(receipt, **{field: value}))
            with self.subTest(field=field), self.assertRaises(ValueError):
                go_reader.assert_final_binding(self.artifact / 'catalog-reader', self.artifact / 'catalog-reader.json', evidence)
        self.write('catalog-reader.json', receipt)
        (self.artifact / 'catalog-reader').write_bytes(b'other-reader')
        with self.assertRaises(ValueError):
            go_reader.assert_final_binding(self.artifact / 'catalog-reader', self.artifact / 'catalog-reader.json', evidence)
        with self.assertRaises(ValueError):
            self.evidence()
        raw = trivy()
        raw['Results'][0]['Packages'].pop()
        with self.assertRaises(ValueError):
            go_reader.assert_inventory(raw, evidence)

    def test_qualification_source_and_tool_checksum_substitution_are_rejected(self):
        pins = copy.deepcopy(self.pins)
        pins['sourceCommit'] = 'f' * 40
        with self.assertRaisesRegex(ValueError, 'source/compiler provenance'):
            applicability.validate_artifacts(self.artifact, pins)
        tool = json.loads((self.artifact / 'govulncheck-module.json').read_text())
        tool['Sum'] = 'h1:substituted'
        self.write('govulncheck-module.json', tool)
        self.receipt()
        with self.assertRaisesRegex(ValueError, 'module checksum'):
            self.evidence()
        (self.artifact / 'govulncheck').write_bytes(b'changed-scanner')
        with self.assertRaisesRegex(ValueError, 'artifact hash'):
            self.evidence()

    def test_go_mod_replacements_exclusions_and_duplicate_requirements_need_explicit_coverage(self):
        for suffix in (f'\nreplace {DEP} => example.test/fork v1.0.0', f'\nexclude {DEP} {VERSION}',
                       f'\nrequire {DEP} {VERSION}', '\nrequire ('):
            with self.subTest(suffix=suffix), self.assertRaises(ValueError):
                applicability.parse_go_mod(self.go_mod + suffix)


class GoOrchestrationTests(GoEvidenceFixture):
    def setUp(self):
        super().setUp()
        # Qualification runs only on native Linux amd64; simulate that host so these tests run anywhere.
        native = patch.object(go_reader, 'platform', SimpleNamespace(system=lambda: 'Linux', machine=lambda: 'x86_64'))
        native.start()
        self.addCleanup(native.stop)

    def test_successful_exit_with_scanner_diagnostics_fails_source_and_final_gates(self):
        completed = subprocess.CompletedProcess([], 0, '{}', 'unexpected diagnostics')
        def prepare(_root, _work, command):
            command(['govulncheck'], 'go-reader-govulncheck-binary-package')
        with patch.object(go_reader, 'prepare', side_effect=prepare), patch.object(source, 'run', return_value=completed):
            with self.assertRaisesRegex(source.ScanError, 'diagnostics require investigation'):
                source.scan_go_reader(self.work, self.work)
        commands = metadata.Commands(self.work, self.work)
        with patch.object(metadata.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, b'{}', b'unexpected diagnostics')):
            with self.assertRaisesRegex(RuntimeError, 'diagnostics require investigation'):
                commands.run(['govulncheck'], 'go-reader-govulncheck-binary-package')

    def test_final_image_scan_uses_actual_extracted_reader_and_retains_bad_raw_stream(self):
        binary, receipt = self.artifact / 'catalog-reader', self.artifact / 'catalog-reader.json'
        def scanner(artifacts, evidence, output, run, **kwargs):
            self.assertEqual(binary, kwargs['binary'])
            output.mkdir()
            (output / 'govulncheck.jsonstream').write_text('{truncated')
            raise ValueError('invalid scanner stream')
        with patch.object(go_reader, 'scan', side_effect=scanner), self.assertRaisesRegex(ValueError, 'invalid scanner stream'):
            metadata.scan_packaged_reader(binary, receipt, self.artifact, self.evidence(), self.work, self.work,
                                          SimpleNamespace(run=lambda *_args: ''), 'admin')
        retained = json.loads((self.work / 'admin.go-reader.json').read_text())
        self.assertEqual('{truncated', retained['govulncheck.jsonstream'])

    def test_qualification_refuses_hosts_other_than_native_linux_amd64(self):
        for system, machine in (('Darwin', 'arm64'), ('Linux', 'aarch64'), ('Windows', 'AMD64')):
            host = SimpleNamespace(system=lambda value=system: value, machine=lambda value=machine: value)
            with self.subTest(system=system, machine=machine), patch.object(go_reader, 'platform', host), \
                    self.assertRaisesRegex(ValueError, 'native Linux amd64'):
                go_reader.prepare(self.work, self.work, lambda *args: self.fail('must fail before running commands'))

    def test_docker_buildkit_is_explicit_and_missing_engine_fails_closed(self):
        with patch.dict(os.environ, {'ROMD_CONTAINER_ENGINE': 'podman'}), self.assertRaisesRegex(ValueError, 'Docker BuildKit'):
            go_reader.prepare(self.work, self.work, lambda *args: self.fail('must fail before running commands'))
        with patch.object(go_reader, 'pins', return_value=self.pins), patch.dict(os.environ, {'ROMD_CONTAINER_ENGINE': 'docker'}):
            def missing(_arguments, _stage):
                raise OSError('missing docker executable')
            with self.assertRaises(OSError):
                go_reader.prepare(self.work, self.work, missing)

    def test_fresh_export_binds_manifests_and_rejects_substitution(self):
        def export(arguments, stage):
            if stage.endswith('artifacts'):
                self.artifact.rename(self.work / 'reader-artifacts')
            return ''
        with patch.object(go_reader, 'pins', return_value=self.pins), patch.dict(os.environ, {'ROMD_CONTAINER_ENGINE': 'docker'}):
            artifact, evidence = go_reader.prepare(self.work, self.work, export)
            self.assertEqual(self.pins['sourceCommit'], evidence['pins']['sourceCommit'])
            self.assertEqual(self.work / 'reader-artifacts', artifact)
            with self.assertRaisesRegex(ValueError, 'already exists'):
                go_reader.prepare(self.work, self.work, export)

    def test_fresh_primary_osv_must_agree_and_nonzero_scanner_never_becomes_clean(self):
        evidence = self.evidence()
        def fetch(url):
            return {'modified': PUBLISHED} if url.endswith('db.json') else osv()
        with patch.object(go_reader, 'fetch', side_effect=fetch), patch('go_reader.datetime') as clock:
            clock.now.return_value = NOW
            with patch('go_applicability.datetime') as validator_clock:
                validator_clock.now.return_value = NOW
                validator_clock.fromisoformat = datetime.fromisoformat
                qualified = go_reader.scan(self.artifact, evidence, self.work / 'scan', lambda *_args: encoded(messages()))
        self.assertEqual(1, len(qualified['findings']))
        retained = go_reader.retained_scan(self.work / 'scan')
        self.assertEqual(encoded(messages()), retained['govulncheck.jsonstream'])
        self.assertIn(IDENTIFIER + '.osv.json', retained)
        with patch.object(go_reader, 'fetch', side_effect=fetch), \
                patch.object(source.subprocess, 'run', return_value=subprocess.CompletedProcess([], 2, '{}', '')):
            with self.assertRaises(source.ScanError):
                go_reader.scan(self.artifact, evidence, self.work / 'error',
                               lambda arguments, _stage: source.run(arguments, self.work).stdout)
        with patch.object(go_reader, 'fetch', side_effect=[{'modified': PUBLISHED}, dict(osv(), aliases=['CVE-2026-99999'])]), \
                patch('go_reader.datetime') as clock, patch('go_applicability.datetime') as validator_clock:
            clock.now.return_value = NOW
            validator_clock.now.return_value = NOW
            validator_clock.fromisoformat = datetime.fromisoformat
            with self.assertRaisesRegex(ValueError, 'fresh primary OSV'):
                go_reader.scan(self.artifact, evidence, self.work / 'mismatch', lambda *_args: encoded(messages()))


if __name__ == '__main__':
    unittest.main()
