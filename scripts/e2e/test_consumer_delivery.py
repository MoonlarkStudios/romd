"""Consumer release discovery uses canonical pages and fresh member manifests."""
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, call

import chd


def page(items, cursor=None):
    return {'items': items, 'nextCursor': cursor, 'hasNextPage': cursor is not None}


class ConsumerReleaseDiscoveryTests(unittest.TestCase):
    def setUp(self):
        self.qualification = object.__new__(chd.Qualification)
        self.qualification.title_id = 'title'
        self.qualification.subject = SimpleNamespace(identity={'sha256': 'a' * 64})
        self.qualification.api = Mock()
        self.original = {'format': 'original', 'sha256': 'a' * 64, 'state': 'available'}
        self.manifest = {'items': [{'relativePath': 'Disc.iso', 'formats': [self.original]}]}

    def test_disc_on_later_release_page_uses_consumer_host_and_encoded_cursor(self):
        self.qualification.api.json.side_effect = [
            {'defaultReleaseId': 'release', 'releases': page([])},
            page([{'id': 'incomplete', 'isComplete': False}], 'next+page'),
            page([{'id': 'release', 'isComplete': True}]), self.manifest,
        ]
        self.assertEqual(('release', {'original': self.original}), self.qualification.consumer_formats())
        self.assertEqual([
            call(chd.CONSUMER_HOST, '/api/titles/title'),
            call(chd.CONSUMER_HOST, '/api/titles/title/releases?limit=100'),
            call(chd.CONSUMER_HOST, '/api/titles/title/releases?limit=100&cursor=next%2Bpage'),
            call(chd.CONSUMER_HOST, '/api/releases/release/manifest', method='POST'),
        ], self.qualification.api.json.call_args_list)

    def test_default_complete_release_is_preferred_to_other_matching_releases(self):
        self.qualification.api.json.side_effect = [
            {'defaultReleaseId': 'default', 'releases': page([])},
            page([{'id': 'other', 'isComplete': True}, {'id': 'default', 'isComplete': True}]),
            self.manifest,
        ]
        self.assertEqual('default', self.qualification.consumer_formats()[0])
        self.assertEqual(call(chd.CONSUMER_HOST, '/api/releases/default/manifest', method='POST'),
                         self.qualification.api.json.call_args)

    def test_invalid_release_page_fails_closed_before_requesting_a_manifest(self):
        self.qualification.api.json.side_effect = [{'releases': page([])}, ['not-a-page']]
        with self.assertRaisesRegex(chd.HarnessError, 'invalid page'):
            self.qualification.consumer_formats()
        self.assertEqual(2, self.qualification.api.json.call_count)
