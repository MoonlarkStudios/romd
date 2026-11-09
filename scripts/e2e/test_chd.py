#!/usr/bin/env python3
"""Docker-free tests for the CHD end-to-end harness: fail-closed configuration, path safety, hashing,
fixture/DAT/reference matching, checkpoint detection, custody release and generated inputs."""
import contextlib
import fcntl
import hashlib
import io
import ipaddress
import json
import os
from pathlib import Path
import random
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
import zipfile
import zlib

import chd


def setUpModule():
    # No unit test may reach the real rdctl: a guest cache drop or trim would hit the shared VM.
    guard = patch.object(chd, 'rdctl_path', return_value='/nonexistent/rdctl')
    guard.start()
    unittest.addModuleCleanup(guard.stop)


def identity_of(data):
    return {'size': len(data), 'sha256': hashlib.sha256(data).hexdigest(), 'sha1': hashlib.sha1(data).hexdigest(),
            'md5': hashlib.md5(data).hexdigest(), 'crc32': f'{zlib.crc32(data):08x}'}


class IngressBindingTests(unittest.TestCase):
    def test_default_and_separate_loopback_bindings(self):
        self.assertEqual('127.0.0.1', chd.parse_args([]).https_bind)
        self.assertEqual('127.0.0.2', chd.parse_args(['--https-bind', '127.0.0.2']).https_bind)

    def test_preparation_forwards_the_selected_binding_to_compose(self):
        with tempfile.TemporaryDirectory() as temporary:
            settings = chd.Settings(False, Path(temporary).resolve(), None, None, chd.image_refs('t'),
                                    'r', 'nginx', 0, 60, 60, https_bind='127.0.0.2')
            run = chd.Qualification(settings, chd.synthetic_subject())
            with patch.object(chd, 'ingress_certificate'), patch.object(chd, 'network_subnets', return_value=[]), \
                    patch.object(chd, 'free_subnets', return_value=('10.213.1.0/24', '10.213.2.0/24')), \
                    patch.object(chd, 'Stack') as stack, patch.object(chd, 'Api'), patch.object(chd, 'DiskSampler'):
                run.prepare()
            self.assertEqual('127.0.0.2', stack.call_args.args[1]['ROMD_HTTPS_BIND'])

    def test_public_and_invalid_bindings_fail_closed(self):
        for address in ('0.0.0.0', '192.168.1.2', '::1', 'hostname', '127.0.0.1:443'):
            with self.subTest(address=address), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                chd.parse_args(['--https-bind', address])


class DecoderIdentityTests(unittest.TestCase):
    def manifest(self, rid='linux-x64'):
        return {'schemaVersion': 3, 'product': 'moonlark_chdr', 'rid': rid, 'upstreamCommit': 'a' * 40,
                'recipeVersion': 2, 'sha256': 'b' * 64, 'buildInfo': {'buildId': 'c' * 64}}

    def test_receipt_records_current_tuple_and_exact_binary_build(self):
        for architecture, rid in (('amd64', 'linux-x64'), ('arm64', 'linux-arm64')):
            with self.subTest(architecture=architecture):
                observed = chd.decoder_identity(self.manifest(rid), architecture, 'a' * 40, 2)
                self.assertEqual({'rid': rid, 'upstreamCommit': 'a' * 40, 'recipeVersion': 2,
                                  'binarySha256': 'b' * 64, 'buildId': 'c' * 64}, observed)

    def test_old_recipe_other_pin_and_architecture_cannot_qualify_current_decoder(self):
        for field, value in (('recipeVersion', 1), ('recipeVersion', True), ('upstreamCommit', 'd' * 40),
                             ('rid', 'linux-arm64'), ('sha256', ''), ('buildInfo', None), ('buildInfo', []),
                             ('schemaVersion', 2), ('product', 'other')):
            with self.subTest(field=field, value=value):
                manifest = self.manifest() | {field: value}
                with self.assertRaises(chd.HarnessError):
                    chd.decoder_identity(manifest, 'amd64', 'a' * 40, 2)
        with self.assertRaises(chd.HarnessError):
            chd.decoder_identity(self.manifest(), 'unknown', 'a' * 40, 2)

    def test_source_tuple_is_read_from_current_authorities(self):
        upstream, recipe_version = chd.current_decoder_source()
        self.assertRegex(upstream, '[0-9a-f]{40}')
        self.assertGreater(recipe_version, 0)


class TemporaryTree(unittest.TestCase):
    def setUp(self):
        self._directory = tempfile.TemporaryDirectory()
        self.base = Path(self._directory.name).resolve()

    def tearDown(self):
        self._directory.cleanup()

    def directory(self, *parts):
        path = self.base.joinpath(*parts)
        path.mkdir(parents=True, exist_ok=True)
        return path


class ConfigurationTests(TemporaryTree):
    def paths(self, environ, **options):
        options.setdefault('repository', self.directory('repository'))
        options.setdefault('temporary_roots', ())
        return chd.real_paths(environ, **options)

    def fixtures(self):
        fixtures = self.directory('fixtures')
        (fixtures / 'manifest.json').write_text('{"schema": 1}')
        return fixtures

    def test_real_paths_accepts_resolved_disjoint_directories(self):
        fixtures, root = self.fixtures(), self.directory('e2e')
        self.assertEqual((fixtures, root), self.paths({'ROMD_FIXTURES': str(fixtures), 'ROMD_E2E_ROOT': str(root)}))

    def test_real_paths_fail_closed(self):
        fixtures, root = self.fixtures(), self.directory('e2e')
        link = self.base / 'linked'
        link.symlink_to(root, target_is_directory=True)
        comma = self.directory('with,comma')
        cases = {
            'unset fixtures': {'ROMD_E2E_ROOT': str(root)},
            'empty root': {'ROMD_FIXTURES': str(fixtures), 'ROMD_E2E_ROOT': ''},
            'relative root': {'ROMD_FIXTURES': str(fixtures), 'ROMD_E2E_ROOT': 'e2e'},
            'missing root': {'ROMD_FIXTURES': str(fixtures), 'ROMD_E2E_ROOT': str(self.base / 'absent')},
            'symlinked root': {'ROMD_FIXTURES': str(fixtures), 'ROMD_E2E_ROOT': str(link)},
            'dot-dot root': {'ROMD_FIXTURES': str(fixtures), 'ROMD_E2E_ROOT': str(fixtures / '..' / 'e2e')},
            'comma root': {'ROMD_FIXTURES': str(fixtures), 'ROMD_E2E_ROOT': str(comma)},
            'root inside fixtures': {'ROMD_FIXTURES': str(fixtures), 'ROMD_E2E_ROOT': str(self.directory('fixtures', 'scratch'))},
            'fixtures inside root': {'ROMD_FIXTURES': str(fixtures), 'ROMD_E2E_ROOT': str(self.base)},
        }
        for name, environ in cases.items():
            with self.subTest(name), self.assertRaises(chd.HarnessError):
                self.paths(environ)

    def test_real_paths_refuse_temporary_and_repository_locations(self):
        fixtures, root = self.fixtures(), self.directory('e2e')
        environ = {'ROMD_FIXTURES': str(fixtures), 'ROMD_E2E_ROOT': str(root)}
        with self.assertRaisesRegex(chd.HarnessError, 'temporary'):
            self.paths(environ, temporary_roots=(self.base,))
        with self.assertRaisesRegex(chd.HarnessError, 'repository'):
            self.paths(environ, repository=root)
        with self.assertRaisesRegex(chd.HarnessError, 'manifest.json'):
            (fixtures / 'manifest.json').unlink()
            self.paths(environ)

    def test_default_temporary_roots_cover_macos_and_linux_scratch(self):
        roots = chd.default_temporary_roots()
        for path in ('/tmp', '/private/tmp', '/private/var/folders'):
            self.assertIn(Path(path), roots)

    def test_main_fails_closed_without_environment_or_docker(self):
        stderr = io.StringIO()
        with patch.dict(os.environ, {}, clear=True), contextlib.redirect_stderr(stderr), \
                patch.object(chd, 'docker', side_effect=AssertionError('Docker must not be reached')):
            self.assertEqual(2, chd.main([]))
        self.assertIn('ROMD_FIXTURES is unset', stderr.getvalue())

    def test_arguments_reject_mixed_modes_and_bad_sizes(self):
        for argv in (['--synthetic', '--disc', 'ffx'], ['--synthetic', '--synthetic-mib', '8'], ['--image-tag', 'bad tag']):
            with self.subTest(argv), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                chd.parse_args(argv)
        self.assertEqual(['gt4'], chd.parse_args(['--disc', 'gt4']).disc)


class PathSafetyTests(TemporaryTree):
    def test_run_ids_and_disposable_folders(self):
        root = self.directory('e2e')
        run_id = chd.new_run_id('ffx', token='a1b2c3')
        self.assertRegex(run_id, r'^chd-ffx-\d{8}t\d{6}-a1b2c3$')
        folder = chd.run_folder(root, run_id)
        folder.mkdir(parents=True)
        self.assertEqual(folder, chd.require_disposable(root, folder))
        with self.assertRaises(chd.HarnessError):
            chd.run_folder(root, '../escape')

    def test_disposable_refuses_anything_but_a_run_folder_directly_under_runs(self):
        root = self.directory('e2e')
        run_id = chd.new_run_id('gt4', token='000000')
        nested = self.directory('e2e', 'runs', run_id, 'data')
        outside = self.directory('elsewhere', run_id)
        link = root / 'runs' / chd.new_run_id('ffx', token='ffffff')
        link.symlink_to(outside, target_is_directory=True)
        for candidate in (nested, outside, link, self.directory('e2e', 'runs', 'not-a-run'), root / 'runs'):
            with self.subTest(candidate=candidate), self.assertRaises(chd.HarnessError):
                chd.require_disposable(root, candidate)

    def test_evidence_must_stay_outside_runs_fixtures_and_the_checkout(self):
        root, fixtures = self.directory('e2e'), self.directory('fixtures')
        self.assertEqual(root / 'evidence' / 'x.json', chd.resolve_evidence(None, root, 'x'))
        self.assertIsNone(chd.resolve_evidence(None, None, 'x'))
        for raw in ('relative.json', str(root / 'runs' / 'evidence.json'), str(fixtures / 'evidence.json'),
                    str(chd.ROOT / 'evidence.json'), str(chd.ROOT / 'tmp' / 'e2e' / 'evidence.json')):
            with self.subTest(raw), self.assertRaises(chd.HarnessError):
                chd.resolve_evidence(raw, root, 'x', fixtures)
        with self.assertRaisesRegex(chd.HarnessError, 'repository'):
            chd.resolve_evidence(None, root, 'x', repository=root)

    def test_runs_directory_rejects_symlinks(self):
        root = self.directory('e2e')
        self.assertEqual(root / 'runs', chd.runs_directory(root))
        linked_root = self.directory('linked-root')
        (linked_root / 'runs').symlink_to(self.directory('elsewhere'), target_is_directory=True)
        with self.assertRaisesRegex(chd.HarnessError, 'symlink'):
            chd.runs_directory(linked_root)
        run = self.directory('elsewhere', chd.new_run_id('ffx', token='abcdef'))
        with self.assertRaises(chd.HarnessError):
            chd.require_disposable(linked_root, linked_root / 'runs' / run.name)
        alias = self.base / 'alias'
        alias.symlink_to(root, target_is_directory=True)
        with self.assertRaises(chd.HarnessError):
            chd.runs_directory(alias)

    def test_teardown_deletes_only_a_folder_this_run_created(self):
        root = self.directory('e2e')
        settings = chd.Settings(True, root, None, None, chd.image_refs('t'), 'r', 'nginx', 1 << 20, 60, 60)
        run = chd.Qualification(settings, chd.synthetic_subject())
        run.folder.mkdir(parents=True)
        (run.folder / 'keep').write_text('owner data')
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertIsNone(run.teardown(failed=False))
        self.assertTrue((run.folder / 'keep').is_file())
        self.assertIn('not created', run.record['cleanup']['runFolder'])
        with self.assertRaisesRegex(chd.HarnessError, 'did not create'):
            run.delete_run_folder()
        run.created = True
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertIsNone(run.teardown(failed=False))
        self.assertFalse(run.folder.exists())
        self.assertEqual('deleted', run.record['cleanup']['runFolder'])

    def test_download_evidence_keeps_every_compared_digest(self):
        identity = identity_of(b'original bytes')
        settings = chd.Settings(False, self.directory('e2e'), None, None, chd.image_refs('t'), 'r', 'nginx', 0, 60, 60)
        subject = chd.Subject('ffx', 'dvd5', 'Dvd5', 210, identity, {key: identity[key] for key in ('size', 'sha1', 'md5', 'crc32')})
        observed = {**identity, 'host': 'admin', 'seconds': 1.0, 'megabytesPerSecond': 1.0, 'cacheControl': 'private, no-store'}
        recorded = chd.Qualification(settings, subject).verify_download('admin original', observed, True)
        self.assertEqual({**identity, 'host': 'admin', 'seconds': 1.0, 'megabytesPerSecond': 1.0}, recorded)

    def test_failed_vm_trim_warns_without_failing_the_run(self):
        root = self.directory('e2e')
        settings = chd.Settings(False, root, None, None, chd.image_refs('t'), 'r', 'nginx', 0, 60, 60)
        run = chd.Qualification(settings, chd.synthetic_subject())
        stderr = io.StringIO()
        with patch.object(chd.sys, 'platform', 'darwin'), \
                patch.object(chd, 'trim_rancher_vm', return_value={'status': 'failed', 'reason': 'guest busy'}), \
                contextlib.redirect_stderr(stderr):
            self.assertIsNone(run.teardown(failed=False), 'the next disc keeps its own space preflight')
        self.assertEqual({'status': 'failed', 'reason': 'guest busy'}, run.record['cleanup']['vmTrim'])
        self.assertIn('WARNING: Docker VM trim failed: guest busy', stderr.getvalue())
        self.assertIn('rdctl shell sudo fstrim -v', stderr.getvalue())

    def test_macos_teardown_releases_guest_held_run_files_before_the_trim(self):
        for synthetic in (True, False):
            with self.subTest(synthetic=synthetic):
                root = self.directory(f'e2e-{synthetic}')
                settings = chd.Settings(synthetic, root, None, None, chd.image_refs('t'), 'r', 'nginx', 0, 60, 60)
                run = chd.Qualification(settings, chd.synthetic_subject())
                run.folder.mkdir(parents=True)
                run.created = True
                calls = []

                def release():
                    self.assertFalse(run.folder.exists(), 'release only after the run folder is deleted')
                    calls.append('release')
                    return {'status': 'released'}

                trim = lambda: calls.append('trim') or {'status': 'trimmed'}
                with patch.object(chd.sys, 'platform', 'darwin'), \
                        patch.object(chd, 'release_rancher_bind_mount_files', side_effect=release), \
                        patch.object(chd, 'trim_rancher_vm', side_effect=trim), contextlib.redirect_stderr(io.StringIO()):
                    self.assertIsNone(run.teardown(failed=False))
                # Deleted bind-mount files stay allocated on macOS until the guest drops their inodes.
                self.assertEqual(['release'] if synthetic else ['release', 'trim'], calls)
                self.assertEqual({'status': 'released'}, run.record['cleanup']['guestFileRelease'])

    def test_failed_guest_file_release_warns_and_the_trim_still_runs(self):
        for failure in ({'return_value': {'status': 'failed', 'reason': 'sudo'}},
                        {'side_effect': chd.HarnessError('rdctl drop_caches timed out')}):
            with self.subTest(failure=failure):
                root = self.directory(f'e2e-{len(failure)}-{id(failure)}')
                settings = chd.Settings(False, root, None, None, chd.image_refs('t'), 'r', 'nginx', 0, 60, 60)
                run = chd.Qualification(settings, chd.synthetic_subject())
                run.folder.mkdir(parents=True)
                run.created = True
                stderr = io.StringIO()
                with patch.object(chd.sys, 'platform', 'darwin'), \
                        patch.object(chd, 'release_rancher_bind_mount_files', **failure), \
                        patch.object(chd, 'trim_rancher_vm', return_value={'status': 'trimmed'}) as trim, \
                        contextlib.redirect_stderr(stderr):
                    self.assertIsNone(run.teardown(failed=False))
                self.assertEqual('failed', run.record['cleanup']['guestFileRelease']['status'])
                self.assertIn('WARNING: guest file release failed', stderr.getvalue())
                self.assertIn('drop_caches', stderr.getvalue())
                trim.assert_called_once()

    def test_guest_file_release_needs_macos_and_a_deleted_run_folder(self):
        for platform, created in (('linux', True), ('darwin', False)):
            with self.subTest(platform=platform, created=created):
                root = self.directory(f'e2e-{platform}-{created}')
                settings = chd.Settings(True, root, None, None, chd.image_refs('t'), 'r', 'nginx', 0, 60, 60)
                run = chd.Qualification(settings, chd.synthetic_subject())
                run.folder.mkdir(parents=True)
                run.created = created
                with patch.object(chd.sys, 'platform', platform), \
                        patch.object(chd, 'release_rancher_bind_mount_files') as release, \
                        contextlib.redirect_stderr(io.StringIO()):
                    self.assertIsNone(run.teardown(failed=False))
                release.assert_not_called()
                self.assertNotIn('guestFileRelease', run.record['cleanup'])

    def test_guest_file_release_drops_only_the_dentry_and_inode_caches(self):
        result = subprocess.CompletedProcess([], 0, b'', b'')
        with patch.object(chd, 'rdctl_path', return_value=__file__), patch.object(chd, 'run_command', return_value=result) as run:
            self.assertEqual('released', chd.release_rancher_bind_mount_files()['status'])
        self.assertEqual([__file__, 'shell', 'sudo', 'sh', '-c', 'sync && echo 2 > /proc/sys/vm/drop_caches'], run.call_args.args[0])
        with patch.object(chd, 'rdctl_path', return_value='/nonexistent/rdctl'):
            self.assertEqual('skipped', chd.release_rancher_bind_mount_files()['status'])

    def test_hangup_and_termination_take_the_interrupt_path_and_cleanup_ignores_them(self):
        for signum in (chd.signal.SIGTERM, chd.signal.SIGHUP):
            with self.subTest(signum.name), self.assertRaisesRegex(KeyboardInterrupt, signum.name):
                chd.interrupt(signum, None)
        before = {signum: chd.signal.getsignal(signum) for signum in chd.INTERRUPT_SIGNALS}
        try:
            chd.signal.signal(chd.signal.SIGHUP, chd.signal.SIG_IGN)  # as nohup leaves it
            chd.signal.signal(chd.signal.SIGTERM, chd.signal.SIG_DFL)
            chd.install_interrupt_handlers()
            self.assertIs(chd.signal.SIG_IGN, chd.signal.getsignal(chd.signal.SIGHUP), 'an inherited ignore must survive')
            self.assertIs(chd.interrupt, chd.signal.getsignal(chd.signal.SIGTERM))
        finally:
            for signum, handler in before.items():
                chd.signal.signal(signum, handler)
        with chd.interrupts_ignored():
            self.assertEqual({signum: chd.signal.SIG_IGN for signum in chd.INTERRUPT_SIGNALS},
                             {signum: chd.signal.getsignal(signum) for signum in chd.INTERRUPT_SIGNALS})
        self.assertEqual(before, {signum: chd.signal.getsignal(signum) for signum in chd.INTERRUPT_SIGNALS})

    def test_environment_records_the_docker_server_and_host(self):
        info = {'ServerVersion': '28.1', 'OperatingSystem': 'Alpine', 'KernelVersion': '6.6', 'Architecture': 'aarch64',
                'NCPU': 2, 'MemTotal': 6 << 30, 'Name': 'lima-rancher-desktop'}
        result = subprocess.CompletedProcess([], 0, json.dumps(info).encode(), b'')
        with patch.object(chd, 'docker', return_value=result) as inspect:
            environment = chd.docker_environment()
        inspect.assert_called_once_with('info', '--format', '{{json .}}', timeout=60)
        self.assertEqual({key: info[key] for key in ('ServerVersion', 'OperatingSystem', 'KernelVersion', 'Architecture',
                                                     'NCPU', 'MemTotal')}, environment['dockerServer'])
        self.assertEqual({'system', 'release', 'machine'}, set(environment['host']))

    def test_space_preflight_counts_only_passed_peaks_and_never_drops_below_the_assumption(self):
        evidence, gib = self.directory('evidence'), chd.GIB
        self.assertEqual((30 * gib, 10 * gib), (chd.DEFAULT_PEAK_BYTES, chd.PEAK_HEADROOM_BYTES), 'binary units')
        self.assertIsNone(chd.prior_peak(evidence, 'ffx'))
        self.assertEqual(40 * gib, chd.required_free_bytes(None))
        (evidence / 'aborted.json').write_text(json.dumps({'runs': [
            {'disc': 'ffx', 'status': 'failed', 'disk': {'peakBytes': 2 * gib}},
            {'disc': 'ffx', 'status': 'running', 'disk': {'peakBytes': 3 * gib}}]}))
        (evidence / 'broken.json').write_text('{')
        self.assertIsNone(chd.prior_peak(evidence, 'ffx'), 'a failed or aborted low-peak run is not a measurement')
        (evidence / 'passed.json').write_text(json.dumps({'runs': [
            {'disc': 'ffx', 'status': 'passed', 'disk': {'peakBytes': 12 * gib}},
            {'disc': 'gt4', 'status': 'passed', 'disk': {'peakBytes': 99 * gib}}]}))
        self.assertEqual(12 * gib, chd.prior_peak(evidence, 'ffx'))
        self.assertEqual(40 * gib, chd.required_free_bytes(12 * gib), 'a small measured peak never weakens the preflight')
        self.assertEqual(45 * gib, chd.required_free_bytes(35 * gib))
        self.assertEqual(99 * gib, chd.prior_peak(evidence, 'gt4'))


class HashingTests(unittest.TestCase):
    def test_stream_digest_matches_reference_digests_across_chunk_boundaries(self):
        data = random.Random(7).randbytes(3 * 1024 * 1024 + 17)
        digest = chd.StreamDigest()
        for start in range(0, len(data), 999_983):
            digest.update(data[start:start + 999_983])
        self.assertEqual(identity_of(data), digest.identity())
        self.assertEqual(identity_of(data), chd.digest_stream(io.BytesIO(data), chunk_bytes=4096))
        self.assertEqual(identity_of(b''), chd.StreamDigest().identity())

    def test_identity_mismatches_normalize_and_fail_closed(self):
        expected = {'size': '10', 'sha1': 'AB' * 20, 'md5': 'cd' * 16}
        self.assertEqual([], chd.identity_mismatches('x', expected, {'size': 10, 'sha1': 'ab' * 20, 'md5': 'CD' * 16},
                                                     ('size', 'sha1', 'md5')))
        self.assertEqual(1, len(chd.identity_mismatches('x', expected, {'size': 11, 'sha1': 'ab' * 20, 'md5': 'cd' * 16},
                                                        ('size', 'sha1', 'md5'))))
        self.assertEqual(1, len(chd.identity_mismatches('x', expected, {'size': 10, 'sha1': 'ab' * 20}, ('sha1', 'crc32'))))
        self.assertTrue(chd.valid_identity(identity_of(b'abc')))
        self.assertFalse(chd.valid_identity({**identity_of(b'abc'), 'crc32': 'xyz'}))


class FixtureMatchingTests(TemporaryTree):
    GAME = 'Final Fantasy X (USA, Canada)'

    def write_fixtures(self, iso_bytes, dat_identity=None):
        fixtures = self.directory('fixtures')
        (self.directory('fixtures', 'ps2', 'redump') / 'ps2.dat').write_text(
            '<?xml version="1.0"?>\n<!DOCTYPE datafile PUBLIC "-//Logiqx//DTD ROM Management Datafile//EN" '
            '"http://www.logiqx.com/Dats/datafile.dtd">\n'
            + chd.synthetic_dat(self.GAME, 'Final Fantasy X (USA, Canada).iso', dat_identity or identity_of(iso_bytes))
            .split('\n', 1)[1])
        (fixtures / 'ps2' / 'Final Fantasy X (USA).iso').write_bytes(iso_bytes)
        dat_sha = hashlib.sha256((fixtures / 'ps2/redump/ps2.dat').read_bytes()).hexdigest()
        (fixtures / 'manifest.json').write_text(json.dumps({
            'schema': 1,
            'files': [{'role': 'dvd5', 'path': 'ps2/Final Fantasy X (USA).iso', **identity_of(iso_bytes)}],
            'dats': [{'role': 'ps2-redump-dat', 'path': 'ps2/redump/ps2.dat', 'sha256': dat_sha,
                      'discEntries': {'dvd5': self.GAME}}]}))
        return fixtures

    def test_real_subject_binds_manifest_dat_and_streamed_fixture_hashes(self):
        data = random.Random(1).randbytes(8192)
        fixtures = self.write_fixtures(data)
        with contextlib.redirect_stdout(io.StringIO()):
            subject = chd.real_subject(fixtures, chd.DISCS['ffx'])
        self.assertEqual(identity_of(data), subject.identity)
        self.assertEqual('/e2e/fixtures/ps2/Final Fantasy X (USA).iso', subject.iso_container_path)
        self.assertEqual('/e2e/fixtures/ps2/redump/ps2.dat', subject.dat_container_path)
        self.assertEqual({key: identity_of(data)[key] for key in ('size', 'sha1', 'md5', 'crc32')},
                         {key: subject.dat_identity[key] for key in ('size', 'sha1', 'md5', 'crc32')})
        sha256 = lambda path: hashlib.sha256((fixtures / path).read_bytes()).hexdigest()
        self.assertEqual({'fixturePath': 'ps2/Final Fantasy X (USA).iso', 'manifestSha256': sha256('manifest.json'),
                          'datPath': 'ps2/redump/ps2.dat', 'datSha256': sha256('ps2/redump/ps2.dat'),
                          'datGame': subject.provenance['datGame']}, subject.provenance)
        settings = chd.Settings(False, self.base, fixtures, None, chd.image_refs('t'), 'r', 'nginx', 0, 60, 60)
        self.assertEqual({**subject.provenance, 'identity': subject.identity, 'datIdentity': subject.dat_identity},
                         chd.Qualification(settings, subject).record['fixture'])

    def test_real_subject_rejects_a_dat_entry_that_disagrees_with_the_manifest(self):
        data = random.Random(2).randbytes(4096)
        fixtures = self.write_fixtures(data, dat_identity=identity_of(data + b'x'))
        with self.assertRaisesRegex(chd.HarnessError, 'DAT entry'):
            chd.real_subject(fixtures, chd.DISCS['ffx'])

    def test_real_subject_rejects_missing_roles_and_escaping_paths(self):
        fixtures = self.write_fixtures(b'\0' * 2048)
        with self.assertRaisesRegex(chd.HarnessError, 'dvd9'):
            chd.real_subject(fixtures, chd.DISCS['gt4'])
        for relative in ('../outside.iso', '/abs.iso', 'ps2/"quoted".iso'):
            with self.subTest(relative), self.assertRaises((chd.HarnessError, FileNotFoundError)):
                chd.fixture_file(fixtures, relative)

    def test_dat_rom_requires_exactly_one_game_and_rom(self):
        path = self.base / 'two.dat'
        rom = '<rom name="a" size="1" crc="00000000" md5="{0}" sha1="{1}"/>'.format('0' * 32, '0' * 40)
        path.write_text(f'<datafile><game name="A">{rom}</game><game name="A">{rom}</game><game name="B">{rom}{rom}</game></datafile>')
        for name in ('A', 'B', 'missing'):
            with self.subTest(name), self.assertRaises(chd.HarnessError):
                chd.dat_rom(path, name)


class ReferenceTests(unittest.TestCase):
    EXPECTED = {'size': 4096, 'sha1': 'a' * 40, 'md5': 'b' * 32, 'crc32': '0000002a'}

    def reference(self, **overrides):
        return {'recordId': 'id', 'source': 'https://redump.info/disc/210', 'system': 'PlayStation2', 'media': 'Dvd5',
                'fileCount': 1, 'logicalBytes': 4096, 'sha1': 'A' * 40, 'md5': 'b' * 32, 'crc32': '0000002A', **overrides}

    def test_parse_reference_takes_the_single_json_result_line(self):
        stdout = 'info: starting\n' + json.dumps(self.reference()) + '\n{"unrelated": true}\n'
        self.assertEqual('id', chd.parse_reference(stdout)['recordId'])
        for broken in ('', 'no json', json.dumps(self.reference()) + '\n' + json.dumps(self.reference())):
            with self.subTest(broken=broken[:20]), self.assertRaises(chd.HarnessError):
                chd.parse_reference(broken)

    def test_reference_mismatches(self):
        self.assertEqual([], chd.reference_mismatches(self.reference(), self.EXPECTED, 'Dvd5', 210))
        for overrides in ({'media': 'Dvd9'}, {'logicalBytes': 2048}, {'source': 'https://redump.info/disc/211'},
                          {'fileCount': 2}, {'system': 'PlayStation'}, {'crc32': '00000000'}):
            with self.subTest(overrides):
                self.assertTrue(chd.reference_mismatches(self.reference(**overrides), self.EXPECTED, 'Dvd5', 210))

    def test_recipe_comes_from_source_and_measurements_apply_only_to_their_recipe(self):
        self.assertEqual('chd-x/v9', chd.expected_recipe('    public const string Id = "chd-x/v9";'))
        self.assertTrue(chd.expected_recipe(chd.RECIPE_SOURCE.read_text()).startswith('chd-dvd-canonical/'))
        with self.assertRaises(chd.HarnessError):
            chd.expected_recipe('no id here')
        self.assertFalse(chd.recipe_measurement('chd-dvd-canonical/v1', 'dvd5', 'x', 1)['applicable'])
        sha, size = chd.RECIPE_MEASUREMENTS['chd-dvd-canonical/v2']['dvd9']
        self.assertTrue(chd.recipe_measurement('chd-dvd-canonical/v2', 'dvd9', sha, size)['matches'])
        self.assertFalse(chd.recipe_measurement('chd-dvd-canonical/v2', 'dvd9', sha, size + 1)['matches'])
        synthetic = chd.recipe_measurement('chd-dvd-canonical/v2', 'dvd5', 'x', 1, synthetic=True)
        self.assertEqual(False, synthetic['applicable'], 'a synthetic CHD is never compared with a real-disc artifact')


def sample(*staging, runs=()):
    return chd.DataSample(tuple(staging), tuple(runs))


class CheckpointTests(unittest.TestCase):
    def test_encoding_confirmation_requires_unchanged_active_custody(self):
        job = {'phase': 'Encoding', 'isTerminal': False, 'outcome': None}
        before = {'record': 'run-0.json', 'runId': 'run', 'parent': {'ProcessId': 1}, 'child': {'ProcessId': 23},
                  'admission': {'Inode': 41}, 'output': 'temp/staging-v1/slot-1.data', 'bytes': 128}
        self.assertIsNone(chd.encoding_confirmation(job, before, {**before, 'bytes': 256}))
        self.assertIsNone(chd.encoding_confirmation(job, before, before))
        for after in (None, {**before, 'runId': 'other'}, {**before, 'child': {'ProcessId': 24}},
                      {**before, 'admission': {'Inode': 42}}, {**before, 'bytes': 127}):
            with self.subTest(after=after):
                self.assertIn('MISSED checkpoint encoding', chd.encoding_confirmation(job, before, after))
        for changed in ({**job, 'isTerminal': True}, {**job, 'phase': 'Verifying'}, {**job, 'outcome': 'Converged'}):
            self.assertIn('MISSED checkpoint encoding', chd.encoding_confirmation(changed, before, before))

    def test_fires_on_the_same_staging_file_growing_after_arming(self):
        detector = chd.PublishingCheckpoint()
        detector.observe_job({'phase': 'Inspecting', 'isTerminal': False})
        self.assertFalse(detector.observe_data(sample(('a', 10))))
        self.assertFalse(detector.observe_data(sample(('a', 20))), 'growth before arming is ignored')
        detector.observe_job({'phase': 'Encoding', 'isTerminal': False})
        self.assertFalse(detector.observe_data(sample(runs=('run-0.json',))))
        self.assertFalse(detector.observe_data(sample(('b', 0))), 'a new file is not yet growth')
        self.assertFalse(detector.observe_data(sample(('b', 0))), 'an unchanged file is not growth')
        self.assertTrue(detector.observe_data(sample(('b', 4096))))
        self.assertEqual(('b', 0, 4096), detector.growth)
        self.assertIsNone(detector.missed)

    def test_media_run_records_or_a_replaced_file_block_the_checkpoint(self):
        detector = chd.PublishingCheckpoint()
        detector.observe_job({'phase': 'Verifying', 'isTerminal': False})
        detector.observe_data(sample(('a', 10), runs=('run-0.json',)))
        self.assertFalse(detector.observe_data(sample(('a', 20), runs=('run-0.json',))))
        self.assertFalse(detector.observe_data(sample(('c', 30))), 'a different file is not the same file growing')
        self.assertTrue(detector.observe_data(sample(('c', 40))))

    def test_publishing_without_growth_then_terminal_is_a_miss(self):
        detector = chd.PublishingCheckpoint()
        for phase in ('Pending', 'Inspecting', 'Encoding', 'Verifying', 'Publishing'):
            detector.observe_job({'phase': phase, 'isTerminal': False})
            self.assertFalse(detector.observe_data(sample(('a', 100))))
        detector.observe_job({'phase': 'Completed', 'isTerminal': True, 'outcome': 'Converged'})
        self.assertIn('MISSED checkpoint publishing', detector.missed)
        self.assertFalse(detector.observe_data(sample(('a', 200))), 'nothing fires after a miss')

    def test_terminal_before_arming_is_a_miss(self):
        detector = chd.PublishingCheckpoint()
        detector.observe_job({'phase': 'Failed', 'isTerminal': True, 'outcome': 'Failed', 'failureCode': 'Rom.Media.Malformed',
                              'errors': [{'message': 'chdman exited 137'}]})
        self.assertIn('MISSED', detector.missed)
        self.assertIn('failure Rom.Media.Malformed', detector.missed, 'a limit kill must not read as a detector miss')
        self.assertIn('chdman exited 137', detector.missed)

    def test_confirmation_after_the_kill_requires_the_grown_file_still_staged(self):
        held = {'phase': 'Publishing', 'isTerminal': False, 'outcome': None}
        self.assertIsNone(chd.checkpoint_confirmation(held, '.tmp-a', {'.tmp-a': 4096}))
        cases = [({'phase': 'Completed', 'isTerminal': True, 'outcome': 'Converged'}, {'.tmp-a': 4096}),
                 ({'phase': 'Verifying', 'isTerminal': False, 'outcome': None}, {'.tmp-a': 4096}),
                 ({'phase': 'Publishing', 'isTerminal': False, 'outcome': 'Converged'}, {'.tmp-a': 4096}),
                 (held, {}),
                 (held, {'.tmp-other': 4096})]
        for job, staged in cases:
            with self.subTest(job=job, staged=staged):
                self.assertIn('MISSED checkpoint publishing', chd.checkpoint_confirmation(job, '.tmp-a', staged))

    def test_watcher_attributes_a_poll_to_the_attempt_it_was_issued_in(self):
        class KillDuringPoll:
            def __init__(self):
                self.watcher, self.calls = None, 0

            def json(self, host, path):
                self.calls += 1
                if self.calls == 1:
                    self.watcher.attempt = 2  # the controller kills the worker while this poll is in flight
                    return {'phase': 'Verifying', 'isTerminal': False, 'errors': []}
                self.watcher._halt.set()
                return {'phase': 'Publishing', 'isTerminal': False, 'errors': []}

        api = KillDuringPoll()
        watcher = chd.JobWatcher(api, 'job', 0)
        api.watcher = watcher
        watcher.run()
        self.assertIsNone(watcher.error)
        self.assertEqual([(1, 'Verifying'), (2, 'Publishing')], [(entry['attempt'], entry['phase']) for entry in watcher.timeline])
        self.assertFalse(chd.recovery_reexecuted(watcher.timeline), 'a pre-kill Verifying is not re-execution')

    def test_watcher_and_sampler_run_as_real_threads(self):
        class OnePhase:
            def json(self, host, path):
                return {'phase': 'Encoding', 'isTerminal': False, 'errors': []}

        plain = {key: type(value) for key, value in vars(threading.Thread(daemon=True)).items()}
        with tempfile.TemporaryDirectory() as directory:
            for thread in (chd.JobWatcher(OnePhase(), 'job', 0.01), chd.DiskSampler(directory, directory, 0.01)):
                with self.subTest(type(thread).__name__):
                    # Shadowing a Thread attribute breaks start() or join() (Thread._stop is a method before 3.13).
                    self.assertEqual(plain, {key: type(value) for key, value in vars(thread).items() if key in plain})
                    self.assertEqual([], [key for key in vars(thread) if key not in plain and hasattr(threading.Thread, key)])
                    self.assertEqual([], [name for name in vars(type(thread)) if not name.startswith('__') and name != 'run'
                                          and hasattr(threading.Thread, name)])
                    thread.start()
                    if isinstance(thread, chd.JobWatcher):
                        self.assertEqual('Encoding', thread.events.get(timeout=10)['phase'])
                        thread.stop()
                        self.assertIsNone(thread.error)
                    else:
                        self.assertEqual(0.01, thread.summary()['sampleIntervalSeconds'])
                    self.assertFalse(thread.is_alive())

    def test_recovery_must_repeat_the_encode(self):
        first = [{'attempt': 1, 'phase': phase} for phase in ('Inspecting', 'Encoding', 'Verifying', 'Publishing')]
        replay = first + [{'attempt': 2, 'phase': 'Publishing'}, {'attempt': 2, 'phase': 'Completed'}]
        self.assertFalse(chd.recovery_reexecuted(replay), 'a journal replay is not a re-execution')
        self.assertFalse(chd.recovery_reexecuted(first + [{'attempt': 2, 'phase': 'Inspecting'},
                                                          {'attempt': 2, 'phase': 'Publishing'}]))
        for phase in ('Encoding', 'Verifying'):
            with self.subTest(phase):
                self.assertTrue(chd.recovery_reexecuted(first + [{'attempt': 2, 'phase': phase}]))

    def test_encode_wall_seconds_per_attempt(self):
        timeline = [{'attempt': 1, 'phase': phase, 'elapsedSeconds': seconds} for phase, seconds in
                    (('Pending', 0), ('Inspecting', 2), ('Encoding', 10), ('Verifying', 70.5), ('Publishing', 90))]
        timeline += [{'attempt': 2, 'phase': phase, 'elapsedSeconds': seconds} for phase, seconds in
                     (('Publishing', 91), ('Inspecting', 400), ('Encoding', 410), ('Verifying', 480), ('Completed', 500))]
        timeline.append({'attempt': 3, 'phase': 'Encoding', 'elapsedSeconds': 600})
        self.assertEqual({'1': 60.5, '2': 70.0}, chd.encode_wall_seconds(timeline))

    def test_old_encoding_phase_during_lease_wait_is_excluded_from_encode_duration(self):
        timeline = [{'attempt': 2, 'phase': phase, 'elapsedSeconds': seconds} for phase, seconds in
                    (('Encoding', 5), ('Inspecting', 305), ('Encoding', 310), ('Verifying', 350), ('Completed', 360))]
        self.assertEqual({'2': 40}, chd.encode_wall_seconds(timeline))

    def test_scan_data_reads_staging_sizes_and_run_records_only(self):
        with tempfile.TemporaryDirectory() as directory:
            data = Path(directory)
            self.assertEqual(sample(), chd.scan_data(data))
            (data / 'content' / '.staging').mkdir(parents=True)
            (data / 'content' / '.staging' / 'x.tmp').write_bytes(b'12345')
            runs = data / 'temp' / 'media-runs-v1'
            runs.mkdir(parents=True)
            for name in ('capacity.json', 'accounting.lease', 'processing-0.lease', 'run-1.json', 'run-1.json.identity'):
                (runs / name).write_text('x')
            self.assertEqual(sample(('x.tmp', 5), runs=('run-1.json', 'run-1.json.identity')), chd.scan_data(data))


class ActiveEncodingTests(TemporaryTree):
    def test_reexecution_requires_a_new_run_with_output_not_the_old_encoding_phase(self):
        before = {'runId': 'interrupted-run', 'bytes': 100}
        self.assertFalse(chd.fresh_encoding(before, None))
        self.assertFalse(chd.fresh_encoding(before, before | {'bytes': 200}))
        self.assertFalse(chd.fresh_encoding(before, {'runId': 'new-run', 'bytes': 0}))
        self.assertTrue(chd.fresh_encoding(before, {'runId': 'new-run', 'bytes': 1}))

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux per-thread child observation')
    def test_child_launched_by_a_non_main_thread_is_observed(self):
        ready, release = threading.Event(), threading.Event()
        children = []

        def launch():
            with subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)']) as child:
                children.append(child.pid)
                ready.set()
                try:
                    release.wait(10)
                finally:
                    child.terminate()
                    child.wait(timeout=5)

        class LocalProc:
            def exec(self, service, *arguments):
                return subprocess.run(arguments, capture_output=True, check=True, timeout=5)

        thread = threading.Thread(target=launch)
        thread.start()
        try:
            self.assertTrue(ready.wait(5))
            leader_children = Path(f'/proc/{os.getpid()}/task/{os.getpid()}/children').read_text().split()
            self.assertNotIn(str(children[0]), leader_children)
            self.assertIn(children[0], chd.media_child_pids(LocalProc(), os.getpid()))
        finally:
            release.set()
            thread.join(timeout=10)
            self.assertFalse(thread.is_alive())

    @unittest.skipUnless(sys.platform.startswith('linux'), 'Linux sealed memfd execution')
    def test_active_tool_is_identified_by_its_sealed_image_bytes(self):
        class LocalProc:
            def exec(self, service, *arguments):
                return subprocess.run(arguments, capture_output=True, check=True, timeout=10)

        program = Path(shutil.which('sleep')).resolve().read_bytes()
        build = self.directory('chdman')
        (build / 'native').mkdir()
        (build / 'native/chdman').write_bytes(program)
        (build / 'native/chdman').chmod(0o755)

        def record(binary):
            (build / 'build-manifest.json').write_text(json.dumps({'schemaVersion': 2, 'binary': binary}))

        def launch(sealed):
            if not sealed:
                return subprocess.Popen([str(build / 'native/chdman'), '30'])
            image = os.memfd_create('chdman', os.MFD_ALLOW_SEALING)
            try:
                os.write(image, program)
                fcntl.fcntl(image, fcntl.F_ADD_SEALS, fcntl.F_SEAL_SEAL | fcntl.F_SEAL_SHRINK | fcntl.F_SEAL_GROW | fcntl.F_SEAL_WRITE)
                return subprocess.Popen([f'/proc/self/fd/{image}', '30'], pass_fds=[image])
            finally:
                os.close(image)

        pinned = {'bytes': len(program), 'sha256': hashlib.sha256(program).hexdigest()}
        cases = [('sealed pinned image', True, pinned, None),
                 ('image at a path', False, pinned, 'active child is not the sealed chdman image'),
                 ('other digest', True, {**pinned, 'sha256': 'a' * 64}, 'active child is not the pinned chdman'),
                 ('other size', True, {**pinned, 'bytes': len(program) + 1}, 'active child is not the pinned chdman'),
                 ('textual size', True, {**pinned, 'bytes': str(len(program))}, 'active child is not the pinned chdman')]
        for name, sealed, binary, error in cases:
            with self.subTest(name):
                record(binary)
                with launch(sealed) as child:
                    try:
                        if error is None:
                            self.assertEqual({'executable': chd.SEALED_CHDMAN_EXECUTABLE, **pinned},
                                             chd.pinned_tool_image(LocalProc(), child.pid, str(build)))
                        else:
                            with self.assertRaisesRegex(chd.HarnessError, error):
                                chd.pinned_tool_image(LocalProc(), child.pid, str(build))
                    finally:
                        child.kill()
        record(pinned)
        (build / 'native/chdman').write_bytes(program + b'x')
        with launch(True) as child:
            try:
                with self.assertRaisesRegex(chd.HarnessError, 'configured chdman binary differs from its build record'):
                    chd.pinned_tool_image(LocalProc(), child.pid, str(build))
            finally:
                child.kill()

    def test_only_permitted_pid_one_encode_for_the_selected_job_arms(self):
        data = self.directory('data')
        records = self.directory('data', 'temp', 'media-runs-v1')
        output = self.directory('data', 'temp', 'staging-v1') / 'slot-1.data'
        output.write_bytes(b'encoded')
        record = {'Version': 'romd-media-run/v2', 'JobId': 'job', 'Operation': 'encode-chd', 'Phase': 'permitted', 'RunId': 'run',
                  'Parent': {'ProcessId': 1}, 'Identity': {'ProcessId': 23}, 'Admission': {'Inode': 41},
                  'Staging': [{'Role': 'output', 'Path': '/var/lib/romd/temp/staging-v1/slot-1.data'}]}
        journal = records / 'run-0.json'
        journal.write_text(json.dumps(record))
        measured = chd.active_encoding(data, 'job')
        self.assertEqual(7, measured['bytes'])
        self.assertEqual('run', measured['runId'])
        for override in ({'Version': 'romd-media-run/v1'}, {'JobId': 'other'}, {'Operation': 'inspect-chd'}, {'Phase': 'stopped'},
                         {'Parent': None}, {'Identity': None}, {'Admission': None},
                         {'Parent': {'ProcessId': 7}}, {'Staging': []},
                         {'Staging': [{'Role': 'output', 'Path': '/var/lib/romd/../escape'}]}):
            with self.subTest(override=override):
                journal.write_text(json.dumps(record | override))
                self.assertIsNone(chd.active_encoding(data, 'job'))
        for malformed in (b'{', b'null', b'x' * 16385):
            journal.write_bytes(malformed)
            self.assertIsNone(chd.active_encoding(data, 'job'))

    def test_cd_record_is_explicitly_selected_and_uses_its_bounded_multi_source_budget(self):
        data = self.directory('data')
        records = self.directory('data', 'temp', 'media-runs-v1')
        output = self.directory('data', 'temp', 'staging-v1') / 'slot-1.data'
        output.write_bytes(b'encoded')
        record = {'Version': 'romd-media-run/v2', 'JobId': 'job', 'Operation': 'encode-chd-cd', 'Phase': 'permitted', 'RunId': 'run',
                  'Parent': {'ProcessId': 1}, 'Identity': {'ProcessId': 23}, 'Admission': {'Inode': 41},
                  'Staging': [{'Role': 'source', 'Path': 'x' * 240} for _ in range(99)] +
                             [{'Role': 'output', 'Path': '/var/lib/romd/temp/staging-v1/slot-1.data'}]}
        journal = records / 'run-0.json'
        journal.write_text(json.dumps(record))
        self.assertGreater(journal.stat().st_size, 16384)
        self.assertIsNone(chd.active_encoding(data, 'job'))
        self.assertEqual('run', chd.active_encoding(data, 'job', 'encode-chd-cd')['runId'])
        self.assertIsNone(chd.active_encoding(data, 'job', 'unbounded-operation'))
        journal.write_bytes(b'x' * ((1 << 20) + 1))
        self.assertIsNone(chd.active_encoding(data, 'job', 'encode-chd-cd'))



class CustodyTests(unittest.TestCase):
    def test_persistent_lease_and_accounting_files_are_not_holds(self):
        with tempfile.TemporaryDirectory() as directory:
            data = Path(directory)
            runs, pool = data / 'temp' / 'media-runs-v1', data / 'temp' / 'staging-v1'
            runs.mkdir(parents=True)
            pool.mkdir(parents=True)
            (data / 'content' / '.staging').mkdir(parents=True)
            for name in ('capacity.json', 'accounting.lease', 'processing-0.lease', 'recovery.lease'):
                (runs / name).write_text('x')
            for name in ('accounting.lease', 'slot-0.lease', 'slot-1.lease'):
                (pool / name).write_text('x')
            (pool / 'reservations.json').write_text(json.dumps({'Version': 1, 'Reservations': {}}))
            self.assertEqual([], chd.custody_residue(data))
            (runs / 'run-0.json').write_text('{}')
            (pool / 'slot-1.data').write_bytes(b'x')
            (pool / 'slot-1.data.quarantine').write_bytes(b'')
            (pool / 'reservations.json').write_text(json.dumps({'Version': 1, 'Reservations': {'1': 4096}}))
            (data / 'content' / '.staging' / 'partial').write_bytes(b'x')
            problems = chd.custody_residue(data)
            for expected in ('media run record run-0.json', 'staging payload slot-1.data', 'staging reservation slot 1',
                             'quarantine marker temp/staging-v1/slot-1.data.quarantine', 'CAS staging entry partial'):
                self.assertTrue(any(problem.startswith(expected) for problem in problems), (expected, problems))
            (pool / 'reservations.json').write_text('{')
            self.assertIn('unreadable staging accounting temp/staging-v1/reservations.json', chd.custody_residue(data))

    def test_malformed_isolation_requires_only_the_poison_to_fail(self):
        poison = {'kind': 'rom', 'fileName': chd.POISON_NAME, 'outcome': 'failed',
                  'error': 'The media is malformed or its complete output could not be verified.'}
        sibling = {'kind': 'rom', 'fileName': f'archive/{chd.SIBLING_NAME}', 'outcome': 'ingested'}
        self.assertEqual([], chd.malformed_isolation_failures([poison, sibling, {'kind': 'dat', 'fileName': 'x'}]))
        cases = {
            'poison ingested': [{**poison, 'outcome': 'ingested', 'error': None}, sibling],
            'sibling rejected': [poison, {**sibling, 'outcome': 'rejected'}],
            'native reading off': [{**poison, 'error': 'CHD preparation requires configured native assets.'}, sibling],
            'other failure category': [{**poison, 'error': 'The media exceeds the inspection limits.'}, sibling],
            'error without category': [{**poison, 'error': None}, sibling],
            'missing sibling': [poison],
            'unexpected item': [poison, sibling, {'kind': 'rom', 'fileName': 'other.bin', 'outcome': 'ingested'}],
        }
        for name, items in cases.items():
            with self.subTest(name):
                self.assertTrue(chd.malformed_isolation_failures(items))


def read_iso_directory(image, extent, size):
    entries, offset = {}, extent * chd.SECTOR
    while offset < extent * chd.SECTOR + size:
        length = image[offset]
        if length == 0:
            offset = (offset // chd.SECTOR + 1) * chd.SECTOR
            continue
        name = image[offset + 33:offset + 33 + image[offset + 32]]
        entries[name] = struct.unpack_from('<I', image, offset + 2)[0], struct.unpack_from('<I', image, offset + 10)[0]
        offset += length
    return entries


class SyntheticInputTests(TemporaryTree):
    def test_synthetic_iso_parses_back_to_its_system_cnf_and_boot_elf(self):
        path = self.base / 'synthetic.iso'
        total = 96 * chd.SECTOR
        identity = chd.write_synthetic_iso(path, total, seed=5)
        image = path.read_bytes()
        self.assertEqual(identity_of(image), identity)
        self.assertEqual(total, len(image))
        pvd = chd.PRIMARY_BLOCK * chd.SECTOR
        self.assertEqual((1, b'CD001', 1), (image[pvd], image[pvd + 1:pvd + 6], image[pvd + 6]))
        self.assertEqual(255, image[(chd.PRIMARY_BLOCK + 1) * chd.SECTOR])
        self.assertEqual((96, 96), (struct.unpack_from('<I', image, pvd + 80)[0], struct.unpack_from('>I', image, pvd + 84)[0]))
        self.assertEqual(chd.SECTOR, struct.unpack_from('<H', image, pvd + 128)[0])
        root_extent, root_size = struct.unpack_from('<I', image, pvd + 158)[0], struct.unpack_from('<I', image, pvd + 166)[0]
        entries = read_iso_directory(image, root_extent, root_size)
        cnf_extent, cnf_size = entries[b'SYSTEM.CNF;1']
        configuration = image[cnf_extent * chd.SECTOR:cnf_extent * chd.SECTOR + cnf_size].decode('ascii')
        self.assertEqual(chd.BOOT_CONFIGURATION, configuration)
        boot = re.search(r'BOOT2 = cdrom0:\\(\S+)', configuration).group(1).encode()
        elf_extent, elf_size = entries[boot]
        elf = image[elf_extent * chd.SECTOR:elf_extent * chd.SECTOR + elf_size]
        self.assertEqual((b'\x7fELF', 512), (elf[:4], elf_size))
        self.assertEqual((2, 8, chd.ENTRY_POINT), struct.unpack_from('<HHxxxxI', elf, 16))
        self.assertEqual((1, 256, chd.ENTRY_POINT, 0, 128, 256, 5, 16), struct.unpack_from('<8I', elf, 52))
        padding = image[chd.PS2_HEADER_BLOCKS * chd.SECTOR:]
        self.assertGreater(len(set(padding)), 200, 'padding must be incompressible, not zeros')
        self.assertLess(len(zlib.compress(padding)), len(padding) + 64)
        self.assertEqual(identity, chd.write_synthetic_iso(self.base / 'again.iso', total, seed=5), 'seeded')
        with self.assertRaises(chd.HarnessError):
            chd.write_synthetic_iso(self.base / 'odd.iso', total + 1, seed=5)

    def test_synthetic_dat_round_trips_through_the_dat_reader(self):
        identity = identity_of(b'payload')
        path = self.base / 'synthetic.dat'
        path.write_text(chd.synthetic_dat('A & B "game"', 'a.iso', identity))
        rom = chd.dat_rom(path, 'A & B "game"')
        self.assertEqual([], chd.identity_mismatches('dat', identity, rom, ('size', 'sha1', 'md5', 'crc32')))


class RedumpPageTests(unittest.TestCase):
    """Mirrors RedumpDiscMediaReferenceParser's closed-layout rules; this is not a .NET parse."""

    HEADERS = ['Filename', 'Size', 'CRC-32', 'MD5', 'SHA-1']

    def test_page_matches_the_singleton_dvd_layout(self):
        identity = identity_of(b'synthetic disc bytes')
        page = chd.redump_page(chd.SYNTHETIC_ISO, identity)
        raw = page.encode('utf-8')
        self.assertTrue(0 < len(raw) <= 1 << 20)
        self.assertTrue(page.startswith('<!DOCTYPE html>'))
        self.assertEqual(1, page.upper().count('<!DOCTYPE'))
        self.assertFalse(any(ord(c) < 32 and c not in '\r\n\t' for c in page))
        for forbidden in ('<?', '<![CDATA[', '<!ENTITY', '<script', '<svg', '<math', '<template', '<noscript', 'href'):
            self.assertNotIn(forbidden, page)
        tables = re.findall(r"<table class='([^']+)'>.*?</table>", page)
        self.assertEqual(['disc-info-table', 'files-table'], tables)
        info = ET.fromstring(re.search(r"<table class='disc-info-table'>.*?</table>", page).group(0))
        rows = {row[0].text: row[1].text for row in info}
        self.assertTrue(all(row.tag == 'tr' and len(row) == 2 and all(cell.tag == 'td' for cell in row) for row in info))
        self.assertEqual({'System': 'Sony PlayStation 2', 'Media': 'DVD-5'}, rows)
        files = ET.fromstring(re.search(r"<table class='files-table'>.*?</table>", page).group(0))
        self.assertEqual(['thead', 'tbody'], [section.tag for section in files])
        self.assertEqual([self.HEADERS], [[cell.text for cell in row] for row in files[0]])
        body = files[1]
        self.assertEqual(1, len(body))
        self.assertEqual({}, body[0].attrib)
        cells = [cell.text for cell in body[0]]
        self.assertEqual(['td'] * 5, [cell.tag for cell in body[0]])
        self.assertEqual([chd.SYNTHETIC_ISO, str(identity['size']), identity['crc32'], identity['md5'], identity['sha1']], cells)
        self.assertTrue(cells[1].isdigit())
        for value, length in zip(cells[2:], (8, 32, 40)):
            self.assertRegex(value, f'^[0-9a-f]{{{length}}}$')

    def test_mock_server_serves_utf8_html_without_encoding_or_redirects(self):
        conf = chd.mock_redump_conf(4242)
        self.assertIn('location = /disc/4242 {', conf)
        self.assertIn('default_type "text/html; charset=utf-8";', conf)
        self.assertIn('alias /srv/redump/4242.html;', conf)
        self.assertIn('gzip off;', conf)
        self.assertIn('server_name redump.info;', conf)
        self.assertNotIn('return 30', conf)


def preflight(header, artifact_bytes):
    """Python port of Romd.Media.Chd.ChdHeaderPreflight with ChdDvdLimits."""
    dvd = {0, 0x7A6C6962, 0x6C7A6D61, 0x68756666, 0x666C6163, 0x7A737464}
    cd = {0x63647A6C, 0x63646C7A, 0x6364666C, 0x63647A73}
    if len(header) < 124 or header[:8] != b'MComprHD' or struct.unpack_from('>I', header, 8)[0] != 124:
        return 'header'
    if struct.unpack_from('>I', header, 12)[0] != 5 or any(header[104:124]):
        return 'version or parent'
    if not 124 <= artifact_bytes <= 10 << 30:
        return 'artifact length'
    if any(codec not in dvd | cd for codec in struct.unpack_from('>4I', header, 16)):
        return 'codec'
    logical, = struct.unpack_from('>Q', header, 32)
    hunk, unit = struct.unpack_from('>II', header, 56)
    if not logical or not hunk or not unit or logical > 10 << 30 or hunk > 1 << 20:
        return 'geometry'
    if logical // hunk + (1 if logical % hunk else 0) > 2621440:
        return 'hunk count'
    return 'accepted'


class MalformedInputTests(TemporaryTree):
    def test_poison_passes_header_preflight_so_the_body_reaches_inspection(self):
        poison = chd.poison_chd(3)
        self.assertEqual('accepted', preflight(poison, len(poison)))
        self.assertEqual((0x7A6C6962, 0, 0, 0), struct.unpack_from('>4I', poison, 16))
        self.assertEqual((16384, 2048), struct.unpack_from('>II', poison, 56))
        self.assertEqual(124, struct.unpack_from('>Q', poison, 40)[0], 'the map follows the header')
        self.assertGreater(len(set(poison[124:])), 200, 'the map and body are corrupt random bytes')
        self.assertEqual('version or parent', preflight(poison[:104] + b'\1' * 20, len(poison)))

    def test_malformed_archive_holds_the_poison_and_one_valid_sibling(self):
        path = self.base / chd.MALFORMED_ARCHIVE
        chd.write_malformed_archive(path, 9)
        with zipfile.ZipFile(path) as archive:
            self.assertEqual([chd.POISON_NAME, chd.SIBLING_NAME], archive.namelist())
            self.assertEqual('accepted', preflight(archive.read(chd.POISON_NAME), len(archive.read(chd.POISON_NAME))))
            self.assertEqual(64 << 10, len(archive.read(chd.SIBLING_NAME)))
            self.assertTrue(all(info.compress_type == zipfile.ZIP_STORED for info in archive.infolist()))


class OverlayTests(TemporaryTree):
    def overlay(self, mock=None, fixtures=None, **options):
        return chd.build_overlay(data=self.base / 'data', tls=self.base / 'tls', inputs=self.base / 'inputs',
                                 client_image='romd-admin:t', ingress_templates=self.base / 'templates',
                                 fixtures=fixtures, mock=mock, **options)

    def test_ingress_streams_downloads_through_a_generated_template(self):
        self.assertEqual([{'type': 'bind', 'source': str(self.base / 'templates'), 'target': '/etc/nginx/templates',
                           'read_only': True}], self.overlay()['services']['ingress']['volumes'])
        committed = (chd.ROOT / 'deploy/examples/https/templates/default.conf.template').read_text()
        generated = chd.e2e_ingress_template(committed)
        anchor = '    proxy_request_buffering off;\n'
        expected = committed.replace(anchor, anchor + '    proxy_buffering off;\n')
        self.assertEqual(expected, generated, 'add the server default and preserve the VFS location verbatim')
        self.assertEqual(committed.count('proxy_buffering off;') + 1, generated.count('proxy_buffering off;'))
        self.assertIn('    proxy_buffering off;', generated.splitlines())
        self.assertEqual(generated, chd.e2e_ingress_template(generated))
        for broken in ('server { }\n', committed.replace('proxy_request_buffering off;', 'proxy_buffering on;'),
                       committed.replace('proxy_buffering off;', 'proxy_buffering on;'),
                       generated.replace('    proxy_buffering off;\n', '    proxy_buffering on;\n', 1)):
            with self.subTest(broken=broken[:20]), self.assertRaises(chd.HarnessError):
                chd.e2e_ingress_template(broken)

    def test_image_defaults_are_exercised_without_native_activation_overrides(self):
        self.assertTrue(chd.parse_args(['--image-defaults']).image_defaults)
        self.assertFalse(chd.parse_args([]).image_defaults)
        overlay = self.overlay(image_defaults=True)
        for name in chd.HOSTS:
            self.assertEqual({}, overlay['services'][name]['environment'])
            self.assertEqual(self.overlay()['services'][name]['volumes'], overlay['services'][name]['volumes'])

    def test_activation_is_disposable_and_scoped_to_each_host(self):
        services = self.overlay(fixtures=self.base / 'fixtures')['services']
        for name in chd.HOSTS:
            service = services[name]
            native = {key: value for key, value in service['environment'].items() if key.startswith('Romd__Native__')}
            expected = {chd.LIBCHDR_KEY: '/app/native/libchdr'}
            if name == 'romd-worker':
                expected[chd.CHDMAN_KEY] = '/app/native/chdman'
            self.assertEqual(expected, native, name)
            self.assertFalse({'entrypoint', 'command', 'image', 'build'} & set(service), name)
            self.assertEqual([{'type': 'bind', 'source': str(self.base / 'data'), 'target': '/var/lib/romd', 'read_only': False}],
                             service['volumes'])
        client = services['e2e-client']
        self.assertTrue(all(volume['read_only'] for volume in client['volumes']))
        self.assertEqual({'/e2e/tls', '/e2e/inputs', '/e2e/fixtures'}, {volume['target'] for volume in client['volumes']})
        self.assertEqual(['edge'], client['networks'])
        self.assertIn('proxy_buffering off', chd.INGRESS_DEVIATION)
        self.assertNotIn('networks', self.overlay())

    def test_synthetic_reference_resolves_only_through_the_worker_mock(self):
        mock = chd.MockRedump('nginx:test', '10.213.7.0/24', '10.213.7.10', self.base / 'c', self.base / 't',
                              self.base / 'p', self.base / 'ca.pem')
        overlay = self.overlay(mock=mock)
        worker = overlay['services']['romd-worker']
        self.assertEqual(['redump.info:10.213.7.10'], worker['extra_hosts'])
        self.assertEqual('/etc/romd-e2e/redump-ca.pem', worker['environment']['SSL_CERT_FILE'])
        self.assertIn('/etc/romd-e2e/redump-ca.pem', {volume['target'] for volume in worker['volumes']})
        self.assertEqual({'backend': {}, 'e2e-redump': {}}, worker['networks'])
        for name in ('romd-admin', 'romd-consumer'):
            self.assertNotIn('SSL_CERT_FILE', overlay['services'][name]['environment'])
        self.assertEqual('10.213.7.10', overlay['services']['e2e-redump']['networks']['e2e-redump']['ipv4_address'])
        self.assertEqual({'internal': True, 'ipam': {'config': [{'subnet': '10.213.7.0/24'}]}}, overlay['networks']['e2e-redump'])
        json.dumps(overlay)  # Compose reads the overlay as JSON-compatible YAML

    def test_subnets_avoid_existing_networks(self):
        self.assertEqual(['10.213.2.0/24', '10.213.4.0/24'],
                         chd.free_subnets(['10.213.0.0/23', '10.213.3.0/24', 'fd00::/64', None, '172.17.0.0/16'], 2))
        with self.assertRaises(chd.HarnessError):
            chd.free_subnets(['10.0.0.0/8'], 1)
        self.assertTrue(all(not chd.SUBNET_POOL.overlaps(ipaddress.ip_network(pool)) for pool in
                            ('172.16.0.0/12', '192.168.0.0/16', '10.42.0.0/16', '10.43.0.0/16')))
        environment = chd.edge_environment('10.213.9.0/24')
        self.assertEqual(('10.213.9.2', '10.213.9.3', '10.213.9.4', '10.213.9.5', '10.213.9.128/25'),
                         tuple(environment[key] for key in ('ROMD_TRUSTED_PROXY', 'ROMD_ADMIN_EDGE_IP', 'ROMD_CONSUMER_EDGE_IP',
                                                            'ROMD_PLAYER_EDGE_IP', 'ROMD_EDGE_DYNAMIC_RANGE')))


class PlumbingTests(unittest.TestCase):
    def test_http_response_skips_interim_continue(self):
        raw = b'HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 202 Accepted\r\nContent-Type: application/json\r\nLocation: /x\r\n\r\n{"jobId":"1"}'
        status, headers, body = chd.parse_http_response(raw)
        self.assertEqual((202, 'application/json', b'{"jobId":"1"}'), (status, headers['content-type'], body))
        status, headers, body = chd.parse_http_response(b'HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n')
        self.assertEqual((200, '4', b''), (status, headers['content-length'], body))

    def test_guest_mountpoint_reads_the_busybox_df_mount(self):
        output = ('Filesystem           1024-blocks    Used Available Capacity Mounted on\n'
                  '/dev/vdb1              102626232 1234567  96000000   2% /mnt/data\n')
        self.assertEqual('/mnt/data', chd.guest_mountpoint(output))
        for broken in ('', 'Filesystem\n', 'Filesystem 1K Used Available Use% Mounted on\nx 1 1 1 1% relative\n'):
            with self.subTest(broken=broken), self.assertRaises(chd.HarnessError):
                chd.guest_mountpoint(broken)

    def test_tool_failures_never_echo_command_lines(self):
        secret = ['docker', 'exec', 'client', 'curl', '-H', 'Authorization: Bearer SECRET-TOKEN', '--data', 'password=P4ss']
        failures = (subprocess.TimeoutExpired(secret, 5), FileNotFoundError(2, 'No such file or directory'))
        for failure in failures:
            with self.subTest(type(failure).__name__), patch.object(chd.subprocess, 'run', side_effect=failure):
                with self.assertRaises(chd.HarnessError) as raised:
                    chd.docker('exec', 'client', 'curl', '-H', 'Authorization: Bearer SECRET-TOKEN', timeout=5)
                message = str(raised.exception)
                self.assertNotIn('SECRET-TOKEN', message)
                self.assertNotIn('Authorization', message)
                self.assertIsNone(raised.exception.__cause__)
                self.assertTrue(raised.exception.__suppress_context__)
        result = subprocess.CompletedProcess(secret, 1, b'', b'daemon unavailable')
        with patch.object(chd.subprocess, 'run', return_value=result), self.assertRaises(chd.HarnessError) as raised:
            chd.docker('exec', 'client', 'curl', '-H', 'Authorization: Bearer SECRET-TOKEN')
        self.assertNotIn('SECRET-TOKEN', str(raised.exception))

    def test_image_refs_cover_every_built_target(self):
        refs = chd.image_refs('e2e-x')
        self.assertEqual({'ROMD_WORKER_IMAGE': 'romd-worker:e2e-x', 'ROMD_ADMIN_IMAGE': 'romd-admin:e2e-x',
                          'ROMD_CONSUMER_IMAGE': 'romd-consumer:e2e-x', 'ROMD_PLAYER_IMAGE': 'romd-player:e2e-x'},
                         chd.image_environment(refs))


if __name__ == '__main__':
    unittest.main()
