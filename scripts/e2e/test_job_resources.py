"""Docker-free behavior checks for the canonical job resources used by the CHD harnesses."""
import copy
import hashlib
import io
import json
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, Mock, patch
from urllib.parse import parse_qs, urlsplit
import zlib

from test_media_facts import DECODER, appended_facts

import chd
import chd_cd
import chd_cd_upgrade
import chd_upgrade


def page(items, cursor=None):
    return {'items': items, 'nextCursor': cursor, 'hasNextPage': cursor is not None}


def identity(data):
    return {'size': len(data), 'sha256': hashlib.sha256(data).hexdigest(),
            'sha1': hashlib.sha1(data).hexdigest(), 'md5': hashlib.md5(data).hexdigest(),
            'crc32': f'{zlib.crc32(data):08x}'}


def completed_selection():
    return {'phase': 'Completed', 'outcome': 'Completed', 'outcomeCode': 'selected-work-complete',
            'requestMode': 'Selection', 'selection': {'filter': 'RetainedRepresentations', 'limit': 1,
                'conversionRequested': False, 'selected': 1, 'created': 1, 'adopted': 0, 'skipped': 0,
                'completed': 1, 'failed': 0, 'enumerationComplete': True}}


class JobItemPaginationTests(unittest.TestCase):
    def test_reads_every_page_using_opaque_cursor_and_canonical_completion_flag(self):
        api = Mock()
        api.json.side_effect = [page([{'id': 1}], 'opaque+/= token'), page([], 'last'), page([{'id': 2}])]
        self.assertEqual([{'id': 1}, {'id': 2}], chd.job_items(api, 'upload'))
        self.assertEqual(3, api.json.call_count)
        for call in api.json.call_args_list:
            self.assertEqual(chd.ADMIN_HOST, call.args[0])
            self.assertEqual('/api/jobs/upload/items', urlsplit(call.args[1]).path)
            self.assertEqual(['100'], parse_qs(urlsplit(call.args[1]).query)['limit'])
        self.assertEqual(['opaque+/= token'], parse_qs(urlsplit(api.json.call_args_list[1].args[1]).query)['cursor'])

    def test_incomplete_or_inconsistent_page_never_returns_partial_success(self):
        cases = (
            {'items': [{'id': 1}], 'hasNextPage': True, 'nextCursor': None},
            {'items': [{'id': 1}], 'hasNextPage': True, 'nextCursor': ''},
            {'items': [{'id': 1}], 'hasNextPage': True, 'nextCursor': 4},
            {'items': [{'id': 1}], 'nextCursor': None},
            {'items': [{'id': 1}], 'hasNextPage': 'false', 'nextCursor': None},
            {'items': None, 'hasNextPage': False, 'nextCursor': None},
            {'items': {'id': 1}, 'hasNextPage': False, 'nextCursor': None},
            {'items': [{'id': 1}], 'hasNextPage': False, 'nextCursor': 'unexpected'},
        )
        for broken in cases:
            with self.subTest(page=broken), self.assertRaises(chd.HarnessError):
                chd.job_items(Mock(json=Mock(return_value=broken)), 'upload')

    def test_repeated_cursor_fails_instead_of_looping_or_truncating(self):
        api = Mock()
        api.json.side_effect = [page([{'id': 1}], 'repeat'), page([{'id': 2}], 'repeat')]
        with self.assertRaises(chd.HarnessError):
            chd.job_items(api, 'upload')
        self.assertEqual(2, api.json.call_count)

    def test_later_page_failure_is_propagated(self):
        api = Mock()
        api.json.side_effect = [page([{'id': 1}], 'next'), chd.HarnessError('second page returned 500')]
        with self.assertRaisesRegex(chd.HarnessError, 'second page returned 500'):
            chd.job_items(api, 'upload')


class ConvergenceDiscoveryTests(unittest.TestCase):
    def test_upload_finds_convergence_on_a_later_page_with_archive_all(self):
        run = object.__new__(chd.Qualification)
        run.step, run.api, run.record = Mock(), Mock(), {}
        run.subject = SimpleNamespace(iso_container_path='/e2e/disc.iso')
        run.settings = SimpleNamespace(upload_timeout=60, recipe='recipe')
        target = {'id': 'convergence', 'jobType': 'media-reconciliation', 'requestMode': 'Subject', 'subject': {'kind': 'LogicalRom', 'id': 'rom'}}

        def request(host, path):
            self.assertEqual(chd.ADMIN_HOST, host)
            self.assertEqual('/api/jobs', urlsplit(path).path)
            query = parse_qs(urlsplit(path).query)
            self.assertEqual(['all'], query.get('archive'))
            self.assertEqual(['100'], query.get('limit'))
            self.assertNotIn('includeArchived', query)
            self.assertIn(query.get('jobType'), (None, ['media-reconciliation']))
            if 'cursor' not in query:
                return page([{'id': 'other', 'jobType': 'media-reconciliation', 'requestMode': 'Subject', 'subject': {'kind': 'Representation', 'id': 'rom'}}], 'next')
            self.assertEqual(['next'], query['cursor'])
            return page([target])

        run.api.json.side_effect = request
        with patch.object(chd, 'upload', return_value='upload'), \
                patch.object(chd, 'wait_for_job', return_value={'romsIngested': 1}), \
                patch.object(chd, 'job_items', return_value=[{'kind': 'rom', 'outcome': 'ingested', 'romFileId': 'rom'}]):
            run.upload_disc()
        self.assertEqual('convergence', run.convergence_id)
        self.assertEqual(2, run.api.json.call_count)
        self.assertEqual('convergence', run.record['reconciliation']['jobId'])


class UpgradeJobResourcesTests(unittest.TestCase):
    def test_settle_waits_for_an_active_other_family_on_page_two(self):
        run = object.__new__(chd_upgrade.UpgradeQualification)
        run.api, run.record = Mock(), {}
        facts = {'pendingDemand': 0}
        run.snapshot = Mock(return_value=facts)
        clock, iterations = [1.0], [0]

        def request(host, path):
            self.assertEqual(chd.ADMIN_HOST, host)
            self.assertEqual('/api/jobs', urlsplit(path).path)
            query = parse_qs(urlsplit(path).query)
            self.assertEqual(['all'], query.get('archive'))
            self.assertEqual(['100'], query.get('limit'))
            self.assertNotIn('jobType', query)
            self.assertNotIn('includeArchived', query)
            if 'cursor' not in query:
                iterations[0] += 1
                return page([{'id': 'convergence', 'jobType': 'media-reconciliation', 'isTerminal': True}], 'other-family')
            self.assertEqual(['other-family'], query['cursor'])
            return page([{'id': 'metadata', 'jobType': 'metadata-rematerialization', 'isTerminal': iterations[0] > 3}])

        run.api.json.side_effect = request
        with patch.object(chd_upgrade.time, 'monotonic', side_effect=lambda: clock[0]), \
                patch.object(chd_upgrade.time, 'sleep', side_effect=lambda seconds: clock.__setitem__(0, clock[0] + seconds)):
            self.assertEqual(facts, run.settle_baseline())
        self.assertGreaterEqual(iterations[0], 12)
        self.assertEqual(['convergence', 'metadata'], [job['id'] for job in run.record['baselineJobs']])

    def qualification(self, assert_job_query=True):
        run = object.__new__(chd_upgrade.UpgradeQualification)
        run.record, run.api, run.stack = {}, Mock(), Mock()
        run.stack.env = {}
        run.stack.inspect.return_value = {'Image': 'image'}
        run.current_refs = chd.image_refs('current')
        run.settings = SimpleNamespace(convergence_timeout=60)
        run.convergence_id, run.chd = 'convergence', {'sha256': 'retained'}
        for name in ('prepare', 'assert_startup_failure', 'start', 'import_catalog', 'upload_disc',
                     'wait_for_released_custody', 'assert_serving_admission', 'step'):
            setattr(run, name, Mock())
        run.teardown = Mock(return_value=None)
        run.assert_activation = Mock(side_effect=lambda **options: run.record.update(decoder=dict(DECODER)))
        run.download_formats = Mock(side_effect=lambda: run.record.update(downloads={}))
        before, after = appended_facts()
        after['verifications'][-1]['Id'] = 'verification'
        after['verificationAttempts'][0]['ResultVerificationId'] = 'verification'
        for row in after['verificationOutputs']:
            if row['VerificationId'] == 'new':
                row['VerificationId'] = 'verification'
        run.settle_baseline = Mock(return_value=before)
        run.snapshot = Mock(side_effect=[before, after])
        unavailable = {kind: {'state': 'unsupported'} for kind in ('original', 'chd')}
        available = {kind: {'state': 'available', 'sha256': 'retained'} for kind in ('original', 'chd')}
        run.admin_formats = Mock(side_effect=[(None, unavailable), (None, available)])
        run.consumer_formats = Mock(side_effect=[(None, unavailable), (None, available)])
        jobs = [{'id': 'upload', 'jobType': 'upload'}, {'id': 'metadata', 'jobType': 'metadata-rematerialization'}]

        def request(host, path, **options):
            self.assertEqual(chd.ADMIN_HOST, host)
            resource, query = urlsplit(path).path, parse_qs(urlsplit(path).query)
            if resource == '/api/jobs':
                if assert_job_query:
                    self.assertEqual(['all'], query.get('archive'))
                    self.assertEqual(['100'], query.get('limit'))
                    self.assertNotIn('jobType', query)
                return page(jobs[:1], 'next') if 'cursor' not in query else page(jobs[1:])
            self.assertEqual('/api/media/reconciliation/selections', resource)
            self.assertEqual('POST', options['method'])
            self.assertEqual((202,), options['expect'])
            self.assertEqual({'requestId', 'filter', 'limit', 'conversionRequested'}, options['payload'].keys())
            self.assertEqual('RetainedRepresentations', options['payload']['filter'])
            self.assertEqual(1, options['payload']['limit'])
            self.assertIs(False, options['payload']['conversionRequested'])
            self.assertTrue(options['payload']['requestId'])
            return {'jobId': 'campaign'}

        run.api.json.side_effect = request
        return run, jobs

    def run_qualification(self, run):
        with patch.object(chd, 'current_decoder_source', return_value=('a' * 40, 2)), \
                patch.object(chd, 'wait_for_job', side_effect=[{'phase': 'Completed', 'outcome': 'Completed'}, completed_selection()]):
            self.assertEqual('passed', run.run()['status'])
        self.assertEqual('campaign', run.record['campaign']['jobId'])
        run.teardown.assert_called_once_with(failed=False)

    def test_upgrade_records_every_job_family_across_all_pages(self):
        run, jobs = self.qualification()
        self.run_qualification(run)
        self.assertEqual(jobs, run.record['beforeCampaignJobs'])

    def test_upgrade_binds_the_new_proof_to_its_owned_stopped_child(self):
        run, _ = self.qualification()
        self.run_qualification(run)
        self.assertEqual('verification', run.record['campaign']['verificationId'])
        self.assertEqual(DECODER, run.record['campaign']['decoder'])
        requests = [call for call in run.api.json.call_args_list if urlsplit(call.args[1]).path == '/api/media/reconciliation/selections']
        self.assertEqual(1, len(requests))


class ReconciliationSelectionTests(unittest.TestCase):
    def test_verification_requires_a_finite_complete_owned_nonconverting_selection(self):
        chd.require_verified_selection(completed_selection())
        mutations = {'filter': 'AllSubjects', 'limit': 2, 'conversionRequested': True, 'selected': 2,
                     'created': 0, 'adopted': 1, 'skipped': 1, 'completed': 0, 'failed': 1, 'enumerationComplete': False}
        for key, value in mutations.items():
            with self.subTest(key=key):
                job = completed_selection()
                job['selection'][key] = value
                with self.assertRaises(chd.HarnessError):
                    chd.require_verified_selection(job)
        for key, value in {'phase': 'Failed', 'outcome': 'CompletedWithErrors', 'requestMode': 'Subject',
                           'outcomeCode': 'other', 'hasErrors': True, 'selection': None}.items():
            with self.subTest(key=key), self.assertRaises(chd.HarnessError):
                chd.require_verified_selection(completed_selection() | {key: value})


class AdmissionIdentityTests(unittest.TestCase):
    def test_dvd_and_cd_admission_read_canonical_self_before_assignment_then_forget_session(self):
        for kind in ('dvd', 'cd'):
            with self.subTest(kind=kind):
                cls = chd.Qualification if kind == 'dvd' else chd_cd.CdQualification
                run = object.__new__(cls)
                run.api, run.step, run.record, run.run_id = Mock(), Mock(), {}, 'run'
                run.settings = SimpleNamespace(recipe='recipe', synthetic=True, checkpoint='encoding')
                original = {'sha256': 'original', 'size': 4}
                run.subject = SimpleNamespace(identity=original, role='dvd')
                run.identities = {'disc.bin': original, 'disc.cue': {'sha256': 'cue'}}
                run.upload_items = [{'kind': 'rom', 'romFileId': 'rom', 'fileName': 'disc.bin'}]
                rom = {'formats': [{'format': 'original', 'state': 'available', 'sha256': 'original', 'sizeBytes': 4}],
                       'matches': [{'titleId': 'title'}]}
                retained = {'format': 'chd', 'state': 'available', 'sha256': 'retained', 'sizeBytes': 2,
                            'filename': 'disc.chd', 'runtimeApplicability': ['pcsx2']}
                if kind == 'dvd':
                    rom['formats'].append(retained)
                    run.admin_formats = Mock(return_value=(rom, {row['format']: row for row in rom['formats']}))
                    run.consumer_formats = Mock(return_value=('release', {'original': rom['formats'][0],
                                                                          'chd': retained | {'contentGrant': {'downloadUrl': '/delivery/content/secret'}}}))
                order = []
                def request(host, path, **options):
                    order.append(('call', host, path))
                    if path == '/api/libraries':
                        return 201, {}, b'{"id":"library"}'
                    if host == chd.CONSUMER_HOST:
                        return 200, {}, b'{"releases":[{"id":"release"}]}'
                    return 200, {}, b'{}'
                def read(host, path):
                    order.append(('json', host, path))
                    if path == '/api/roms/rom':
                        return rom
                    self.assertEqual('/api/me', path)
                    return {'id': 'current-admin'}
                run.api.call.side_effect, run.api.json.side_effect = request, read
                run.api.forget.side_effect = lambda host: order.append(('forget', host))
                with patch.object(chd, 'recipe_measurement', return_value={}):
                    run.assert_serving_admission()
                me = order.index(('json', chd.ADMIN_HOST, '/api/me'))
                assignment = order.index(('call', chd.ADMIN_HOST, '/api/users/current-admin/library'))
                forgotten = order.index(('forget', chd.ADMIN_HOST))
                consumer = order.index(('call', chd.CONSUMER_HOST, '/api/titles/title'))
                self.assertLess(me, assignment)
                self.assertLess(assignment, forgotten)
                self.assertLess(forgotten, consumer)


class OriginalLayoutPaginationTests(unittest.TestCase):
    def qualification(self):
        run = object.__new__(chd_cd.CdQualification)
        run.api, run.step, run.record = Mock(), Mock(), {}
        run.settings = SimpleNamespace(upload_timeout=60)
        return run

    def run_upload(self, run):
        with patch.object(chd, 'upload', return_value='upload'), \
                patch.object(chd, 'wait_for_job', return_value={'hasErrors': False}), \
                patch.object(chd, 'job_items', return_value=[{'id': 'upload-item'}]):
            run.upload_disc()

    def test_cd_upload_finds_original_layout_on_later_page_before_requesting_current_conversion(self):
        run = self.qualification()
        layout = {'id': 'layout', 'sourceJobId': 'upload', 'revision': 'frozen-revision',
                  'dependencies': [{'sha256': 'bound-original'}]}
        def request(host, path, **options):
            self.assertEqual(chd.ADMIN_HOST, host)
            resource, query = urlsplit(path).path, parse_qs(urlsplit(path).query)
            if resource == '/api/media/reconciliation/subjects/OriginalLayout/layout':
                self.assertEqual('POST', options['method'])
                self.assertEqual((202,), options['expect'])
                self.assertIs(True, options['payload']['conversionRequested'])
                self.assertNotIn('revision', options['payload'])
                self.assertTrue(options['payload']['requestId'])
                return {'jobId': 'convergence'}
            self.assertEqual('/api/original-disc-layouts', resource)
            self.assertEqual(['100'], query.get('limit'))
            self.assertEqual(['psx'], query.get('systemKey'))
            return page([{'id': 'unrelated', 'sourceJobId': 'other'}], 'opaque+/= next') if 'cursor' not in query else page([layout])
        run.api.json.side_effect = request
        self.run_upload(run)
        self.assertEqual(layout, run.layout)
        self.assertEqual('convergence', run.convergence_id)
        self.assertEqual({'uploadJobId': 'upload', 'layoutId': 'layout', 'convergenceJobId': 'convergence'}, run.record)
        self.assertEqual(['opaque+/= next'], parse_qs(urlsplit(run.api.json.call_args_list[1].args[1]).query)['cursor'])

    def test_cd_upload_refuses_duplicate_or_incomplete_layout_pages_before_compression(self):
        target = {'id': 'layout', 'sourceJobId': 'upload', 'revision': 'revision', 'dependencies': [{'sha256': 'original'}]}
        cases = ([page([target], 'next'), page([target | {'id': 'duplicate'}])],
                 [page([target], 'repeat'), page([], 'repeat')],
                 [{'items': [target], 'hasNextPage': True, 'nextCursor': None}],
                 [page([target], 'next'), chd.HarnessError('late layout page failed')])
        for pages in cases:
            with self.subTest(pages=pages):
                run = self.qualification()
                run.api.json.side_effect = pages
                with self.assertRaises(chd.HarnessError):
                    self.run_upload(run)
                self.assertEqual(len(pages), run.api.json.call_count)
                self.assertFalse(any(call.kwargs.get('method') == 'POST' for call in run.api.json.call_args_list))


class CdUpgradeJobResourceTests(unittest.TestCase):
    def qualification(self, mismatched_journal=False):
        run = object.__new__(chd_cd_upgrade.CdUpgradeQualification)
        run.api, run.stack, run.record = Mock(), Mock(), {}
        run.stack.env = {}
        run.current_refs = chd.image_refs('current')
        run.settings = SimpleNamespace(convergence_timeout=60)
        run.convergence_id, run.release_id, run.layout = 'convergence', 'release', {'id': 'layout'}
        run.identities, run.roms = {'disc.bin': {'sha256': 'original'}}, [('rom', 'disc.bin')]
        for name in ('prepare', 'assert_startup_failure', 'start', 'import_catalog', 'upload_disc',
                     'wait_for_released_custody', 'assert_serving_admission', 'assert_malformed_isolation', 'step'):
            setattr(run, name, Mock())
        run.teardown = Mock(return_value=None)
        run.assert_activation = Mock(side_effect=lambda **options: run.record.update(decoder=dict(DECODER)))
        run.download_formats = Mock(side_effect=lambda: run.record.update(downloads={}))
        # Capture/provenance is independently tested against PostgreSQL; this fixture owns HTTP resource paging.
        run.capture_archive = Mock()
        before, after = appended_facts()
        after['verifications'][-1]['Id'] = 'verification'
        after['verificationAttempts'][0]['ResultVerificationId'] = 'verification'
        for row in after['verificationOutputs']:
            if row['VerificationId'] == 'new':
                row['VerificationId'] = 'verification'
        if mismatched_journal:
            after['selectionItems'][0]['ChildJobId'] = 'unrelated-child'
        run.snapshot = Mock(side_effect=[before, after])
        run.recover_campaign = Mock(return_value=completed_selection())
        requests, manifests, accepted = [], [0], []
        def request(host, path, **options):
            resource, query = urlsplit(path).path, parse_qs(urlsplit(path).query)
            requests.append((host, resource, options))
            if resource == '/api/releases/release/manifest':
                self.assertEqual(chd.CONSUMER_HOST, host)
                self.assertEqual('POST', options.get('method'))
                manifests[0] += 1
                state = 'available' if manifests[0] == 1 else 'unsupported'
                return {'items': [{'relativePath': 'disc.bin', 'isAvailable': state == 'available',
                                   'formats': [{'format': 'original', 'state': state, 'sha256': 'original'}]}]}
            self.assertEqual(chd.ADMIN_HOST, host)
            if resource == '/api/roms/rom':
                return {'formats': [{'format': 'original', 'state': 'unsupported'}]}
            self.assertEqual('/api/media/reconciliation/selections', resource)
            self.assertEqual('POST', options['method'])
            self.assertEqual((202,), options['expect'])
            self.assertEqual(1, options['payload']['limit'])
            self.assertEqual('RetainedRepresentations', options['payload']['filter'])
            self.assertIs(False, options['payload']['conversionRequested'])
            accepted.append(dict(options['payload']))
            return {'jobId': 'campaign'}
        run.api.json.side_effect = request
        run.api.call.return_value = (409, {}, b'')
        return run, accepted

    def run_upgrade(self, run):
        with patch.object(chd, 'current_decoder_source', return_value=('a' * 40, 2)), \
                patch.object(chd, 'wait_for_job', return_value={'phase': 'Completed', 'outcome': 'Completed'}):
            result = run.run()
        return result

    def test_cd_upgrade_binds_current_journal_and_replays_the_exact_finite_request(self):
        run, accepted = self.qualification()
        self.assertEqual('passed', self.run_upgrade(run)['status'])
        self.assertEqual(2, len(accepted))
        self.assertEqual(accepted[0], accepted[1])
        run.recover_campaign.assert_called_once_with('campaign')
        run.teardown.assert_called_once_with(failed=False)
        self.assertEqual('verification', run.record['campaign']['verificationId'])
        self.assertEqual('campaign', run.record['campaign']['jobId'])

    def test_cd_upgrade_unrelated_child_proof_cannot_publish_success(self):
        run, _ = self.qualification(mismatched_journal=True)
        with self.assertRaisesRegex(ValueError, 'own exactly'):
            self.run_upgrade(run)
        self.assertEqual('failed', run.record['status'])
        self.assertNotIn('campaign', run.record)
        run.teardown.assert_called_once_with(failed=True)


class AdminDownloadTicketTests(unittest.TestCase):
    def test_each_format_issues_a_fresh_rom_ticket_and_redeems_anonymously_with_all_hashes(self):
        original, retained = identity(b'original'), identity(b'chd')
        run = object.__new__(chd.Qualification)
        run.api, run.step, run.record, run.rom_id = Mock(), Mock(), {}, 'rom'
        run.subject = SimpleNamespace(identity=original, dat_identity=original)
        run.chd = {'sizeBytes': retained['size'], 'sha256': retained['sha256']}
        capabilities = [f'/api/download-tickets/{digit * 64}' for digit in ('a', 'b')]
        run.api.json.side_effect = [{'downloadUrl': value} for value in capabilities]
        consumer = {'original': {'contentGrant': {'downloadUrl': '/delivery/content/original'}},
                    'chd': {'contentGrant': {'downloadUrl': '/delivery/content/chd'}}}
        run.consumer_formats = Mock(return_value=('release', consumer))
        run.api.download.side_effect = [dict(facts, host=host, cacheControl='private, no-store', seconds=1.0,
                                             megabytesPerSecond=1.0)
                                       for host in (chd.ADMIN_HOST, chd.CONSUMER_HOST) for facts in (original, retained)]
        run.download_formats()
        self.assertEqual(2, run.api.json.call_count)
        for call, kind in zip(run.api.json.call_args_list, ('original', 'chd')):
            self.assertEqual((chd.ADMIN_HOST, '/api/download-tickets'), call.args)
            self.assertEqual({'method': 'POST', 'payload': {'target': 'Rom', 'id': 'rom', 'format': kind}, 'expect': (201,)}, call.kwargs)
        for call, capability in zip(run.api.download.call_args_list[:2], capabilities):
            self.assertEqual((chd.ADMIN_HOST, capability), call.args)
            self.assertEqual({'authorized': False}, call.kwargs)
        self.assertEqual(2, run.consumer_formats.call_count)
        self.assertEqual({'admin-original', 'admin-chd', 'consumer-original', 'consumer-chd'}, set(run.record['downloads']))
        for label, facts in run.record['downloads'].items():
            expected = original if label.endswith('original') else retained
            self.assertEqual(expected, {key: facts[key] for key in expected})
        self.assertNotIn('download-tickets', json.dumps(run.record))
        self.assertNotIn(capabilities[0], str(run.step.call_args_list))

    def test_invalid_ticket_response_fails_without_exposing_capability(self):
        for response in ({}, {'downloadUrl': None}, {'downloadUrl': 'https://evil.example/api/download-tickets/secret'},
                         {'downloadUrl': '/api/download-tickets/secret?extra=private'}):
            run = object.__new__(chd.Qualification)
            run.api, run.step, run.record, run.rom_id = Mock(), Mock(), {}, 'rom'
            run.api.json.return_value = response
            run.verify_download = Mock(return_value={})
            run.consumer_formats = Mock(return_value=('release', {kind: {'contentGrant': {'downloadUrl': '/delivery/content/grant'}}
                                                                  for kind in ('original', 'chd')}))
            with self.subTest(response=response):
                with self.assertRaises(chd.HarnessError) as raised:
                    run.download_formats()
                self.assertNotIn('secret', str(raised.exception))
                run.api.download.assert_not_called()

    def test_stream_failures_redact_admin_and_consumer_capabilities(self):
        token = 'a' * 64
        for host, path, label in ((chd.ADMIN_HOST, f'/api/download-tickets/{token}', '/api/download-tickets/<ticket>'),
                                  (chd.CONSUMER_HOST, f'/delivery/content/{token}', '/delivery/content/<grant>')):
            with self.subTest(host=host):
                api = chd.Api(Mock(), '127.0.0.1', 'email', 'password')
                process = MagicMock()
                process.__enter__.return_value = process
                process.stdout = io.BytesIO(b'partial')
                process.wait.return_value = 7
                with patch.object(chd.subprocess, 'Popen', return_value=process) as popen, self.assertRaises(chd.HarnessError) as raised:
                    api.download(host, path, authorized=False)
                self.assertNotIn(token, str(raised.exception))
                self.assertIn(label, str(raised.exception))
                self.assertNotIn('Authorization', str(popen.call_args))

    def test_ordinary_api_failures_keep_their_response_diagnostics(self):
        api = chd.Api(Mock(), '127.0.0.1', 'email', 'password')
        api.token = Mock(return_value='bearer')
        api.raw = Mock(return_value=(409, {}, b'{"code":"Jobs.NotTerminal"}'))
        with self.assertRaisesRegex(chd.HarnessError, 'Jobs.NotTerminal'):
            api.json(chd.ADMIN_HOST, '/api/jobs/job')

    def test_unexpected_ticket_status_does_not_echo_a_capability_response(self):
        token = 'a' * 64
        api = chd.Api(Mock(), '127.0.0.1', 'email', 'password')
        api.token = Mock(return_value='bearer')
        api.raw = Mock(return_value=(200, {}, json.dumps({'downloadUrl': f'/api/download-tickets/{token}'}).encode()))
        with self.assertRaises(chd.HarnessError) as raised:
            api.json(chd.ADMIN_HOST, '/api/download-tickets', method='POST',
                     payload={'target': 'Rom', 'id': 'rom', 'format': 'original'}, expect=(201,))
        self.assertIn('returned 200', str(raised.exception))
        self.assertNotIn(token, str(raised.exception))


if __name__ == '__main__':
    unittest.main()
