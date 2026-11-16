#!/usr/bin/env python3
import copy
from datetime import datetime, timezone
import hashlib
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import subprocess

from metadata import (Commands, assert_coverage, assert_fresh_database, assert_official_postgres, assert_scanner_coverage,
                      catalog_supplement, generate, main, merge_sbom, native_supplement)
from qualify import image_map, rehearsal_images, verify_bundle, verify_publisher
from policy import GATE_EXIT_CODE, IGNORE_FILE, gate_command, load_exceptions, suppressed
from tools import binary_from_archive
from verify import verify_files
from credentials import anonymous_environment

HERE = Path(__file__).resolve().parent
DIGEST = 'sha256:' + 'a' * 64


class VulnerabilityPolicyTests(unittest.TestCase):
    def test_rehearsal_scan_sources_cannot_escape_owned_roles_registry_or_official_database(self):
        pins = json.loads((HERE.parent / 'deployment/base-images.json').read_text())
        references = {f'ROMD_{role.upper()}_IMAGE':
            f'127.0.0.1:51234/romd-rehearsal-abcd/romd-{role}@{DIGEST}'
            for role in ('admin', 'consumer', 'worker', 'player')}
        references['ROMD_POSTGRES_IMAGE'] = pins['postgres']
        rehearsal_images(references)
        for changed in (
                dict(references, ROMD_ADMIN_IMAGE='ghcr.io/example/admin@' + DIGEST),
                dict(references, ROMD_ADMIN_IMAGE=references['ROMD_WORKER_IMAGE']),
                dict(references, ROMD_ADMIN_IMAGE=references['ROMD_ADMIN_IMAGE'].replace(':51234/', ':51235/')),
                dict(references, ROMD_ADMIN_IMAGE=references['ROMD_ADMIN_IMAGE'].replace(':51234/', ':51234@evil.test/')),
                dict(references, ROMD_POSTGRES_IMAGE='example/postgres@' + DIGEST),
                {key: value for key, value in references.items() if key != 'ROMD_WORKER_IMAGE'}):
            with self.subTest(references=changed), self.assertRaises(ValueError):
                rehearsal_images(changed)

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.now = datetime(2026, 10, 15, tzinfo=timezone.utc)

    def exceptions(self, *entries):
        path = self.work / 'trivyignore.yaml'
        path.write_text(json.dumps({'vulnerabilities': list(entries)}))
        return load_exceptions(path, self.now)

    def exception(self, **changes):
        return dict({'id': 'CVE-2026-12345', 'purls': ['pkg:npm/example@1.2.3'],
                     'statement': 'Controlled fixture is unreachable from the public route.',
                     'owner': 'release-maintainer', 'expired_at': '2026-11-01T00:00:00Z'}, **changes)

    def test_reviewed_exception_file_is_valid_and_empty_by_default(self):
        self.assertEqual([], load_exceptions(IGNORE_FILE))

    def test_exact_owned_exception_within_ninety_days_is_accepted_in_trivys_format(self):
        self.assertEqual([self.exception()], self.exceptions(self.exception()))
        self.exceptions(self.exception(expired_at='2027-01-13T00:00:00Z'))

    def test_expired_overlong_wildcard_unversioned_ownerless_or_broad_exceptions_are_rejected(self):
        for changes in ({'expired_at': '2026-10-15T00:00:00Z'}, {'expired_at': '2027-01-14T00:00:00Z'},
                        {'expired_at': '2026-11-01'}, {'purls': ['pkg:npm/example@*']}, {'purls': ['pkg:npm/example']},
                        {'purls': []}, {'id': '*'}, {'owner': ' '}, {'statement': 'ignore'}, {'paths': ['app/']}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                self.exceptions(self.exception(**changes))
        entry = self.exception()
        del entry['owner']
        for entries in ((entry,), (self.exception(), self.exception())):
            with self.subTest(entries=entries), self.assertRaises(ValueError):
                self.exceptions(*entries)
        for text in ('vulnerabilities: []\n', json.dumps({'vulnerabilities': [], 'misconfigurations': []}), '[]'):
            (self.work / 'trivyignore.yaml').write_text(text)
            with self.subTest(text=text), self.assertRaises(ValueError):
                load_exceptions(self.work / 'trivyignore.yaml', self.now)

    def test_gate_blocks_unknown_high_and_critical_findings_including_unfixed_ones(self):
        command = gate_command('trivy', 'admin.image.trivy.json', 'empty-config.json')
        self.assertEqual(['trivy', 'convert'], command[:2])
        self.assertEqual('UNKNOWN,HIGH,CRITICAL', command[command.index('--severity') + 1])
        self.assertEqual(str(GATE_EXIT_CODE), command[command.index('--exit-code') + 1])
        self.assertNotEqual(1, GATE_EXIT_CODE)
        self.assertNotIn('--ignore-unfixed', command)
        source = (HERE / 'metadata.py').read_text()
        self.assertNotIn('--ignore-unfixed', source)
        self.assertNotIn("'--severity'", source)

    def test_gate_separates_blocked_findings_from_scanner_failures(self):
        commands = Commands(self.work, self.work)
        for returncode, expected in ((0, True), (GATE_EXIT_CODE, False)):
            with self.subTest(returncode=returncode), patch('metadata.subprocess.run',
                    return_value=subprocess.CompletedProcess([], returncode, b'', b'')):
                self.assertIs(expected, commands.gate(['trivy', 'convert'], 'vulnerability-gate-admin-image'))
        with patch('metadata.subprocess.run', return_value=subprocess.CompletedProcess([], 1, b'', b'')), \
                self.assertRaises(RuntimeError):
            commands.gate(['trivy', 'convert'], 'vulnerability-gate-admin-image')

    def test_suppressed_findings_are_recorded_with_their_reason(self):
        finding = {'VulnerabilityID': 'GO-2026-5932', 'Severity': 'UNKNOWN',
                   'PkgIdentifier': {'PURL': 'pkg:golang/golang.org/x/crypto@v0.58.0'}}
        report = {'Results': [{'Vulnerabilities': [{'VulnerabilityID': 'CVE-2026-1'}],
                               'ExperimentalModifiedFindings': [{'Status': 'not_affected', 'Finding': finding,
                                                                 'Statement': 'vulnerable_code_not_present'}]}]}
        self.assertEqual([{'id': 'GO-2026-5932', 'severity': 'UNKNOWN', 'purl': 'pkg:golang/golang.org/x/crypto@v0.58.0',
                           'status': 'not_affected', 'statement': 'vulnerable_code_not_present'}], suppressed(report))
        self.assertEqual([], suppressed({'Results': [{}]}))

    def test_upstream_reporting_applies_only_to_the_docker_official_postgres_image(self):
        assert_official_postgres('postgres:18.7-bookworm@' + DIGEST)
        for reference in ('ghcr.io/example/postgres@' + DIGEST, 'docker.io/example/postgres@' + DIGEST):
            with self.subTest(reference=reference), self.assertRaisesRegex(ValueError, 'Docker Official'):
                assert_official_postgres(reference)


class InventoryIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.work = Path(self.temporary.name)

    def release_bundle(self, path, image_digit='a', content=b'controlled-installation-file'):
        images = {f'ROMD_{role.upper()}_IMAGE': f'ghcr.io/example/{role}@sha256:' + image_digit * 64
                  for role in ('admin', 'consumer', 'worker', 'player', 'postgres')}
        files = {name: content for name in ('compose.yaml', '.env.example',
            'scripts/deployment/compose.sh', 'scripts/deployment/validate.sh', 'scripts/deployment/verify.sh',
            'scripts/backup/backup.sh', 'scripts/backup/restore.sh', 'deploy/examples/https/compose.yaml')}
        files['images.env'] = ''.join(f'{key}={value}\n' for key, value in images.items()).encode()
        manifest = {'schemaVersion': 1, 'sourceCommit': 'b' * 40, 'releaseTag': 'v1.0.0-rc.1',
                    'platform': 'linux/amd64', 'images': images,
                    'files': {name: hashlib.sha256(data).hexdigest() for name, data in files.items()}}
        files['manifest.json'] = json.dumps(manifest).encode()
        with tarfile.open(path, 'w:gz') as archive:
            for name, data in files.items():
                member = tarfile.TarInfo('romd-v1.0.0-rc.1/' + name)
                member.size = len(data)
                archive.addfile(member, io.BytesIO(data))

    def test_generation_rejects_self_consistent_substitution_before_extraction_or_scanners(self):
        bundle = self.work / 'release.tar.gz'
        checksum, images = self.work / 'release.tar.gz.sha256', self.work / 'images.env'
        publisher_state = self.work / 'candidate.json'
        self.release_bundle(bundle)
        with tarfile.open(bundle, 'r:gz') as archive:
            images.write_bytes(archive.extractfile('romd-v1.0.0-rc.1/images.env').read())
        publisher_state.write_text(json.dumps({'sourceCommit': 'b' * 40, 'releaseTag': 'v1.0.0-rc.1',
            'bundleSha256': hashlib.sha256(bundle.read_bytes()).hexdigest(), 'images': image_map(images)}))
        verify_publisher(bundle, publisher_state, images, 'b' * 40, 'v1.0.0-rc.1')
        for index, image_digit in enumerate(('a', 'c')):
            with self.subTest(image_digit=image_digit):
                # Both substitutions are real valid deployment archives. One
                # preserves the publisher's image map; the other rewrites it too.
                self.release_bundle(bundle, image_digit, b'valid-but-substituted-installation-file')
                with tarfile.open(bundle, 'r:gz') as archive:
                    images.write_bytes(archive.extractfile('romd-v1.0.0-rc.1/images.env').read())
                checksum.write_text(hashlib.sha256(bundle.read_bytes()).hexdigest() + '  ' + bundle.name + '\n')
                verify_bundle(bundle, checksum, images, self.work / f'independent-extraction-{index}',
                              'b' * 40, 'v1.0.0-rc.1')
                args = SimpleNamespace(bundle=bundle, checksum=checksum, images=images,
                    publisher_state=publisher_state, revision='b' * 40, tag='v1.0.0-rc.1',
                    tools_dir=self.work / 'tools', output=self.work / f'metadata-{index}', ignore_file=IGNORE_FILE)
                with patch('metadata.platform.system', return_value='Linux'), \
                        patch('metadata.platform.machine', return_value='x86_64'), \
                        patch('metadata.verify_installed', return_value={}), \
                        patch('metadata.verify_bundle', wraps=verify_bundle) as extraction, \
                        patch('metadata.Commands') as commands, patch('metadata.subprocess.run') as subprocess_run:
                    self.assertFalse(generate(args))
                    extraction.assert_not_called()
                    commands.assert_not_called()
                    subprocess_run.assert_not_called()
                report = json.loads((args.output / 'release-metadata.json').read_text())
                self.assertEqual('failed', report['result'])
                self.assertIn('independent publisher subject map', report['failure'])
                self.assertEqual([], report['subjects'])
                self.assertEqual([], report['commands'])

    def test_generation_cli_requires_independent_publisher_state(self):
        arguments = ['metadata.py', '--bundle', 'bundle.tar.gz', '--checksum', 'bundle.sha256',
                     '--images', 'images.env', '--tools-dir', 'tools', '--output', 'output',
                     '--revision', 'b' * 40, '--tag', 'v1.0.0-rc.1']
        with patch.object(sys, 'argv', arguments), patch('metadata.generate') as generation, \
                patch('sys.stderr', new_callable=io.StringIO) as errors:
            with self.assertRaises(SystemExit) as failure:
                main()
        self.assertEqual(2, failure.exception.code)
        self.assertIn('--publisher-state', errors.getvalue())
        generation.assert_not_called()

    def test_pinned_scanner_archive_rejects_tamper_and_wrong_architecture(self):
        binary = bytearray(64)
        binary[:4], binary[18:20] = b'\x7fELF', b'\x3e\x00'
        archive = self.work / 'syft.tar.gz'
        with tarfile.open(archive, 'w:gz') as bundle:
            member = tarfile.TarInfo('syft')
            member.size = len(binary)
            bundle.addfile(member, io.BytesIO(binary))
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        self.assertEqual(binary, binary_from_archive(archive, 'syft', digest))
        with self.assertRaisesRegex(ValueError, 'checksum'):
            binary_from_archive(archive, 'syft', 'f' * 64)
        with self.assertRaisesRegex(ValueError, 'one regular'):
            binary_from_archive(archive, 'trivy', digest)
        binary[18:20] = b'\xb7\x00'
        with tarfile.open(archive, 'w:gz') as bundle:
            member = tarfile.TarInfo('syft')
            member.size = len(binary)
            bundle.addfile(member, io.BytesIO(binary))
        with self.assertRaisesRegex(ValueError, 'not Linux amd64'):
            binary_from_archive(archive, 'syft', hashlib.sha256(archive.read_bytes()).hexdigest())

    def test_tool_installer_accepts_only_pinned_tools_and_pins_crane(self):
        import tools
        pins = json.loads((HERE / 'tools.json').read_text())
        self.assertEqual({'syft', 'trivy', 'crane'}, set(tools.TOOLS))
        self.assertTrue(pins['crane']['url'].startswith('https://github.com/google/go-containerregistry/releases/download/v'))
        for selection in ('curl', 'crane,crane', 'crane,', ''):
            arguments = ['tools.py', '--output', str(self.work / 'tools'), '--tools', selection]
            with self.subTest(selection=selection), patch.object(sys, 'argv', arguments), \
                    patch('tools.urllib.request.urlopen', side_effect=AssertionError('no download')), \
                    patch('sys.stderr', new_callable=io.StringIO), self.assertRaises(SystemExit) as failure:
                tools.main()
            self.assertEqual(2, failure.exception.code)
        self.assertFalse((self.work / 'tools').exists())

    def test_native_inventory_comes_from_verified_packaged_binary_and_receipt(self):
        binary = self.work / 'libmoonlark_chdr.so'
        binary.write_bytes(b'controlled-native-binary')
        manifest = self.work / 'build-manifest.json'
        manifest.write_text(json.dumps({'sha256': hashlib.sha256(binary.read_bytes()).hexdigest(),
            'upstreamVersion': 'v0.3.0', 'upstreamCommit': 'b' * 40, 'locations': {'sourceRoot': '/private/builder'}}))
        component = native_supplement('libchdr', manifest, binary)['components'][0]
        self.assertEqual('libchdr', component['name'])
        self.assertNotIn('/private/builder', json.dumps(component))
        binary.write_bytes(b'tampered')
        with self.assertRaisesRegex(ValueError, 'does not match'):
            native_supplement('libchdr', manifest, binary)

    def test_coverage_refuses_missing_native_catalog_frontend_or_os_components(self):
        components = [
            {'name': 'base-files', 'purl': 'pkg:deb/ubuntu/base-files@1'},
            {'name': 'Npgsql', 'purl': 'pkg:nuget/Npgsql@10.0.0'},
            {'name': 'Microsoft.EntityFrameworkCore', 'purl': 'pkg:nuget/Microsoft.EntityFrameworkCore@10.0.0'},
            {'name': 'github.com/MoonlarkStudios/romd-dat-catalogs', 'purl': 'pkg:golang/github.com/MoonlarkStudios/romd-dat-catalogs@v0.0.0'},
            *({'name': name} for name in ('libchdr', 'react', '@mantine/core', 'oidc-client-ts')),
        ]
        self.assertTrue(all(assert_coverage({'components': components}, 'admin').values()))
        for removed in ('base-files', 'Npgsql', 'github.com/MoonlarkStudios/romd-dat-catalogs', 'libchdr', 'react'):
            with self.assertRaises(ValueError):
                assert_coverage({'components': [component for component in components if component['name'] != removed]}, 'admin')

    def test_merge_preserves_original_graph_and_binds_final_subject(self):
        original = {'bomFormat': 'CycloneDX', 'components': [{'name': 'Npgsql', 'bom-ref': 'original'}],
                    'dependencies': [{'ref': 'original', 'dependsOn': []}]}
        merged = merge_sbom(original, [{'bomFormat': 'CycloneDX', 'components': [{'name': 'react', 'purl': 'pkg:npm/react@19.2.3'}]}],
                            'ghcr.io/example/admin@' + DIGEST, 'a' * 64)
        self.assertEqual(original['dependencies'], merged['dependencies'][:1])
        self.assertEqual('romd:supplement:pkg:npm/react@19.2.3', merged['components'][1]['bom-ref'])
        self.assertEqual('a' * 64, merged['metadata']['component']['hashes'][0]['content'])
        self.assertEqual(1, len(original['components']))

    def test_package_free_bundle_omits_optional_components_but_keeps_every_image(self):
        original = {'bomFormat': 'CycloneDX', 'specVersion': '1.7'}
        images = [dict(type='container', name=role, version=DIGEST,
                       purl='pkg:oci/' + role + '@' + DIGEST,
                       hashes=[dict(alg='SHA-256', content=DIGEST.removeprefix('sha256:'))])
                  for role in ('admin', 'consumer', 'worker', 'player', 'postgres')]
        merged = merge_sbom(original, [dict(bomFormat='CycloneDX', components=images)], 'bundle.tar.gz', 'a' * 64)
        self.assertEqual(images, [{key: value for key, value in component.items() if key != 'bom-ref'}
                                  for component in merged['components']])
        self.assertEqual(5, len(merged['dependencies'][-1]['dependsOn']))
        self.assertNotIn('components', original)
        for malformed in (None, {}, 'not an inventory'):
            with self.subTest(malformed=malformed), self.assertRaises(ValueError):
                merge_sbom(dict(original, components=malformed), [], 'bundle.tar.gz', 'a' * 64)
        with self.assertRaisesRegex(ValueError, 'nonempty'):
            merge_sbom(original, [], 'bundle.tar.gz', 'a' * 64)
        with self.assertRaisesRegex(ValueError, 'coverage'):
            assert_coverage(merged, 'worker')

    def test_catalog_reader_receipt_binds_companion_commit_to_actual_packaged_binary(self):
        binary, receipt = self.work / 'catalog-reader', self.work / 'catalog-reader.json'
        binary.write_bytes(b'controlled-catalog-reader')
        receipt.write_text(json.dumps({'schemaVersion': 1, 'module': 'github.com/MoonlarkStudios/romd-dat-catalogs',
            'sourceCommit': 'f' * 40, 'binarySha256': hashlib.sha256(binary.read_bytes()).hexdigest()}))
        supplement = catalog_supplement(receipt, binary)
        self.assertTrue(supplement['components'][0]['purl'].endswith('@' + 'f' * 40))
        binary.write_bytes(b'tampered')
        with self.assertRaisesRegex(ValueError, 'does not match'):
            catalog_supplement(receipt, binary)

    def test_stale_or_future_vulnerability_database_is_rejected(self):
        now = datetime(2026, 10, 15, tzinfo=timezone.utc)
        assert_fresh_database({'VulnerabilityDB': {'UpdatedAt': '2026-10-14T00:00:00Z'}}, now)
        for updated in ('2026-10-01T00:00:00Z', '2026-10-20T00:00:00Z', '2026-10-14T00:00:00'):
            with self.assertRaises(ValueError):
                assert_fresh_database({'VulnerabilityDB': {'UpdatedAt': updated}}, now)

    def test_empty_or_missing_ecosystem_scanner_inventory_cannot_report_a_false_green(self):
        report = {'Results': [{'Class': 'os-pkgs', 'Packages': [{'Name': 'libc6'}]},
                              {'Class': 'lang-pkgs', 'Packages': [{'Name': name} for name in
                               ('Npgsql', 'Microsoft.EntityFrameworkCore', 'react', '@mantine/core', 'oidc-client-ts',
                                'github.com/sigstore/sigstore', 'github.com/theupdateframework/go-tuf/v2')]}]}
        assert_scanner_coverage(report, 'admin', 'image')
        assert_scanner_coverage(report, 'admin', 'sbom')
        with self.assertRaisesRegex(ValueError, 'OS packages'):
            assert_scanner_coverage({'Results': []}, 'admin', 'image')
        for name in ('Npgsql', 'react'):
            changed = copy.deepcopy(report)
            changed['Results'][1]['Packages'] = [item for item in changed['Results'][1]['Packages'] if item['Name'] != name]
            with self.assertRaises(ValueError):
                assert_scanner_coverage(changed, 'admin', 'sbom')

    def test_postgres_inventory_requires_the_supported_database_server(self):
        os_package = {'name': 'busybox', 'purl': 'pkg:apk/alpine/busybox@1.37.0'}
        for server in (None, {'name': 'postgresql'},
                       {'name': 'postgresql', 'version': '17.8', 'purl': 'pkg:generic/postgresql@17.8'},
                       {'name': 'postgresql', 'version': '18', 'purl': 'pkg:deb/debian/postgresql@18'},
                       {'name': 'postgresql-client-18', 'version': '18.6-1.pgdg12+2',
                        'purl': 'pkg:deb/debian/postgresql-client-18@18.6-1.pgdg12%2B2'},
                       {'name': 'postgresql-18', 'version': '18.6-1.pgdg12+2',
                        'purl': 'pkg:deb/debian/postgresql-17@18.6-1.pgdg12%2B2'}):
            with self.subTest(server=server), self.assertRaisesRegex(ValueError, 'database-server'):
                assert_coverage({'components': [os_package] + ([server] if server else [])}, 'postgres')
        for server in ({'name': 'postgresql', 'version': '18.6', 'purl': 'pkg:generic/postgresql@18.6'},
                       {'name': 'postgresql-18', 'version': '18.6-1.pgdg12+2',
                        'purl': 'pkg:deb/debian/postgresql-18@18.6-1.pgdg12%2B2?arch=amd64&distro=debian-12.15'}):
            self.assertTrue(all(assert_coverage({'components': [os_package, server]}, 'postgres').values()))

    def test_player_and_postgres_sbom_scans_reject_empty_and_os_only_frontend_results(self):
        for role in ('player', 'postgres'):
            with self.subTest(role=role), self.assertRaisesRegex(ValueError, 'OS packages'):
                assert_scanner_coverage({'Results': []}, role, 'sbom')
        os_only = {'Results': [{'Class': 'os-pkgs', 'Packages': [{'Name': 'busybox'}]}]}
        assert_scanner_coverage(os_only, 'postgres', 'sbom')
        with self.assertRaisesRegex(ValueError, 'frontend dependency closure'):
            assert_scanner_coverage(os_only, 'player', 'sbom')

    def test_catalog_receipt_identity_cannot_replace_scanning_linked_go_dependencies(self):
        scan = {'Results': [{'Class': 'os-pkgs', 'Packages': [{'Name': 'libc6'}]},
                           {'Class': 'lang-pkgs', 'Packages': [{'Name': name} for name in
                            ('Npgsql', 'Microsoft.EntityFrameworkCore', 'react', '@mantine/core', 'oidc-client-ts',
                             'github.com/MoonlarkStudios/romd-dat-catalogs')]}]}
        for role in ('admin', 'worker'):
            for mode in ('image', 'sbom'):
                with self.subTest(role=role, mode=mode), self.assertRaisesRegex(ValueError, 'linked Go trust dependencies'):
                    assert_scanner_coverage(scan, role, mode)

    def test_downloaded_metadata_and_bundle_tamper_fail_before_attestation(self):
        bundle = self.work / 'release.tar.gz'
        self.release_bundle(bundle)
        original_bundle = bundle.read_bytes()
        bundle_digest = hashlib.sha256(bundle.read_bytes()).hexdigest()
        subjects, files = [], {}
        for role in ('admin', 'consumer', 'worker', 'player', 'postgres', 'bundle'):
            digest = bundle_digest if role == 'bundle' else 'a' * 64
            sbom = self.work / f'{role}.cdx.json'
            sbom.write_text(json.dumps({'metadata': {'component': {'hashes': [{'alg': 'SHA-256', 'content': digest}]}}}))
            files[sbom.name] = hashlib.sha256(sbom.read_bytes()).hexdigest()
            subjects.append({'kind': 'bundle' if role == 'bundle' else 'image', 'role': role,
                'name': bundle.name if role == 'bundle' else f'ghcr.io/example/{role}@sha256:{digest}',
                'sha256': digest, 'sbom': sbom.name})
        index = {'schemaVersion': 1, 'result': 'passed', 'sourceCommit': 'b' * 40, 'releaseTag': 'v1.0.0-rc.1', 'files': files, 'subjects': subjects}
        (self.work / 'release-metadata.json').write_text(json.dumps(index))
        verify_files(self.work, bundle, 'b' * 40, 'v1.0.0-rc.1')
        bundle.write_bytes(b'tampered')
        with self.assertRaisesRegex(ValueError, 'bundle checksum'):
            verify_files(self.work, bundle, 'b' * 40, 'v1.0.0-rc.1')
        bundle.write_bytes(original_bundle)
        # Every downloaded checksum can be internally consistent and every
        # provenance signer valid, while the bundle selects different images.
        self.release_bundle(bundle, image_digit='b')
        new_digest = hashlib.sha256(bundle.read_bytes()).hexdigest()
        index['subjects'][-1]['sha256'] = new_digest
        (self.work / 'bundle.cdx.json').write_text(json.dumps({'metadata': {'component': {
            'hashes': [{'alg': 'SHA-256', 'content': new_digest}]}}}))
        index['files']['bundle.cdx.json'] = hashlib.sha256((self.work / 'bundle.cdx.json').read_bytes()).hexdigest()
        (self.work / 'release-metadata.json').write_text(json.dumps(index))
        with self.assertRaisesRegex(ValueError, 'published image map mismatch'):
            verify_files(self.work, bundle, 'b' * 40, 'v1.0.0-rc.1')
        bundle.write_bytes(original_bundle)
        index['subjects'][-1]['sha256'] = bundle_digest
        (self.work / 'bundle.cdx.json').write_text(json.dumps({'metadata': {'component': {
            'hashes': [{'alg': 'SHA-256', 'content': bundle_digest}]}}}))
        index['files']['bundle.cdx.json'] = hashlib.sha256((self.work / 'bundle.cdx.json').read_bytes()).hexdigest()
        (self.work / 'release-metadata.json').write_text(json.dumps(index))
        (self.work / 'admin.cdx.json').write_text('{}')
        with self.assertRaisesRegex(ValueError, 'file checksum'):
            verify_files(self.work, bundle, 'b' * 40, 'v1.0.0-rc.1')

    def test_anonymous_environment_excludes_home_cloud_tokens_and_podman_auth_fallback(self):
        inherited = {'HOME': '/private/caller-home', 'REGISTRY_AUTH_FILE': '/private/podman/auth.json',
            'XDG_RUNTIME_DIR': '/run/user/501', 'XDG_CONFIG_HOME': '/private/caller-config',
            'DOCKER_CONFIG': '/private/caller-docker', 'GITHUB_TOKEN': 'caller-token',
            'TRIVY_TOKEN': 'caller-token', 'SYFT_REGISTRY_AUTH_PASSWORD': 'caller-token',
            'AWS_ACCESS_KEY_ID': 'caller-token', 'AZURE_CLIENT_SECRET': 'caller-token',
            'GOOGLE_APPLICATION_CREDENTIALS': '/private/google-creds.json',
            'CONTAINERS_AUTH_FILE': '/private/other-auth.json'}
        with patch.dict('os.environ', inherited):
            environment = anonymous_environment(self.work / 'clean')
        self.assertNotIn('HOME', environment)
        for key in ('GITHUB_TOKEN', 'TRIVY_TOKEN', 'SYFT_REGISTRY_AUTH_PASSWORD', 'AWS_ACCESS_KEY_ID',
                    'AZURE_CLIENT_SECRET', 'GOOGLE_APPLICATION_CREDENTIALS', 'CONTAINERS_AUTH_FILE'):
            self.assertNotIn(key, environment)
        self.assertFalse(any(value in environment.values() for value in inherited.values()))
        self.assertEqual({'auths': {}}, json.loads((Path(environment['DOCKER_CONFIG']) / 'config.json').read_text()))
        self.assertEqual({'auths': {}}, json.loads(Path(environment['REGISTRY_AUTH_FILE']).read_text()))
        self.assertEqual({'auths': {}}, json.loads((Path(environment['XDG_RUNTIME_DIR']) / 'containers/auth.json').read_text()))


if __name__ == '__main__':
    unittest.main()
