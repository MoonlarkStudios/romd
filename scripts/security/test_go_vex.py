import copy
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import go_vex

ROOT = Path(__file__).resolve().parents[2]
# The shape govulncheck v1.9.0 emits for the shipped v0.1.0-rc.7 catalog reader.
STATEMENT = {
    'vulnerability': {'@id': 'https://pkg.go.dev/vuln/GO-2026-5932', 'name': 'GO-2026-5932',
                      'description': 'Example advisory'},
    'products': [{'@id': 'Unknown Product', 'subcomponents': [{'@id': 'pkg:golang/golang.org%2Fx%2Fcrypto@v0.58.0'}]}],
    'status': 'not_affected', 'justification': 'vulnerable_code_not_present',
    'impact_statement': "Govulncheck determined that the vulnerable code isn't called"}


def document(*statements):
    return json.dumps({'@context': go_vex.OPENVEX, '@id': 'govulncheck/vex:controlled', 'author': 'Unknown Author',
                       'version': 1, 'statements': list(statements)})


def report(*groups):
    return {'SchemaVersion': 2, 'Results': list(groups)}


def reader(*names):
    return {'Target': go_vex.READER, 'Class': 'lang-pkgs', 'Type': 'gobinary',
            'Packages': [{'Name': name, 'Version': 'v1.27.4' if name == 'stdlib' else 'v1.0.0'} for name in names]}


TRUST = ('stdlib', 'github.com/sigstore/sigstore', 'github.com/theupdateframework/go-tuf/v2')


class NormalizeTests(unittest.TestCase):
    def test_statements_name_decoded_package_urls_as_products_so_trivy_can_match_them(self):
        normalized, blocking = go_vex.normalize(document(STATEMENT))
        self.assertEqual([], blocking)
        self.assertEqual([{'@id': 'pkg:golang/golang.org/x/crypto@v0.58.0'}], normalized['statements'][0]['products'])
        self.assertEqual('not_affected', normalized['statements'][0]['status'])
        self.assertEqual(go_vex.OPENVEX, normalized['@context'])

    def test_any_status_other_than_code_not_present_blocks_whatever_the_severity(self):
        for changes in ({'status': 'affected', 'justification': None}, {'status': 'under_investigation'},
                        {'justification': 'vulnerable_code_not_in_execute_path'}, {'justification': None}):
            statement = {key: value for key, value in dict(STATEMENT, **changes).items() if value is not None}
            with self.subTest(changes=changes):
                normalized, blocking = go_vex.normalize(document(statement))
                self.assertEqual(['GO-2026-5932'], blocking)
                # Kept in the document, where Trivy leaves the finding reported.
                self.assertEqual(statement['status'], normalized['statements'][0]['status'])

    def test_malformed_documents_and_statements_are_refused(self):
        def changed(**changes):
            return dict(copy.deepcopy(STATEMENT), **changes)
        for text in ('not json', json.dumps([]), json.dumps({'@context': 'https://openvex.dev/ns/v0.1.0', 'statements': []}),
                     json.dumps({'@context': go_vex.OPENVEX}),
                     document(changed(vulnerability={'name': 'CVE-2026-1'})),
                     document(changed(products=[{'@id': 'Unknown Product'}])),
                     document(changed(products=[{'@id': 'x', 'subcomponents': [{'@id': 'pkg:npm/left-pad@1.0.0'}]}])),
                     document(changed(products=[{'@id': 'x', 'subcomponents': [{'@id': 'pkg:golang/stdlib'}]}]))):
            with self.subTest(text=text[:60]), self.assertRaises(ValueError):
                go_vex.normalize(text)

    def test_scan_runs_binary_mode_openvex_against_the_official_database_and_writes_the_document(self):
        calls = []
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / 'admin.vex.json'
            blocking = go_vex.scan('/tools/govulncheck', '/work/catalog-reader', output,
                                   lambda command, stage: calls.append((command, stage)) or document(STATEMENT), 'govulncheck-admin')
            written = json.loads(output.read_text())
        self.assertEqual([], blocking)
        self.assertEqual([(['/tools/govulncheck', '-db=https://vuln.go.dev', '-mode=binary', '-format=openvex',
                            '/work/catalog-reader'], 'govulncheck-admin')], calls)
        self.assertEqual('pkg:golang/golang.org/x/crypto@v0.58.0', written['statements'][0]['products'][0]['@id'])


class BindingTests(unittest.TestCase):
    def test_vex_applies_only_when_the_reader_is_the_only_go_binary(self):
        go_vex.assert_only_reader(report({'Target': 'image (ubuntu 24.04)', 'Class': 'os-pkgs', 'Type': 'ubuntu'}, reader('stdlib')))
        for groups in ((), (reader('stdlib'), dict(reader('stdlib'), Target='usr/local/bin/other')),
                       (dict(reader('stdlib'), Target='usr/local/bin/other'),),
                       (reader('stdlib'), dict(reader('stdlib'), Type='gomod', Target='go.mod'))):
            with self.subTest(groups=groups), self.assertRaisesRegex(ValueError, 'only Go binary'):
                go_vex.assert_only_reader(report(*groups))

    def test_reader_scan_must_inventory_standard_library_and_linked_trust_modules(self):
        self.assertEqual({'goModules': 2, 'standardLibrary': 'v1.27.4'}, go_vex.assert_reader_inventory(report(reader(*TRUST))))
        for missing in TRUST:
            with self.subTest(missing=missing), self.assertRaisesRegex(ValueError, 'standard library and linked trust'):
                go_vex.assert_reader_inventory(report(reader(*(name for name in TRUST if name != missing))))


class ExportTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        native = patch.object(go_vex, 'platform', SimpleNamespace(system=lambda: 'Linux', machine=lambda: 'x86_64'))
        native.start()
        self.addCleanup(native.stop)

    def test_dockerfile_builds_exactly_the_reviewed_govulncheck_pin(self):
        self.assertEqual('v1.9.0', go_vex.pin(ROOT)['version'])
        root = self.work / 'root'
        (root / 'scripts/security').mkdir(parents=True)
        (root / 'scripts/security/tools.json').write_bytes((ROOT / 'scripts/security/tools.json').read_bytes())
        (root / 'Dockerfile').write_text((ROOT / 'Dockerfile').read_text().replace('h1:12VaqnI9', 'h1:changed'))
        with self.assertRaisesRegex(ValueError, 'govulncheck pins'):
            go_vex.pin(root)

    def test_export_builds_the_qualification_target_and_requires_every_file(self):
        calls = []
        def run(command, stage):
            calls.append((command, stage))
            output = Path(command[command.index('--output') + 1].removeprefix('type=local,dest='))
            output.mkdir()
            for name in go_vex.EXPORT:
                (output / name).write_bytes(b'controlled')
        def incomplete(command, stage):
            Path(command[command.index('--output') + 1].removeprefix('type=local,dest=')).mkdir()
        with patch.dict(os.environ, {'ROMD_CONTAINER_ENGINE': 'docker', 'ROMD_GO_READER_BUILDER': 'romd-bounded'}):
            files = go_vex.export(ROOT, self.work, run)
            command, stage = calls[0]
            self.assertEqual('catalog-reader-security-export', stage)
            self.assertEqual(['docker', 'buildx', 'build', '--builder', 'romd-bounded', '--platform', 'linux/amd64',
                              '--target', 'catalog-reader-security-artifacts'], command[:9])
            self.assertEqual(set(go_vex.EXPORT), set(files))
            with self.assertRaisesRegex(ValueError, 'already exists'):
                go_vex.export(ROOT, self.work, run)
            (self.work / 'other').mkdir()
            with self.assertRaisesRegex(ValueError, 'incomplete'):
                go_vex.export(ROOT, self.work / 'other', incomplete)

    def test_unsupported_hosts_and_engines_fail_before_any_command(self):
        def never(*_args):
            self.fail('must fail before running commands')
        with patch.object(go_vex, 'platform', SimpleNamespace(system=lambda: 'Darwin', machine=lambda: 'arm64')):
            with self.assertRaisesRegex(ValueError, 'native Linux amd64'):
                go_vex.export(ROOT, self.work, never)
        with patch.dict(os.environ, {'ROMD_CONTAINER_ENGINE': 'podman'}), self.assertRaisesRegex(ValueError, 'BuildKit'):
            go_vex.export(ROOT, self.work, never)


if __name__ == '__main__':
    unittest.main()
