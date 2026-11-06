import unittest
import native_webdav_eof_matrix as matrix


class NativeWebDavEofMatrixTests(unittest.TestCase):
    def test_background_download_is_plain_or_open_ended_get_only(self):
        self.assertTrue(matrix.is_background_download('GET', None))
        self.assertTrue(matrix.is_background_download('GET', 'bytes=4096-'))
        self.assertFalse(matrix.is_background_download('GET', 'bytes=4096-8191'))
        self.assertFalse(matrix.is_background_download('HEAD', None))

    def test_describe_read_separates_exact_prefix_from_zero_padding(self):
        padded = matrix.describe_read(b'data\0\0\0', b'data', 0)
        self.assertFalse(padded['exact'])
        self.assertTrue(padded['prefix_exact'])
        self.assertEqual(3, padded['extra_bytes'])
        self.assertTrue(padded['extra_all_zero'])
        self.assertFalse(matrix.describe_read(b'data\0x', b'data', 0)['extra_all_zero'])
        self.assertFalse(matrix.describe_read(b'data', b'data', 0)['extra_all_zero'])

    def test_crosses_eof_only_for_closed_ranges_ending_at_or_past_length(self):
        self.assertTrue(matrix.crosses_eof('bytes=900-1999', 'bytes 900-999/1000'))
        self.assertTrue(matrix.crosses_eof('bytes=900-1000', 'bytes 900-999/1000'))
        self.assertFalse(matrix.crosses_eof('bytes=900-999', 'bytes 900-999/1000'))
        self.assertFalse(matrix.crosses_eof('bytes=900-', 'bytes 900-999/1000'))
        self.assertFalse(matrix.crosses_eof('bytes=900-1999', 'bytes 900-999/*'))
        self.assertFalse(matrix.crosses_eof(None, 'bytes 900-999/1000'))

    def test_read_is_exact_covers_ranges_sequential_reads_and_bookkeeping(self):
        self.assertTrue(matrix.read_is_exact(dict(call='open', seconds=0)))
        self.assertFalse(matrix.read_is_exact(dict(exact=False)))
        self.assertTrue(matrix.read_is_exact(dict(sha256_matches=True, bytes=5, expected_bytes=5)))
        self.assertFalse(matrix.read_is_exact(dict(sha256_matches=True, bytes=6, expected_bytes=5)))

    def test_fixture_bytes_are_deterministic_across_generation_chunks(self):
        case = dict(name='chunked', size=16 * matrix.MIB + 3)
        first = matrix.fixture_bytes(case)
        self.assertEqual(case['size'], len(first))
        self.assertEqual(first, matrix.fixture_bytes(case))


if __name__ == '__main__':
    unittest.main()
