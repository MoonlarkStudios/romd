#!/usr/bin/env python3
import copy
from datetime import date, datetime, timezone
from email.message import Message
import hashlib
import http.client
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
import urllib.error
import urllib.parse
import urllib.request
import urllib.response

from download_release import DownloadFailure, OfficialRedirects, anonymous_opener, download_release, official_url
from metadata import assert_coverage, assert_fresh_database, assert_scanner_coverage, catalog_supplement, generate, main, merge_sbom, native_supplement
from qualify import image_map, verify_bundle, verify_publisher
from policy import evaluate, validate_policy
from tools import binary_from_archive
from verify import matching_predicate, verify_attestations, verify_files
from credentials import anonymous_environment

HERE = Path(__file__).resolve().parent
DIGEST = 'sha256:' + 'a' * 64


class VulnerabilityPolicyTests(unittest.TestCase):
    def setUp(self):
        self.policy = json.loads((HERE / 'policy.json').read_text())
        self.policy.update(assessments=[], exceptions=[])
        self.today = date(2026, 10, 15)
        self.vulnerability = {'VulnerabilityID': 'CVE-2026-12345', 'PkgName': 'example', 'InstalledVersion': '1.2.3',
                              'Severity': 'HIGH', 'PkgIdentifier': {'PURL': 'pkg:npm/example@1.2.3'}}

    def report(self, severity=None):
        vulnerability = dict(self.vulnerability)
        if severity is not None:
            vulnerability['Severity'] = severity
        return {'SchemaVersion': 2, 'Results': [{'Type': 'npm', 'Vulnerabilities': [vulnerability]}]}

    def exception(self):
        return {'id': 'CVE-2026-12345', 'component': 'pkg:npm/example@1.2.3', 'installedVersion': '1.2.3',
                'subjectDigest': DIGEST, 'rationale': 'Controlled fixture is unreachable from the public route.',
                'owner': 'release-maintainer', 'reviewed': '2026-10-15', 'expires': '2026-11-01', 'severity': 'HIGH',
                'evidence': ['https://example.com/security/CVE-2026-12345'], 'releaseTag': 'v0.1.0-rc.2', 'fixedVersion': None,
                'mitigations': 'Controlled fixture has no network or attacker-controlled input.',
                'followUp': 'Update the dependency when the vendor publishes a fixed version.'}

    def test_high_critical_unknown_and_unfixed_findings_block(self):
        for severity in ('HIGH', 'CRITICAL', 'UNKNOWN'):
            with self.subTest(severity=severity):
                result = evaluate(self.report(severity), DIGEST, self.policy, self.today)
                self.assertEqual('failed', result['result'])
                self.assertIsNone(result['findings'][0]['fixedVersion'])
        self.assertEqual('passed', evaluate(self.report('MEDIUM'), DIGEST, self.policy, self.today)['result'])

    def test_specific_reviewed_exception_does_not_cover_other_component_version_digest_or_cve(self):
        self.policy['exceptions'] = [self.exception()]
        self.assertEqual('passed', evaluate(self.report(), DIGEST, self.policy, self.today, release_tag='v0.1.0-rc.2')['result'])
        for key, replacement in (('component', 'pkg:npm/other@1.2.3'), ('installedVersion', '1.2.4'),
                                 ('subjectDigest', 'sha256:' + 'b' * 64), ('id', 'CVE-2026-54321')):
            policy = copy.deepcopy(self.policy)
            policy['exceptions'][0][key] = replacement
            self.assertEqual('failed', evaluate(self.report(), DIGEST, policy, self.today, release_tag='v0.1.0-rc.2')['result'])

    def test_expired_overlong_wildcard_ownerless_and_weak_policy_are_rejected(self):
        for key, value in (('expires', '2026-10-15'), ('expires', '2027-10-15'),
                           ('component', '*'), ('owner', ''), ('rationale', 'ignore')):
            policy = copy.deepcopy(self.policy)
            policy['exceptions'] = [dict(self.exception(), **{key: value})]
            with self.assertRaises(ValueError):
                validate_policy(policy, self.today)
        self.policy['blockedSeverities'] = ['CRITICAL']
        with self.assertRaises(ValueError):
            validate_policy(self.policy, self.today)

    def test_empty_or_malformed_scanner_report_does_not_pass(self):
        for report in ({}, {'SchemaVersion': 1, 'Results': []}, self.report('unrecognized')):
            with self.assertRaises(ValueError):
                evaluate(report, DIGEST, self.policy, self.today)
        self.assertEqual('passed', evaluate({'SchemaVersion': 2, 'Results': []}, DIGEST, self.policy, self.today)['result'])


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
                member = tarfile.TarInfo(name)
                member.size = len(data)
                archive.addfile(member, io.BytesIO(data))

    def test_generation_rejects_self_consistent_substitution_before_extraction_or_scanners(self):
        bundle = self.work / 'release.tar.gz'
        checksum, images = self.work / 'release.tar.gz.sha256', self.work / 'images.env'
        publisher_state = self.work / 'candidate.json'
        self.release_bundle(bundle)
        with tarfile.open(bundle, 'r:gz') as archive:
            images.write_bytes(archive.extractfile('images.env').read())
        publisher_state.write_text(json.dumps({'sourceCommit': 'b' * 40, 'releaseTag': 'v1.0.0-rc.1',
            'bundleSha256': hashlib.sha256(bundle.read_bytes()).hexdigest(), 'images': image_map(images)}))
        verify_publisher(bundle, publisher_state, images, 'b' * 40, 'v1.0.0-rc.1')
        for index, image_digit in enumerate(('a', 'c')):
            with self.subTest(image_digit=image_digit):
                # Both substitutions are real valid deployment archives. One
                # preserves the publisher's image map; the other rewrites it too.
                self.release_bundle(bundle, image_digit, b'valid-but-substituted-installation-file')
                with tarfile.open(bundle, 'r:gz') as archive:
                    images.write_bytes(archive.extractfile('images.env').read())
                checksum.write_text(hashlib.sha256(bundle.read_bytes()).hexdigest() + '  ' + bundle.name + '\n')
                verify_bundle(bundle, checksum, images, self.work / f'independent-extraction-{index}',
                              'b' * 40, 'v1.0.0-rc.1')
                args = SimpleNamespace(bundle=bundle, checksum=checksum, images=images,
                    publisher_state=publisher_state, revision='b' * 40, tag='v1.0.0-rc.1',
                    tools_dir=self.work / 'tools', output=self.work / f'metadata-{index}', policy=HERE / 'policy.json')
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
                'sha256': digest, 'sbom': sbom.name, 'policy': 'passed-policy.json', 'scans': ['raw-scan.json']})
        policy = json.loads((HERE / 'policy.json').read_text())
        policy.update(assessments=[], exceptions=[])
        for name, value in {'security-policy.json': policy, 'release.vex.json': {},
                            'raw-scan.json': {'SchemaVersion': 2, 'Results': []},
                            'passed-policy.json': {'result': 'passed', 'findings': []}}.items():
            path = self.work / name
            path.write_text(json.dumps(value))
            files[name] = hashlib.sha256(path.read_bytes()).hexdigest()
        index = {'schemaVersion': 1, 'result': 'passed', 'sourceCommit': 'b' * 40, 'releaseTag': 'v1.0.0-rc.1',
                 'files': files, 'subjects': subjects, 'policy': 'security-policy.json', 'vex': 'release.vex.json'}
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

    def test_downloaded_sbom_must_match_cryptographically_verified_predicate(self):
        expected = {'bomFormat': 'CycloneDX', 'components': [{'name': 'react'}]}
        verified = [{'verificationResult': {'statement': {'predicate': expected}}}]
        self.assertTrue(matching_predicate(verified, expected))
        self.assertFalse(matching_predicate(verified, {'bomFormat': 'CycloneDX', 'components': []}))
        self.assertFalse(matching_predicate([], expected))

    def test_attestation_verifier_enforces_signer_source_predicate_and_discards_credentials(self):
        sbom = {'bomFormat': 'CycloneDX', 'components': [{'name': 'controlled'}]}
        (self.work / 'admin.cdx.json').write_text(json.dumps(sbom))
        args = SimpleNamespace(repository='owner/romd', revision='a' * 40, tag='v1.0.0-rc.1',
            signer_workflow='owner/romd/.github/workflows/release.yml', attestations=self.work / 'attestations.jsonl')
        index = {'subjects': [{'kind': 'image', 'name': 'ghcr.io/owner/admin@' + DIGEST, 'sbom': 'admin.cdx.json'}]}
        verified = json.dumps([{'verificationResult': {'statement': {'predicate': sbom}}}])
        with patch.dict('os.environ', {'GH_TOKEN': 'private', 'GITHUB_TOKEN': 'private', 'DOCKER_AUTH_CONFIG': 'private'}):
            with patch('verify.subprocess.run', return_value=subprocess.CompletedProcess([], 0, verified)) as run:
                verify_attestations(self.work, self.work / 'bundle.tar.gz', index, args)
                self.assertEqual(4, run.call_count)
                for call in run.call_args_list:
                    command, environment = call.args[0], call.kwargs['env']
                    self.assertIn('--deny-self-hosted-runners', command)
                    self.assertEqual('a' * 40, command[command.index('--source-digest') + 1])
                    self.assertEqual('refs/tags/v1.0.0-rc.1', command[command.index('--source-ref') + 1])
                    self.assertEqual(args.signer_workflow, command[command.index('--signer-workflow') + 1])
                    self.assertTrue(call.kwargs['check'])
                    self.assertFalse(any(key in environment for key in ('GH_TOKEN', 'GITHUB_TOKEN', 'DOCKER_AUTH_CONFIG')))
        with patch('verify.subprocess.run', side_effect=subprocess.CalledProcessError(1, ['gh'])):
            with self.assertRaises(subprocess.CalledProcessError):
                verify_attestations(self.work, self.work / 'bundle.tar.gz', index, args)

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


class PublicReleaseDownloadTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.work = Path(self.temporary.name)
        self.metadata = self.work / 'trusted-metadata'
        self.metadata.mkdir()
        self.bundle = self.work / 'release.tar.gz'
        InventoryIntegrityTests.release_bundle(self, self.bundle)
        self.attestations = self.work / 'attestations.jsonl'
        self.attestations.write_text('{"controlled":"attestation"}\n')
        Path(str(self.bundle) + '.sha256').write_text(hashlib.sha256(self.bundle.read_bytes()).hexdigest()
                                                    + '  ' + self.bundle.name + '\n')
        self.sbom = self.metadata / 'admin.cdx.json'
        self.sbom.write_text('{"bomFormat":"CycloneDX","components":[]}\n')
        self.index = self.metadata / 'release-metadata.json'
        self.index.write_text(json.dumps({'schemaVersion': 1, 'result': 'passed',
            'files': {self.sbom.name: hashlib.sha256(self.sbom.read_bytes()).hexdigest()}}))
        self.assets = {path.name: path.read_bytes() for path in
                       (self.index, self.sbom, self.bundle, self.attestations, Path(str(self.bundle) + '.sha256'))}
        self.output = self.work / 'public-download'
        self.requests = []
        self.responses = []

    def opener(self, route=None):
        requests, responses, assets = self.requests, self.responses, self.assets

        class MockHTTPS(urllib.request.BaseHandler):
            handler_order = 100

            def https_open(self, request):
                requests.append(request)
                name = urllib.parse.urlsplit(request.full_url).path.rsplit('/', 1)[-1]
                headers, code, body = {'Content-Length': str(len(assets.get(name, b'')))}, 200, assets.get(name, b'')
                if route is not None:
                    code, headers, body = route(request, name, body)
                message = Message()
                for key, value in headers.items():
                    message[key] = value
                response = urllib.response.addinfourl(io.BytesIO(body), message, request.full_url, code)
                response.msg = 'controlled response'
                responses.append(response)
                return response

        opener = anonymous_opener()
        opener.add_handler(MockHTTPS())
        return opener

    def run_download(self, opener=None):
        with patch('download_release.anonymous_opener', return_value=opener or self.opener()):
            passed = download_release('owner/repository', 'v1.0.0-rc.1', self.metadata, self.bundle,
                                      self.attestations, self.output)
        return passed, json.loads((self.output / 'download-result.json').read_text())

    def test_anonymous_download_matches_every_trusted_asset_and_records_hash_manifest(self):
        passed, report = self.run_download()
        self.assertTrue(passed)
        self.assertEqual('passed', report['result'])
        self.assertEqual(set(self.assets), {record['name'] for record in report['files']})
        for record in report['files']:
            self.assertEqual(self.assets[record['name']], (self.output / record['name']).read_bytes())
            self.assertEqual(record['expectedSize'], record['actualSize'])
            self.assertEqual(record['expectedSha256'], record['actualSha256'])
            self.assertEqual('passed', record['result'])
        self.assertEqual(set(self.assets) | {'download-result.json'}, {path.name for path in self.output.iterdir()})

    def test_private_missing_release_and_network_errors_fail_with_sanitized_evidence(self):
        secret_url = 'https://release-assets.githubusercontent.com/private?token=must-not-appear'

        def missing(request, name, body):
            return 404, {}, secret_url.encode()

        def network_failure(request, name, body):
            raise urllib.error.URLError(secret_url)

        def incomplete_http(request, name, body):
            raise http.client.IncompleteRead(secret_url.encode(), 999)

        for index, (route, failure) in enumerate(((missing, 'http-error'), (network_failure, 'network-error'),
                                                 (incomplete_http, 'network-error'))):
            self.output = self.work / f'failed-download-{index}'
            passed, report = self.run_download(self.opener(route))
            self.assertFalse(passed)
            self.assertEqual(failure, report['failure'])
            self.assertNotIn('must-not-appear', json.dumps(report))
            self.assertEqual({'download-result.json'}, {path.name for path in self.output.iterdir()})
            if failure == 'http-error':
                self.assertTrue(self.responses[-1].closed)

    def test_altered_oversized_and_truncated_asset_bytes_fail_and_remove_partial_content(self):
        routes = [
            (lambda request, name, body: (200, {}, b'X' + body[1:]), 'download-checksum-mismatch'),
            (lambda request, name, body: (200, {'Content-Length': str(len(body) + 1)}, body + b'X'), 'http-content-length-mismatch'),
            (lambda request, name, body: (200, {}, body + b'X' * 100000), 'download-size-mismatch'),
            (lambda request, name, body: (200, {}, body[:-1]), 'download-size-mismatch'),
        ]
        for index, (route, failure) in enumerate(routes):
            with self.subTest(failure=failure, index=index):
                self.output = self.work / f'bad-content-{index}'
                passed, report = self.run_download(self.opener(route))
                self.assertFalse(passed)
                self.assertEqual(failure, report['failure'])
                self.assertEqual({'download-result.json'}, {path.name for path in self.output.iterdir()})
                if index == 2:
                    self.assertEqual(report['files'][0]['expectedSize'] + 1, report['files'][0]['actualSize'])

    def test_official_https_redirects_work_without_environment_auth_proxy_cookie_or_netrc(self):
        inherited = {'HOME': str(self.work), 'GH_TOKEN': 'caller-secret', 'GITHUB_TOKEN': 'caller-secret',
                     'HTTP_PROXY': 'http://user:caller-secret@attacker.test:8080',
                     'HTTPS_PROXY': 'http://user:caller-secret@attacker.test:8080',
                     'ALL_PROXY': 'http://user:caller-secret@attacker.test:8080',
                     'NETRC': str(self.work / '.netrc')}
        (self.work / '.netrc').write_text('machine github.com login user password caller-secret\n')

        def redirect(request, name, body):
            host = urllib.parse.urlsplit(request.full_url).hostname
            if host == 'github.com':
                return 302, {'Location': 'https://release-assets.githubusercontent.com/' + name + '?token=signed-url-secret'}, b''
            if host == 'release-assets.githubusercontent.com':
                return 307, {'Location': 'https://objects.githubusercontent.com/' + name}, b''
            return 200, {'Content-Length': str(len(body))}, body

        with patch.dict('os.environ', inherited), patch('netrc.netrc', side_effect=AssertionError('netrc was used')):
            opener = self.opener(redirect)
            self.assertFalse(any(isinstance(handler, (urllib.request.ProxyHandler, urllib.request.HTTPBasicAuthHandler,
                urllib.request.HTTPDigestAuthHandler, urllib.request.HTTPCookieProcessor)) for handler in opener.handlers))
            passed, report = self.run_download(opener)
        self.assertTrue(passed)
        self.assertEqual(3 * len(self.assets), len(self.requests))
        for request in self.requests:
            headers = {key.lower(): value for key, value in request.header_items()}
            self.assertFalse({'authorization', 'proxy-authorization', 'cookie'} & headers.keys())
            self.assertNotIn('caller-secret', json.dumps(headers))
            self.assertIn(urllib.parse.urlsplit(request.full_url).hostname,
                          ('github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com'))
        self.assertNotIn('signed-url-secret', json.dumps(report))

    def test_untrusted_redirect_hosts_schemes_userinfo_and_ports_are_rejected_before_contact(self):
        locations = ('http://github.com/asset', 'https://attacker.test/asset',
                     'https://github.com.attacker.test/asset', 'https://user:secret@github.com/asset',
                     'https://github.com:444/asset', 'https://github.com/asset#fragment',
                     'file:///private/asset', 'https://objects.githubusercontent.com:invalid/asset')
        for index, location in enumerate(locations):
            with self.subTest(location=location):
                self.output = self.work / f'unsafe-redirect-{index}'
                self.requests.clear()
                route = lambda request, name, body: (302, {'Location': location}, b'')
                passed, report = self.run_download(self.opener(route))
                self.assertFalse(passed)
                self.assertEqual('http-error' if location.startswith('file:') else 'unsafe-release-url', report['failure'])
                self.assertEqual(1, len(self.requests))
                self.assertTrue(self.responses[-1].closed)
                self.assertNotIn(location, json.dumps(report))
        for location in locations:
            with self.assertRaises(DownloadFailure):
                official_url(location)

    def test_redirects_discard_explicit_authorization_and_cookie_headers(self):
        request = urllib.request.Request('https://github.com/source', headers={
            'Authorization': 'Bearer secret', 'Proxy-Authorization': 'Basic secret', 'Cookie': 'secret'})
        redirected = OfficialRedirects().redirect_request(request, None, 302, '', {},
                                                         'https://release-assets.githubusercontent.com/target')
        self.assertFalse({'authorization', 'proxy-authorization', 'cookie'} &
                         {key.lower() for key, value in redirected.header_items()})

    def test_existing_output_directory_is_preserved_and_not_reused(self):
        self.output.mkdir()
        (self.output / 'keep.txt').write_text('existing evidence')
        with self.assertRaises(FileExistsError):
            self.run_download()
        self.assertEqual(['keep.txt'], [path.name for path in self.output.iterdir()])
        self.assertEqual('existing evidence', (self.output / 'keep.txt').read_text())
        self.assertEqual([], self.requests)

    def test_unsafe_duplicate_reserved_symlink_and_mismatched_trusted_files_fail_before_network(self):
        original = self.index.read_text()
        for index, name in enumerate(('../outside.json', '/absolute.json', 'back\\slash.json', '.env',
                                      'release-metadata.json', 'download-result.json', 'attestations.jsonl')):
            with self.subTest(name=name):
                self.output = self.work / f'unsafe-name-{index}'
                self.index.write_text(json.dumps({'schemaVersion': 1, 'result': 'passed',
                    'files': {name: hashlib.sha256(self.index.read_bytes()).hexdigest()}}))
                passed, report = self.run_download()
                self.assertFalse(passed)
                self.assertEqual([], self.requests)
                self.assertEqual({'download-result.json'}, {path.name for path in self.output.iterdir()})
        self.index.write_text(original)
        self.sbom.unlink()
        self.sbom.symlink_to(self.attestations)
        self.output = self.work / 'symlink-input'
        passed, report = self.run_download()
        self.assertFalse(passed)
        self.assertEqual('trusted-asset-not-regular-file', report['failure'])
        self.assertEqual([], self.requests)
        self.sbom.unlink()
        self.sbom.write_text('changed trusted metadata')
        self.output = self.work / 'changed-trusted-input'
        passed, report = self.run_download()
        self.assertFalse(passed)
        self.assertEqual('trusted-metadata-checksum-mismatch', report['failure'])
        self.assertEqual([], self.requests)


if __name__ == '__main__':
    unittest.main()
