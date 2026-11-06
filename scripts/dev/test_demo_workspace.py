from contextlib import redirect_stderr, redirect_stdout
import hashlib
import io
import json
from pathlib import Path
import shutil
import stat
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import demo_workspace as workspace
import demo


class WorkspaceTests(unittest.TestCase):
    def fixture(self, root):
        source = root / 'collection'
        path = source / 'roms/Game Boy/Selected (World).zip'
        path.parent.mkdir(parents=True)
        with zipfile.ZipFile(path, 'w') as archive:
            archive.writestr('Selected (World).gb', b'cartridge')
        (path.parent / 'Alphabetically First.zip').write_bytes(b'not part of the recipe')
        bios = source / 'bios/system/boot.bin'
        bios.parent.mkdir(parents=True)
        bios.write_bytes(b'firmware')
        profile = dict(schemaVersion=1, name='fixture', catalogSite='https://publisher.invalid',
            roms=[dict(system='gb', source=str(path.relative_to(source)), purpose='explicit test selection')],
            bios=[dict(system='gb', source=str(bios.relative_to(source)), size=8,
                       md5=hashlib.md5(b'firmware').hexdigest(), purpose='verified firmware')])
        return source, profile

    def test_preparation_uses_only_exact_recipe_entries_and_checks_bios(self):
        with tempfile.TemporaryDirectory() as directory:
            source, profile = self.fixture(Path(directory))
            before = {p: p.read_bytes() for p in source.rglob('*') if p.is_file()}
            selected = workspace.selection(source, profile)
            self.assertEqual(len(selected), 2)
            self.assertEqual(selected[0]['filename'], 'Selected (World).gb')
            self.assertEqual(selected[0]['payloadSha1'], hashlib.sha1(b'cartridge').hexdigest())
            self.assertEqual(before, {p: p.read_bytes() for p in before})
            profile['bios'][0]['md5'] = '0' * 32
            with self.assertRaisesRegex(workspace.manage.SetupError, 'BIOS'):
                workspace.selection(source, profile)

    def test_missing_selected_file_fails_without_choosing_other_available_roms(self):
        with tempfile.TemporaryDirectory() as directory:
            source, profile = self.fixture(Path(directory))
            (source / profile['roms'][0]['source']).unlink()
            with self.assertRaisesRegex(workspace.manage.SetupError, 'Missing'):
                workspace.selection(source, profile)

    def test_manifest_paths_links_and_changed_files_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'asset').write_bytes(b'original')
            (root / 'alias').symlink_to(root / 'asset')
            for relative in ('../asset', '/asset', 'alias', 'windows\\asset'):
                with self.subTest(relative=relative), self.assertRaises(workspace.manage.SetupError):
                    workspace.read(root, relative)
            with self.assertRaisesRegex(workspace.manage.SetupError, 'hash mismatch'):
                workspace.read(root, 'asset', '0' * 64)
            with self.assertRaisesRegex(workspace.manage.SetupError, 'overwrite'):
                workspace.retain(root, 'asset', b'changed')
            self.assertEqual((root / 'asset').read_bytes(), b'original')

    def test_zip_traversal_multiple_members_and_oversize_are_refused(self):
        for names in (['../outside.gb'], ['first.gb', 'second.gb'], ['/absolute.gb']):
            data = io.BytesIO()
            with zipfile.ZipFile(data, 'w') as archive:
                for name in names:
                    archive.writestr(name, b'data')
            with self.subTest(names=names), self.assertRaises(workspace.manage.SetupError):
                workspace.cartridge(data.getvalue())
        with patch.object(workspace, 'MAX_TOTAL', 1), tempfile.TemporaryDirectory() as directory:
            source, profile = self.fixture(Path(directory))
            with self.assertRaisesRegex(workspace.manage.SetupError, '128 MiB'):
                workspace.selection(source, profile)

    def test_bad_dat_match_never_replaces_existing_workspace_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, profile = self.fixture(root)
            base = root / '.demo'
            base.mkdir()
            old = b'{"preserve":"previous manifest"}\n'
            (base / 'manifest.json').write_bytes(old)
            profile_path = root / 'profile.json'
            profile_path.write_text(json.dumps(profile))
            data = b'<datafile><header><name>Game Boy</name></header></datafile>'
            path = workspace.retain(base, 'dats/gb/full.dat', data)
            definitions = [dict(system='gb', path=path, sha256=workspace.sha(data), name='Game Boy')]
            with patch.object(workspace, 'catalogs', return_value=(definitions, {})), \
                 self.assertRaisesRegex(workspace.manage.SetupError, 'DAT match'):
                workspace.prepare(source, refresh=True, root=root, profile=profile_path)
            self.assertEqual((base / 'manifest.json').read_bytes(), old)

    def test_example_profile_is_a_neutral_template_that_validates_but_selects_nothing_real(self):
        text = workspace.EXAMPLE.read_text()
        profile = workspace.validate_profile(json.loads(text))
        self.assertEqual(profile['bios'], [])
        self.assertTrue(all('Example' in row['source'] and 'Placeholder' in row['purpose'] for row in profile['roms']))
        self.assertNotRegex(text, r'[0-9a-f]{32}')  # No firmware or content hashes.
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(workspace.manage.SetupError, 'Missing.*Example Homebrew Title'):
                workspace.selection(Path(directory), profile)

    def test_invalid_profiles_are_rejected_before_any_selection(self):
        with tempfile.TemporaryDirectory() as directory:
            _, valid = self.fixture(Path(directory))
        cases = [dict(valid, schemaVersion=2), dict(valid, roms=[]), {k: v for k, v in valid.items() if k != 'bios'},
                 dict(valid, catalogSite='http://publisher.invalid'),
                 dict(valid, roms=[dict(valid['roms'][0], source='Selected (World).zip')]),
                 dict(valid, bios=[{k: v for k, v in valid['bios'][0].items() if k != 'md5'}]),
                 dict(valid, bios=[dict(valid['bios'][0], size='8')])]
        for profile in cases:
            with self.subTest(profile=profile), self.assertRaises(workspace.manage.SetupError):
                workspace.validate_profile(profile)
        self.assertIs(workspace.validate_profile(valid), valid)

    def test_profile_defaults_to_the_workspace_and_accepts_an_explicit_override(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, profile = self.fixture(root)
            def plan(*extra):
                with redirect_stdout(io.StringIO()) as output:
                    workspace.main(['--from', str(source), '--plan', *extra], root=root)
                return json.loads(output.getvalue())
            for arguments in ([], ['--profile', str(root / 'absent.json')]):
                with self.subTest(arguments=arguments), \
                        self.assertRaisesRegex(workspace.manage.SetupError,
                                               r'No demo profile.*demo-profile\.example\.json.*demo-data\.md'):
                    plan(*arguments)
            self.assertFalse((root / '.demo').exists())  # A plan never creates the workspace.
            (root / '.demo').mkdir()
            (root / '.demo/profile.json').write_text(json.dumps(profile))
            self.assertEqual(plan()['profile'], 'fixture')
            override = root / 'elsewhere.json'
            override.write_text(json.dumps(dict(profile, name='override', bios=[])))
            result = plan('--profile', str(override))
            self.assertEqual((result['profile'], len(result['files'])), ('override', 1))
            with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as exit:
                workspace.main(['--from-workspace', str(root / 'other/.demo'), '--profile', str(override)], root=root)
            self.assertEqual(exit.exception.code, 2)

    def test_prepared_workspace_is_reused_and_refreshed_without_any_profile(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, profile = self.fixture(root)
            (root / '.demo').mkdir()
            (root / '.demo/profile.json').write_text(json.dumps(profile))
            data = ('<datafile><header><name>Game Boy</name></header><game name="Selected (World)">'
                    f'<rom name="Selected (World).gb" size="9" sha1="{hashlib.sha1(b"cartridge").hexdigest()}"/>'
                    '</game></datafile>').encode()
            recipes = []
            def catalogs(_, recipe):
                recipes.append(recipe)
                path = workspace.retain(root / '.demo', 'dats/gb/full.dat', data)
                return [dict(system='gb', path=path, sha256=workspace.sha(data), name='Game Boy')], {}
            with patch.object(workspace, 'catalogs', side_effect=catalogs), patch.object(workspace, 'reader', return_value={}), \
                    redirect_stdout(io.StringIO()):
                manifest = workspace.prepare(source, root=root)  # Default .demo/profile.json.
                self.assertEqual(manifest['profileSha256'], workspace.sha((root / '.demo/profile.json').read_bytes()))
                # A changed profile is never silently paired with the staged selection.
                (root / '.demo/profile.json').write_text(json.dumps(dict(profile, name='changed')))
                for refresh in (False, True):
                    with self.subTest(refresh=refresh), self.assertRaisesRegex(workspace.manage.SetupError, 'profile changed'):
                        workspace.prepare(root=root, refresh=refresh)
                (root / '.demo/profile.json').unlink()
                shutil.rmtree(source)
                self.assertEqual(workspace.load(root)[0], manifest)
                self.assertEqual(workspace.prepare(root=root, plan=True), manifest)
                self.assertEqual(workspace.prepare(root=root), manifest)
                refreshed = workspace.prepare(root=root, refresh=True)
            self.assertEqual(refreshed, manifest)  # The manifest format and recipe identity are unchanged.
            self.assertEqual(recipes[1]['catalogSite'], profile['catalogSite'])
            self.assertEqual([row['source'] for row in recipes[1]['roms']], [profile['roms'][0]['source']])

    def test_prepared_assets_work_after_original_collection_is_removed_and_reruns_keep_frozen_dats(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source, profile = self.fixture(root)
            profile_path = root / 'profile.json'
            profile_path.write_text(json.dumps(profile))
            data = ('<datafile><header><name>Game Boy</name></header><game name="Selected (World)">'
                    f'<rom name="Selected (World).gb" size="9" sha1="{hashlib.sha1(b"cartridge").hexdigest()}"/>'
                    '</game></datafile>').encode()
            def catalogs(*_):
                path = workspace.retain(root / '.demo', 'dats/gb/full.dat', data)
                return [dict(system='gb', path=path, sha256=workspace.sha(data), name='Game Boy')], {}
            with patch.object(workspace, 'catalogs', side_effect=catalogs) as fetch, \
                 patch.object(workspace, 'reader', return_value={}):
                workspace.prepare(source, root=root, profile=profile_path)
                source.rename(root / 'originals-no-longer-available')
                manifest, entries, _ = workspace.load(root)
                self.assertEqual(len(entries), 2)
                self.assertEqual(entries[0]['data'], b'cartridge')
                self.assertTrue(entries[0]['archive'].startswith(b'PK'))
                self.assertTrue(all(entry['source'].startswith(('roms/', 'bios/')) for entry in entries))
                with patch.object(workspace, 'reader', side_effect=AssertionError('plan must not build/fetch tools')):
                    self.assertEqual(workspace.prepare(root=root, plan=True), manifest)
                self.assertEqual(workspace.prepare(root=root), manifest)
                fetch.assert_called_once()

    def test_unverified_reader_archive_never_reaches_build_execution(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'Dockerfile').write_text((workspace.manage.ROOT / 'Dockerfile').read_text())
            with patch.object(workspace, 'urlopen', return_value=io.BytesIO(b'unverified source')), \
                 patch.object(workspace, 'command') as build, \
                 self.assertRaisesRegex(workspace.manage.SetupError, 'source archive hash mismatch'):
                workspace.reader(root)
            build.assert_not_called()

    def test_reader_source_go_and_independent_trust_root_use_production_pins(self):
        pins = workspace.reader_pins(workspace.manage.ROOT)
        self.assertIn(pins['sourceCommit'], pins['sourceUrl'])
        self.assertEqual(len(pins['rootSha256']), 64)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'Dockerfile').write_text('FROM golang:latest')
            with self.assertRaisesRegex(workspace.manage.SetupError, 'reviewed catalog-reader pins'):
                workspace.reader_pins(root)

    def test_subscription_helper_retains_previews_and_never_calls_apply(self):
        with tempfile.TemporaryDirectory() as directory:
            seeder = object.__new__(demo.Seeder)
            seeder.directory = Path(directory)
            seeder.catalogs = {'gb': {'catalogId': 'no-intro/gb/standard'}}
            calls = []
            def call(path, method='GET', value=None):
                calls.append((path, method, value))
                if path == '/api/dat-catalogs':
                    return dict(enabled=True, catalogs=[{'catalogId': 'no-intro/gb/standard'}])
                if path == '/api/dat-subscriptions':
                    return [] if method == 'GET' else dict(id='sub', state='ReadyToImport')
                return dict(activeSha256='', candidateSha256='a' * 64)
            from unittest.mock import Mock
            seeder.api = Mock()
            seeder.api.call.side_effect = call
            result = seeder.subscriptions()
            self.assertEqual(result[0]['subscription']['state'], 'ReadyToImport')
            self.assertIn(('/api/dat-subscriptions', 'POST', {'catalogId': 'no-intro/gb/standard'}), calls)
            self.assertIn(('/api/dat-subscriptions/sub/preview', 'GET', None), calls)
            self.assertFalse(any('/apply' in path for path, _, _ in calls))
            self.assertTrue((Path(directory) / 'subscription-review.json').exists())

    def test_subscription_helper_rechecks_an_existing_subscription_instead_of_enrolling_again(self):
        with tempfile.TemporaryDirectory() as directory:
            seeder = object.__new__(demo.Seeder)
            seeder.directory = Path(directory)
            seeder.catalogs = {'gb': {'catalogId': 'no-intro/gb/standard'}}
            calls = []
            def call(path, method='GET', value=None):
                calls.append((path, method, value))
                if path == '/api/dat-catalogs':
                    return dict(enabled=True, catalogs=[{'catalogId': 'no-intro/gb/standard'}])
                if path == '/api/dat-subscriptions':
                    return [dict(id='sub', catalogId='no-intro/gb/standard')]
                if path.endswith('/check'):
                    return dict(id='sub', state='UpToDate')
                return dict(activeSha256='', candidateSha256='a' * 64)
            from unittest.mock import Mock
            seeder.api = Mock()
            seeder.api.call.side_effect = call
            result = seeder.subscriptions()
            self.assertEqual(result[0]['preview'], None)
            self.assertIn(('/api/dat-subscriptions/sub/check', 'POST', None), calls)
            self.assertFalse(any(method == 'POST' and path == '/api/dat-subscriptions' for path, method, _ in calls))

    def test_bios_verification_checks_real_delivery_bytes_and_never_records_grants(self):
        with tempfile.TemporaryDirectory() as directory:
            from unittest.mock import Mock
            seeder = object.__new__(demo.Seeder)
            seeder.directory = Path(directory)
            seeder.urls = {'consumer_api': 'http://localhost:5002'}
            seeder.entries = [demo.Entry('gb', 'Firmware', 'bios.bin', b'firmware', 'bios/test.bin', bios=True)]
            consumer = Mock()
            consumer.call.return_value = {'items': [dict(isAvailable=True, sha256=workspace.sha(b'firmware'),
                contentGrant={'downloadUrl': '/delivery/bios/private-grant'})]}
            with patch.object(demo, 'Api') as download:
                download.return_value.call.return_value = b'firmware'
                seeder.verify_bios(consumer)
                download.return_value.call.assert_called_once_with('/delivery/bios/private-grant', raw=True)
            self.assertNotIn('private-grant', (seeder.directory / 'bios-verification.json').read_text())
            consumer.call.return_value['items'][0]['contentGrant']['downloadUrl'] = 'https://foreign/delivery/bios/private-grant'
            with patch.object(demo, 'Api') as download, self.assertRaisesRegex(workspace.manage.SetupError, 'outside'):
                seeder.verify_bios(consumer)
            download.assert_not_called()
            self.assertNotIn('private-grant', str(demo.ApiError('GET', '/delivery/bios/private-grant', 401)))

    def test_up_to_date_subscription_is_reused_without_requesting_an_unavailable_preview(self):
        with tempfile.TemporaryDirectory() as directory:
            from unittest.mock import Mock
            seeder = object.__new__(demo.Seeder)
            seeder.directory = Path(directory)
            seeder.catalogs = {'gb': {'catalogId': 'no-intro/gb/standard'}}
            seeder.api = Mock()
            seeder.api.call.side_effect = [dict(enabled=True, catalogs=[{'catalogId': 'no-intro/gb/standard'}]), [],
                                           dict(id='sub', state='UpToDate')]
            result = seeder.subscriptions()
            self.assertIsNone(result[0]['preview'])
            self.assertEqual(seeder.api.call.call_count, 3)


class CopyWorkspaceTests(unittest.TestCase):
    """Reuse another checkout's prepared inputs without touching its receipts, cache or bytes."""

    def prepared(self, parent, name, payload=b'cartridge'):
        root = parent / name
        source = root / 'collection'
        rom = source / 'roms/Game Boy/Selected (World).zip'
        rom.parent.mkdir(parents=True)
        with zipfile.ZipFile(rom, 'w') as archive:
            archive.writestr('Selected (World).gb', payload)
        bios = source / 'bios/boot.bin'
        bios.parent.mkdir(parents=True)
        bios.write_bytes(b'firmware')
        profile = dict(schemaVersion=1, name='fixture', catalogSite='https://publisher.invalid',
            roms=[dict(system='gb', source=str(rom.relative_to(source)), purpose='explicit test selection')],
            bios=[dict(system='gb', source=str(bios.relative_to(source)), size=8,
                       md5=hashlib.md5(b'firmware').hexdigest(), purpose='verified firmware')])
        profile_path = parent / (name + '-profile.json')
        profile_path.write_text(json.dumps(profile))
        data = ('<datafile><header><name>Game Boy</name></header><game name="Selected (World)">'
                f'<rom name="Selected (World).gb" size="{len(payload)}" sha1="{hashlib.sha1(payload).hexdigest()}"/>'
                '</game></datafile>').encode()
        def catalogs(*_):
            path = workspace.retain(root / '.demo', 'dats/gb/full.dat', data)
            return [dict(system='gb', path=path, sha256=workspace.sha(data), name='Game Boy')], {}
        with patch.object(workspace, 'catalogs', side_effect=catalogs), \
             patch.object(workspace, 'reader', return_value={}), redirect_stdout(io.StringIO()):
            workspace.prepare(source, root=root, profile=profile_path)
        shutil.rmtree(source)  # The copy must never need the original collection.
        # Runtime receipts and TUF state belong to the source checkout only.
        workspace.retain(root / '.demo', 'state/fingerprint/state.json', b'{"private": "receipt"}')
        workspace.retain(root / '.demo', 'catalog-cache/metadata/timestamp.json', b'{"version": 7}')
        return root

    @staticmethod
    def snapshot(base):
        return {str(p.relative_to(base)): (p.read_bytes() if p.is_file() else None) for p in base.rglob('*')}

    @staticmethod
    def copy(source, destination):
        with redirect_stdout(io.StringIO()) as output:
            result = workspace.copy_workspace(source, destination)
        return result, output.getvalue()

    def test_copy_reverifies_declared_inputs_only_and_leaves_source_unchanged(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            source = self.prepared(parent, 'main') / '.demo'
            destination = parent / 'worktree'
            destination.mkdir()
            before = self.snapshot(source)
            manifest, output = self.copy(source, destination)
            self.assertEqual(self.snapshot(source), before)
            self.assertIn('no seeding receipts or TUF cache', output)
            self.assertFalse((destination / '.demo/state').exists())
            self.assertFalse((destination / '.demo/catalog-cache').exists())
            self.assertEqual(json.loads((destination / '.demo/manifest.json').read_text()), manifest)
            self.assertEqual(json.loads((destination / '.demo/dats/gb/full.json').read_text()), manifest['catalogs'][0])
            _, entries, _ = workspace.load(destination)
            self.assertEqual(entries[0]['data'], b'cartridge')
            self.assertEqual(stat.S_IMODE((destination / '.demo').stat().st_mode), 0o700)
            self.assertFalse(list((destination / '.demo').rglob('*.tmp')))
            # A rerun with identical inputs is a verified no-op that keeps local receipts.
            workspace.retain(destination / '.demo', 'state/local/state.json', b'{}')
            _, output = self.copy(source, destination)
            self.assertIn('nothing copied', output)
            self.assertTrue((destination / '.demo/state/local/state.json').exists())

    def test_same_link_missing_misnamed_and_unverified_sources_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            main = self.prepared(parent, 'main')
            destination = parent / 'worktree'
            destination.mkdir()
            (parent / 'linked').mkdir()
            (parent / 'linked/.demo').symlink_to(main / '.demo')
            (parent / 'renamed').symlink_to(main / '.demo')
            (parent / 'empty/.demo').mkdir(parents=True)
            cases = [(main / '.demo', main, 'own .demo'), (parent / 'linked/.demo', destination, 'not a link'),
                     (parent / 'renamed', destination, 'not a link'), (parent / 'empty/.demo', destination, 'No prepared'),
                     (parent / 'absent/.demo', destination, 'No prepared')]
            for source, target, message in cases:
                with self.subTest(source=str(source)), self.assertRaisesRegex(workspace.manage.SetupError, message):
                    workspace.copy_workspace(source, target)
            self.assertFalse((destination / '.demo').exists())
            rom = next((main / '.demo/roms').rglob('*.zip'))
            rom.write_bytes(rom.read_bytes() + b'tampered')
            with self.assertRaisesRegex(workspace.manage.SetupError, 'hash mismatch'):
                workspace.copy_workspace(main / '.demo', destination)
            self.assertFalse((destination / '.demo').exists())

    def test_existing_workspace_with_different_fingerprint_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            main = self.prepared(parent, 'main')
            other = self.prepared(parent, 'other', payload=b'another cartridge')
            before = self.snapshot(other / '.demo')
            self.assertNotEqual(workspace.fingerprint(workspace.load(main)[0]),
                                workspace.fingerprint(workspace.load(other)[0]))
            with self.assertRaisesRegex(workspace.manage.SetupError, 'different prepared .demo'):
                workspace.copy_workspace(main / '.demo', other)
            for arguments in (['--plan'], []):
                with self.subTest(arguments=arguments), redirect_stdout(io.StringIO()), \
                        self.assertRaisesRegex(workspace.manage.SetupError, 'refusing to overwrite'):
                    workspace.main(['--from-workspace', str(main / '.demo'), *arguments], root=other)
            after = self.snapshot(other / '.demo')
            self.assertEqual(after.pop('seed.lock'), b'')  # Only the ordinary lock was added.
            self.assertEqual(after, before)

    def test_different_curation_profile_in_destination_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            main = self.prepared(parent, 'main')
            destination = parent / 'worktree'
            (destination / '.demo').mkdir(parents=True)
            (destination / '.demo/profile.json').write_text('{"name": "changed recipe"}')
            with self.assertRaisesRegex(workspace.manage.SetupError, 'different curation profile'):
                workspace.copy_workspace(main / '.demo', destination)
            self.assertEqual([path.name for path in (destination / '.demo').iterdir()], ['profile.json'])

    def test_failed_copy_removes_everything_it_created_and_keeps_existing_cache(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            source = self.prepared(parent, 'main') / '.demo'
            fresh, existing = parent / 'fresh', parent / 'existing'
            fresh.mkdir()
            workspace.retain(existing / '.demo', 'catalog-cache/root.json', b'{"keep": true}')
            before = self.snapshot(existing / '.demo')
            original = workspace.retain
            for target in (fresh, existing):
                calls = []
                def failing(root, relative, data):
                    calls.append(relative)
                    if len(calls) == 2:
                        raise workspace.manage.SetupError('disk full')
                    return original(root, relative, data)
                with self.subTest(target=target.name), patch.object(workspace, 'retain', side_effect=failing), \
                        self.assertRaisesRegex(workspace.manage.SetupError, 'disk full'):
                    workspace.main(['--from-workspace', str(source)], root=target)
            self.assertFalse((fresh / '.demo').exists())
            self.assertEqual(self.snapshot(existing / '.demo').keys() - {'seed.lock'}, before.keys())
            self.assertFalse((existing / '.demo/manifest.json').exists())
            # A failure after the manifest commit point is rolled back as well.
            verified = workspace.load(source.parent)
            with patch.object(workspace, 'load', side_effect=[verified, workspace.manage.SetupError('verification failed')]), \
                    redirect_stdout(io.StringIO()), self.assertRaisesRegex(workspace.manage.SetupError, 'verification failed'):
                workspace.copy_workspace(source, fresh)
            self.assertFalse((fresh / '.demo').exists())

    def test_copy_is_exclusive_with_collection_preparation_and_refresh_and_plan_writes_nothing(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            source = self.prepared(parent, 'main') / '.demo'
            destination = parent / 'worktree'
            destination.mkdir()
            for arguments in (['--from', '/collection', '--from-workspace', str(source)],
                              ['--from-workspace', str(source), '--refresh']):
                with self.subTest(arguments=arguments), redirect_stderr(io.StringIO()), \
                        self.assertRaises(SystemExit) as exit:
                    workspace.main(arguments, root=destination)
                self.assertEqual(exit.exception.code, 2)
            with redirect_stdout(io.StringIO()) as output:
                workspace.main(['--from-workspace', str(source), '--plan'], root=destination)
            plan = json.loads(output.getvalue())
            self.assertEqual((plan['destinationState'], plan['files'], plan['catalogs']), ('absent', 2, 1))
            self.assertIn('catalog-cache/', plan['notCopied'])
            self.assertFalse((destination / '.demo').exists())
            with redirect_stdout(io.StringIO()):
                workspace.main(['--from-workspace', str(source)], root=destination)
            self.assertTrue((destination / '.demo/manifest.json').exists())
