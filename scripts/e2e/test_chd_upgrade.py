import unittest
from unittest.mock import Mock, call

import chd



class ConsumerMemberTests(unittest.TestCase):
    def test_retained_member_is_located_when_unsupported_formats_omit_hashes(self):
        run = object.__new__(chd.Qualification)
        run.title_id = 'title'
        run.subject = chd.synthetic_subject()
        run.subject.identity = {'sha256': 'disc-hash'}
        run.api = Mock()
        page = {'items': [{'id': 'release', 'isComplete': True}], 'hasNextPage': False, 'nextCursor': None}
        title = {'defaultReleaseId': 'release', 'releases': page}
        baseline = {'items': [{'relativePath': 'disc.iso', 'formats': [
            {'format': 'original', 'state': 'available', 'sha256': 'disc-hash'}]}]}
        run.api.json.side_effect = [title, page, baseline]
        self.assertEqual('release', run.consumer_formats()[0])
        unsupported = [{'format': kind, 'state': 'unsupported'} for kind in ('original', 'chd')]
        run.api.json.side_effect = [title, page, {'items': [
            {'relativePath': 'sibling.iso', 'formats': baseline['items'][0]['formats']},
            {'relativePath': 'disc.iso', 'formats': unsupported}]}]
        self.assertEqual(('release', {entry['format']: entry for entry in unsupported}), run.consumer_formats())
        run.api.json.side_effect = [title, page, {'items': [
            {'relativePath': 'sibling.iso', 'formats': baseline['items'][0]['formats']}]}]
        with self.assertRaisesRegex(chd.HarnessError, 'captured disc member'):
            run.consumer_formats()

    def test_consumer_release_pages_follow_cursors_on_consumer_host(self):
        run = object.__new__(chd.Qualification)
        run.title_id = 'title'
        run.subject = chd.synthetic_subject()
        run.subject.identity = {'sha256': 'disc-hash'}
        run.api = Mock()
        run.api.json.side_effect = [
            {'defaultReleaseId': 'release'},
            {'items': [{'id': 'incomplete', 'isComplete': False}], 'hasNextPage': True, 'nextCursor': 'next+page'},
            {'items': [{'id': 'release', 'isComplete': True}], 'hasNextPage': False, 'nextCursor': None},
            {'items': [{'relativePath': 'disc.iso', 'formats': [{'format': 'original', 'sha256': 'disc-hash'}]}]}]
        self.assertEqual('release', run.consumer_formats()[0])
        self.assertEqual([
            call(chd.CONSUMER_HOST, '/api/titles/title'),
            call(chd.CONSUMER_HOST, '/api/titles/title/releases?limit=100'),
            call(chd.CONSUMER_HOST, '/api/titles/title/releases?limit=100&cursor=next%2Bpage'),
            call(chd.CONSUMER_HOST, '/api/releases/release/manifest', method='POST')], run.api.json.call_args_list)

    def test_legacy_release_array_is_rejected_as_an_invalid_page(self):
        run = object.__new__(chd.Qualification)
        run.title_id = 'title'
        run.api = Mock()
        run.api.json.side_effect = [{'defaultReleaseId': 'release'}, [{'id': 'release', 'isComplete': True}]]
        with self.assertRaisesRegex(chd.HarnessError, 'invalid page'):
            run.consumer_formats()


if __name__ == '__main__':
    unittest.main()
