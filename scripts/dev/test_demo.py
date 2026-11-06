import io
import json
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import Mock, patch
from urllib.parse import parse_qs, urlsplit
import xml.etree.ElementTree as ET
import zipfile

import demo


class DemoTests(unittest.TestCase):
    def test_synthetic_dataset_is_stable_and_has_owned_missing_and_bios_cases(self):
        first, second = demo.dataset(), demo.dataset()
        self.assertEqual(demo.fingerprint(first), demo.fingerprint(second))
        self.assertEqual(len(first), 10)
        self.assertEqual(sum(e.present and not e.bios for e in first), 6)
        self.assertEqual(sum(not e.present for e in first), 3)
        self.assertEqual(sum(e.bios for e in first), 1)
        for system in {e.system for e in first}:
            self.assertIn(system, json.loads((demo.manage.ROOT / 'reference-data/catalog/systems.json').read_text()))

    def test_dat_hashes_and_bios_flag_match_exact_payloads(self):
        entries = demo.dataset()
        dat = ET.fromstring(demo.dat_bytes(entries, 'fixture & <catalog>'))
        self.assertEqual(dat.findtext('header/name'), 'fixture & <catalog>')
        for entry, game in zip(entries, dat.findall('game')):
            self.assertEqual(game.attrib.get('isbios') == 'yes', entry.bios)
            self.assertEqual(game.find('rom').attrib['sha1'], demo.hashlib.sha1(entry.data).hexdigest())
            self.assertEqual(game.find('rom').attrib['crc'], f'{demo.zlib.crc32(entry.data):08x}')

    def test_demo_library_explicitly_allows_unrated_fixtures_without_changing_default_policy(self):
        config = demo.library_configuration(['b', 'a'])
        self.assertEqual(config['contentRatingPolicy'], {'unknownRatingPolicy': 'Allow'})
        self.assertEqual(config['includeTitleIds'], ['a', 'b'])
        self.assertEqual(config['titleSelectionMode'], 'IncludeOnly')
        self.assertTrue(config['showMissingGames'])

    def test_session_requires_live_ready_checkout_and_loopback_source_endpoints(self):
        root = Path('/checkout')
        valid = dict(active=True, mode='source', state='ready', checkout=str(root),
                     urls={key: f'http://localhost:{port}' for key, port in demo.manage.port_settings('source', root=root).items()})
        cases = [dict(active=False), dict(mode='containers'), dict(state='stopped'), dict(checkout='/other'),
                 dict(urls=valid['urls'] | {'admin_api': 'http://remote:5000'}),
                 dict(urls=valid['urls'] | {'admin_api': 'http://user@localhost:5000'})]
        for change in cases:
            with self.subTest(change=change), patch.object(demo.manage, 'session_status', return_value=valid | change), \
                    self.assertRaises(demo.manage.SetupError):
                demo.managed_session(root)
        with patch.object(demo.manage, 'session_status', return_value=valid):
            self.assertEqual(demo.managed_session(root), valid)

    def test_api_does_not_follow_redirects_or_log_error_body_or_token(self):
        api = demo.Api('http://localhost:5000', 'private-token')
        error = demo.HTTPError('http://localhost:5000', 302, 'private-response', {}, io.BytesIO(b'private-data'))
        api.opener = Mock()
        api.opener.open.side_effect = error
        with self.assertRaisesRegex(demo.ApiError, 'HTTP 302') as result:
            api.call('/api/users')
        self.assertNotIn('private', str(result.exception))
        with self.assertRaises(demo.manage.SetupError):
            api.call('//remote/path')

    def test_upload_reuses_accepted_job_and_persists_before_wait(self):
        with tempfile.TemporaryDirectory() as temporary:
            seeder = object.__new__(demo.Seeder)
            seeder.path = Path(temporary) / 'state.json'
            seeder.state = dict(jobs={})
            seeder.api = Mock()
            seeder.api.call.return_value = dict(jobId='accepted-job')
            def timeout(job):
                self.assertEqual(json.loads(seeder.path.read_text())['jobs']['gb:payload'], job)
                raise demo.manage.SetupError('interrupted wait')
            seeder.wait_job = Mock(side_effect=timeout)
            for _ in range(2):
                with self.assertRaisesRegex(demo.manage.SetupError, 'interrupted wait'):
                    seeder.upload('gb:payload', 'gb', 'cartridge.gb', b'data')
            seeder.api.call.assert_called_once()
            self.assertEqual(stat.S_IMODE(seeder.path.stat().st_mode), 0o600)

    def test_job_with_rejections_or_terminal_errors_is_not_success(self):
        for changes in ({'romsRejected': 1}, {'hasErrors': True}, {'phase': 'CompletedWithErrors'}):
            seeder = object.__new__(demo.Seeder)
            seeder.timeout = 10
            seeder.api = Mock()
            seeder.api.call.return_value = dict(isTerminal=True, phase='Completed', hasErrors=False) | changes
            with self.assertRaises(demo.manage.SetupError):
                seeder.wait_job('failed-job')

    def test_fixture_cover_is_a_valid_png_without_downloads(self):
        data = demo.cover(0)
        self.assertTrue(data.startswith(b'\x89PNG\r\n\x1a\n'))
        self.assertEqual(demo.cover(0), data)
        self.assertNotEqual(demo.cover(1), data)


def page(items, cursor=None):
    return dict(items=items, nextCursor=cursor, hasNextPage=cursor is not None)


class DemoPaginationTests(unittest.TestCase):
    def seeder(self):
        seeder = object.__new__(demo.Seeder)
        seeder.api = Mock()
        seeder.state = dict(jobs={})
        seeder.upload = Mock()
        seeder.timeout = 1
        return seeder

    def test_dat_reuse_finds_later_page_and_verifies_frozen_bytes_before_upload(self):
        seeder = self.seeder()
        data = b'frozen immutable DAT'
        def request(path, *args, **options):
            target = urlsplit(path)
            if target.path == '/api/dats/installed/download':
                self.assertEqual(dict(raw=True), options)
                return data
            self.assertEqual('/api/dats', target.path)
            query = parse_qs(target.query)
            self.assertEqual(['100'], query.get('limit'))
            if 'cursor' not in query:
                return page([dict(id='unrelated', name='other', systemKey='gb')], 'opaque+/= next')
            self.assertEqual(['opaque+/= next'], query['cursor'])
            return page([dict(id='installed', name='frozen', systemKey='gb')])
        seeder.api.call.side_effect = request
        self.assertEqual('installed', seeder.ensure_dat('gb', 'gb:dat', 'frozen', data))
        seeder.upload.assert_not_called()
        self.assertEqual(3, seeder.api.call.call_count)

    def test_dat_duplicate_on_a_later_page_is_refused_before_download_or_intake(self):
        seeder = self.seeder()
        target = dict(id='first', name='frozen', systemKey='gb')
        seeder.api.call.side_effect = [page([target], 'next'), page([target | dict(id='second')])]
        with self.assertRaisesRegex(demo.manage.SetupError, 'Duplicate DAT'):
            seeder.ensure_dat('gb', 'gb:dat', 'frozen', b'dat')
        seeder.upload.assert_not_called()
        self.assertEqual(2, seeder.api.call.call_count)

    def test_dat_intake_discovers_published_identity_on_a_later_page(self):
        seeder = self.seeder()
        seeder.api.call.side_effect = [page([]), page([dict(id='other', name='other', systemKey='gb')], 'next'),
                                       page([dict(id='published', name='frozen', systemKey='gb')])]
        self.assertEqual('published', seeder.ensure_dat('gb', 'gb:dat', 'frozen', b'frozen'))
        seeder.upload.assert_called_once_with('gb:dat', 'gb', 'fixture.dat', b'frozen')
        self.assertEqual(3, seeder.api.call.call_count)

    def test_incoherent_or_repeated_dat_pages_never_reuse_or_create_from_partial_results(self):
        target = dict(id='installed', name='frozen', systemKey='gb')
        sequences = (
            [dict(items=[target], hasNextPage=True, nextCursor=None)],
            [dict(items=[target], hasNextPage=False, nextCursor='unexpected')],
            [dict(items=[target], hasNextPage='false', nextCursor=None)],
            [dict(items=None, hasNextPage=False, nextCursor=None)],
            [page([target], 'repeat'), page([], 'repeat')],
        )
        for pages in sequences:
            with self.subTest(pages=pages):
                seeder = self.seeder()
                seeder.api.call.side_effect = pages
                with self.assertRaises(demo.manage.SetupError):
                    seeder.ensure_dat('gb', 'gb:dat', 'frozen', b'dat')
                seeder.upload.assert_not_called()
                self.assertEqual(len(pages), seeder.api.call.call_count)

    def test_late_dat_page_error_propagates_without_using_earlier_match(self):
        seeder = self.seeder()
        seeder.api.call.side_effect = [page([dict(id='installed', name='frozen', systemKey='gb')], 'next'),
                                       demo.ApiError('GET', '/api/dats', 503)]
        with self.assertRaises(demo.ApiError):
            seeder.ensure_dat('gb', 'gb:dat', 'frozen', b'dat')
        seeder.upload.assert_not_called()
        self.assertEqual(2, seeder.api.call.call_count)

    def test_dat_reuse_still_refuses_changed_frozen_bytes(self):
        seeder = self.seeder()
        seeder.api.call.side_effect = [page([dict(id='installed', name='frozen', systemKey='gb')]), b'changed']
        with self.assertRaisesRegex(demo.manage.SetupError, 'differs from the frozen'):
            seeder.ensure_dat('gb', 'gb:dat', 'frozen', b'frozen')
        seeder.upload.assert_not_called()

    def test_source_lookup_traverses_every_page_with_canonical_query(self):
        seeder = self.seeder()
        entry = demo.Entry('gb', 'exact selected source', 'game.gb', b'rom', 'prepared')
        target = dict(name=entry.name, titleId='title', titleName='Selected', hasLocalPayload=True)
        def request(path):
            resource = urlsplit(path)
            self.assertEqual('/api/dats/dat/source-entries', resource.path)
            query = parse_qs(resource.query)
            self.assertEqual([entry.name], query.get('query'))
            self.assertNotIn('search', query)
            self.assertEqual(['100'], query.get('limit'))
            return page([dict(name='prefix match')], 'next') if 'cursor' not in query else page([target])
        seeder.api.call.side_effect = request
        self.assertEqual([target], seeder.source_rows('dat', [entry]))
        self.assertEqual(2, seeder.api.call.call_count)

    def seeded_fixture(self, library_changes=None, user_changes=None, generated=False):
        seeder = self.seeder()
        seeder.root, seeder.directory, seeder.path = Path('/checkout'), Path('/private-evidence'), Path('/private-evidence/state.json')
        seeder.key, seeder.catalogs = 'a' * 64, {}
        seeder.entries = [demo.Entry('gb', 'Selected', 'game.gb', b'rom', 'generated' if generated else 'prepared')]
        seeder.urls = dict(consumer_api='http://localhost:5002', consumer_web='http://localhost:5174')
        seeder.ensure_dat = Mock(return_value='dat')
        seeder.source_rows = Mock(return_value=[dict(titleId='title', titleName='Selected', hasLocalPayload=True)])
        library = dict(id='persisted-library', name='ROMD Demo ' + seeder.key[:16],
                       configuration=demo.library_configuration(['title']), isDefault=False,
                       needsMaterialization=False, lastMaterializedAt='2026-01-01T00:00:00Z') | (library_changes or {})
        user = dict(id='persisted-user', email=f'demo-{seeder.key[:12]}@localhost', roles=['User'],
                    libraryId=library['id'], isSuspended=False, requiresActivation=False) | (user_changes or {})
        consumer = Mock()
        def consumer_request(path, *args, **options):
            resource = urlsplit(path)
            if resource.path == '/api/me/library':
                return dict(name=library['name'], counts=dict(ownedTitleCount=1))
            self.assertEqual('/api/catalog', resource.path)
            return page([], 'owned') if 'cursor' not in parse_qs(resource.query) else page([dict(id='title')])
        consumer.call.side_effect = consumer_request
        def request(path, method='GET', *args, **options):
            resource = urlsplit(path)
            if resource.path == '/api/system-setup/gb/enabled':
                return None
            if resource.path == '/api/titles/title':
                self.assertEqual(dict(with_etag=True), options)
                return dict(id='title', revision='rev-1', description='Already curated', media=page([])), '"rev-1"'
            if resource.path == '/api/titles/title/media' and method == 'POST':
                return None
            if resource.path in ('/api/libraries', '/api/users'):
                self.assertEqual('GET', method, 'existing demo objects must never be recreated or reset')
                query = parse_qs(resource.query)
                self.assertEqual(['100'], query.get('limit'))
                row = library if resource.path == '/api/libraries' else user
                summary = {key: row[key] for key in ('id', 'name' if resource.path == '/api/libraries' else 'email')}
                return page([], 'selected') if 'cursor' not in query else page([summary])
            if resource.path == '/api/libraries/persisted-library':
                return library
            if resource.path == '/api/users/persisted-user':
                return user
            self.fail('Unexpected demo API mutation/read: ' + path + ' ' + method)
        seeder.api.call.side_effect = request
        return seeder, consumer

    def run_seed(self, seeder, consumer):
        with patch.object(demo.demo_workspace, 'retain'), patch.object(demo, 'save') as save, \
                patch.object(demo, 'authorize', return_value='private-token') as authorize, \
                patch.object(demo, 'Api', return_value=consumer), patch('builtins.print'):
            result = seeder.seed()
        return result, save, authorize

    def test_seed_reuses_later_page_objects_from_canonical_details_and_persists_exact_identity(self):
        seeder, consumer = self.seeded_fixture()
        result, save, authorize = self.run_seed(seeder, consumer)
        self.assertEqual('persisted-library', result['libraryId'])
        self.assertEqual(1, result['consumerTitles'])
        paths = [urlsplit(call.args[0]).path for call in seeder.api.call.call_args_list]
        self.assertIn('/api/libraries/persisted-library', paths)
        self.assertIn('/api/users/persisted-user', paths)
        authorize.assert_called_once()
        self.assertEqual((seeder.path, result), save.call_args.args)
        self.assertFalse(any(call.args[1:] and call.args[1] == 'POST' for call in seeder.api.call.call_args_list))

    def test_seed_refuses_edited_existing_object_details_before_consumer_authorization(self):
        cases = ((dict(isDefault=True), None, 'library was edited'),
                 (dict(configuration=demo.library_configuration(['unrelated'])), None, 'library was edited'),
                 (None, dict(roles=['Admin']), 'account was edited'),
                 (None, dict(libraryId='different-library'), 'account was edited'),
                 (None, dict(isSuspended=True), 'account was edited'),
                 (None, dict(requiresActivation=True), 'account was edited'))
        for library, user, message in cases:
            with self.subTest(library=library, user=user):
                seeder, consumer = self.seeded_fixture(library, user)
                with patch.object(demo.demo_workspace, 'retain'), patch.object(demo, 'save'), \
                        patch.object(demo, 'authorize') as authorize, patch('builtins.print'), \
                        self.assertRaisesRegex(demo.manage.SetupError, message):
                    seeder.seed()
                authorize.assert_not_called()
                self.assertFalse(any(call.args[1:] and call.args[1] == 'POST' for call in seeder.api.call.call_args_list))

    def test_generated_fixture_uploads_cover_when_embedded_media_page_is_empty(self):
        seeder, consumer = self.seeded_fixture(generated=True)
        self.run_seed(seeder, consumer)
        covers = [call for call in seeder.api.call.call_args_list
                  if urlsplit(call.args[0]).path == '/api/titles/title/media' and call.args[1:] == ('POST',)]
        self.assertEqual(1, len(covers))
        self.assertEqual('cover.png', covers[0].kwargs['file'][0])

    def test_missing_library_materialization_detail_never_defaults_to_a_new_mutation(self):
        for missing in ('needsMaterialization', 'lastMaterializedAt'):
            with self.subTest(missing=missing):
                seeder, consumer = self.seeded_fixture()
                original = seeder.api.call.side_effect
                def incomplete(path, *args, **options):
                    result = original(path, *args, **options)
                    if path == '/api/libraries/persisted-library':
                        del result[missing]
                    return result
                seeder.api.call.side_effect = incomplete
                with patch.object(demo.demo_workspace, 'retain'), patch.object(demo, 'save'), patch('builtins.print'), \
                        self.assertRaisesRegex(demo.manage.SetupError, 'materialization state is incomplete'):
                    seeder.seed()
                self.assertFalse(any(call.args[1:] and call.args[1] == 'POST' for call in seeder.api.call.call_args_list))

    def test_cover_reuse_traverses_embedded_media_continuation_before_uploading(self):
        seeder, consumer = self.seeded_fixture(generated=True)
        original = seeder.api.call.side_effect
        def continuation(path, method='GET', *args, **options):
            if path == '/api/titles/title':
                return dict(id='title', revision='rev-1', description='Already curated',
                            media=page([], 'existing-cover')), '"rev-1"'
            if urlsplit(path).path == '/api/titles/title/media':
                self.assertEqual('GET', method)
                self.assertEqual(['existing-cover'], parse_qs(urlsplit(path).query)['cursor'])
                return page([dict(id='existing-media')])
            return original(path, method, *args, **options)
        seeder.api.call.side_effect = continuation
        self.run_seed(seeder, consumer)
        self.assertEqual(1, len([call for call in seeder.api.call.call_args_list
                                if urlsplit(call.args[0]).path == '/api/titles/title/media']))

    def title_http_fixture(self, *, description=None, media=None, continuation=None,
                           response_etag='observed', response_identity='title', fail_write=None):
        """Drive the real urllib seam, with routes/conditional headers checked against OpenAPI."""
        seeder, consumer = self.seeded_fixture(generated=True)
        other_request = seeder.api.call.side_effect
        schema = json.loads((demo.manage.ROOT / 'web/schemas/admin-v1.json').read_text())
        state = dict(id=response_identity, revision='rev-1', description=description,
                     media=media if media is not None else page([]))
        requests = []
        def response(value, etag=None):
            result = io.BytesIO(json.dumps(value).encode() if value is not None else b'')
            result.headers = {'ETag': etag} if etag is not None else {}
            return result
        def open_request(request, timeout):
            self.assertEqual(180, timeout)
            resource = urlsplit(request.full_url)
            path = resource.path + ('?' + resource.query if resource.query else '')
            method = request.get_method()
            if not resource.path.startswith('/api/titles/'):
                return response(other_request(path, method))
            requests.append(request)
            # Match the real published path templates rather than a fake retired-route alias.
            templates = [template for template in schema['paths'] if demo.re.fullmatch(
                demo.re.sub(r'\\\{[^}]+\\\}', '[^/]+', demo.re.escape(template)), resource.path)]
            self.assertEqual(1, len(templates), 'Request must match a published title resource.')
            operation = schema['paths'][templates[0]][method.lower()]
            self.assertEqual('Bearer private-admin-token', request.get_header('Authorization'))
            if method != 'GET':
                self.assertTrue(any(parameter['name'] == 'If-Match' and parameter['required']
                                    for parameter in operation['parameters']))
                self.assertEqual('"' + state['revision'] + '"', request.get_header('If-match'))
                if fail_write is not None:
                    raise demo.HTTPError(request.full_url, fail_write, 'private-server-detail', {}, io.BytesIO())
            operation_id = operation['operationId']
            if operation_id == 'UpdateTitle':
                payload = json.loads(request.data)
                self.assertNotIn('name', payload, 'Curation must preserve the generated title identity/name.')
                state.update(payload, revision='rev-2')
            elif operation_id == 'UploadTitleMedia':
                self.assertEqual(['Cover'], parse_qs(resource.query)['type'])
                self.assertIn('multipart/form-data;', request.get_header('Content-type'))
                self.assertIn(demo.cover(0), request.data)
                state.update(media=page([dict(id='cover')]), revision='rev-3')
                return response(dict(id='cover'), '"rev-3"')
            elif operation_id == 'ListTitleMedia':
                self.assertEqual(['100'], parse_qs(resource.query)['limit'])
                if isinstance(continuation, Exception):
                    raise continuation
                self.assertEqual(['existing+/= cover'], parse_qs(resource.query)['cursor'])
                return response(continuation)
            else:
                self.assertEqual('GetTitle', operation_id)
            etag = '"' + state['revision'] + '"' if response_etag == 'observed' else response_etag
            return response(dict(state), etag)
        seeder.api = demo.Api('http://localhost:5000', 'private-admin-token')
        seeder.api.opener = Mock()
        seeder.api.opener.open.side_effect = open_request
        return seeder, consumer, requests, state

    def test_generated_seed_uses_published_http_routes_chains_etags_and_second_seed_makes_no_title_writes(self):
        seeder, consumer, requests, state = self.title_http_fixture()
        first, _, _ = self.run_seed(seeder, consumer)
        second, _, _ = self.run_seed(seeder, consumer)
        self.assertEqual(first['libraryId'], second['libraryId'])
        self.assertEqual(['GET', 'PATCH', 'POST', 'GET'], [request.get_method() for request in requests])
        self.assertEqual([None, '"rev-1"', '"rev-2"', None],
                         [request.get_header('If-match') for request in requests])
        self.assertEqual('rev-3', state['revision'])

    def test_generated_title_reads_later_media_page_before_any_write_and_keeps_existing_cover(self):
        seeder, consumer, requests, _ = self.title_http_fixture(
            media=page([], 'existing+/= cover'), continuation=page([dict(id='existing-cover')]))
        self.run_seed(seeder, consumer)
        self.assertEqual(['GET', 'GET', 'PATCH'], [request.get_method() for request in requests])

    def test_generated_title_late_media_failure_prevents_metadata_and_cover_writes(self):
        for continuation in (dict(items=[], hasNextPage=False),
                             demo.HTTPError('http://localhost:5000/api/titles/title/media', 503,
                                            'private-failure', {}, io.BytesIO())):
            with self.subTest(continuation=type(continuation).__name__):
                seeder, consumer, requests, _ = self.title_http_fixture(
                    media=page([], 'existing+/= cover'), continuation=continuation)
                with self.assertRaises(demo.manage.SetupError):
                    self.run_seed(seeder, consumer)
                self.assertEqual(['GET', 'GET'], [request.get_method() for request in requests])

    def test_generated_title_missing_weak_wildcard_or_mismatched_validator_prevents_every_write(self):
        for etag in (None, '*', 'W/"rev-1"', '"different-revision"'):
            with self.subTest(etag=etag):
                seeder, consumer, requests, _ = self.title_http_fixture(response_etag=etag)
                with self.assertRaises(demo.manage.SetupError):
                    self.run_seed(seeder, consumer)
                self.assertEqual(['GET'], [request.get_method() for request in requests])

    def test_generated_title_foreign_identity_prevents_every_write(self):
        seeder, consumer, requests, _ = self.title_http_fixture(response_identity='foreign-title')
        with self.assertRaises(demo.manage.SetupError):
            self.run_seed(seeder, consumer)
        self.assertEqual(['GET'], [request.get_method() for request in requests])

    def test_generated_title_stale_write_fails_without_retry_or_following_cover_mutation(self):
        seeder, consumer, requests, _ = self.title_http_fixture(fail_write=412)
        with self.assertRaisesRegex(demo.ApiError, 'HTTP 412') as result:
            self.run_seed(seeder, consumer)
        self.assertNotIn('private', str(result.exception))
        self.assertEqual(['GET', 'PATCH'], [request.get_method() for request in requests])


class WaitTests(unittest.TestCase):
    """--wait polls this checkout's session record with a fake clock and fails fast on terminal states."""

    def run_wait(self, states, timeout=60, grace=10):
        now, lines, sequence = [0.0], [], iter(states)
        def status(_root):
            state, active = next(sequence)
            return dict(state=state, active=active)
        def sleep(seconds):
            now[0] += seconds
        with patch.object(demo.manage, 'session_status', side_effect=status) as reads, \
                patch.object(demo, 'managed_session', return_value='validated') as validate:
            result = demo.wait_for_session(Path('/checkout'), timeout, grace=grace, interval=2,
                                           clock=lambda: now[0], sleep=sleep,
                                           output=lambda line, flush: lines.append(line))
        validate.assert_called_once_with(Path('/checkout'))
        return result, lines, reads.call_count

    def test_waits_through_pre_start_starting_and_reloading_until_ready(self):
        states = [('stopped', False), ('starting', True), ('starting', True), ('reloading', True), ('ready', True)]
        result, lines, reads = self.run_wait(states)
        self.assertEqual((result, reads), ('validated', 5))
        self.assertIn('not active yet', lines[0])
        self.assertIn('starting', lines[1])
        self.assertIn('ready after 8s', lines[-1])

    def test_terminal_states_and_timeouts_fail_with_actionable_messages(self):
        cases = [
            ([('not-started', False)] * 10, 'No managed session became active.*dev:demo:serve'),
            ([('starting', True), ('failed', False)], 'ended while waiting.*failed'),
            ([('starting', True), ('interrupted', False)], 'ended while waiting'),
            ([('starting', True), ('reload-failed', True)], 'reload-failed.*dev:status'),
            ([('stopping', True)], 'stopping'),
            ([('starting', True)] * 40, 'still starting after 60s.*--wait SECONDS'),
        ]
        for states, message in cases:
            with self.subTest(message=message), self.assertRaisesRegex(demo.manage.SetupError, message):
                self.run_wait(states)

    def test_wait_is_opt_in_bounded_and_rejected_for_plan_and_serve(self):
        for arguments, expected in ((['--synthetic', '--wait'], demo.WAIT_DEFAULT), (['--synthetic', '--wait', '45'], 45)):
            with self.subTest(arguments=arguments), \
                    patch.object(demo, 'wait_for_session', side_effect=demo.manage.SetupError('stop')) as wait, \
                    self.assertRaisesRegex(demo.manage.SetupError, 'stop'):
                demo.main(arguments)
            wait.assert_called_once_with(demo.manage.ROOT, expected)
        for arguments in (['--wait', '--plan'], ['--wait', '5', '--serve'], ['--synthetic', '--wait', '0']):
            with self.subTest(arguments=arguments), patch.object(demo, 'wait_for_session') as wait, \
                    self.assertRaises(demo.manage.SetupError):
                demo.main(arguments)
            wait.assert_not_called()
        with patch.object(demo, 'wait_for_session') as wait, \
                patch.object(demo, 'Seeder', side_effect=demo.manage.SetupError('not ready')), \
                patch.object(demo, 'seed_lock', return_value=io.StringIO()), \
                self.assertRaisesRegex(demo.manage.SetupError, 'not ready'):
            demo.main(['--synthetic'])
        wait.assert_not_called()
