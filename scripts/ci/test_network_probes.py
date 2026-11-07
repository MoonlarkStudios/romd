import contextlib
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
import zipfile

import network_probes as probes


class NetworkProbeTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)

    def archive(self, names=None):
        path = self.directory / 'fixture.zip'
        member = probes.RCLONE_ARCHIVE.removesuffix('.zip') + '/rclone'
        with zipfile.ZipFile(path, 'w') as archive:
            for name in names or [member]:
                archive.writestr(name, b'fixture executable')
        return path, hashlib.sha256(path.read_bytes()).hexdigest()

    def test_authenticated_executable_only_is_copied_with_execute_permission(self):
        member = probes.RCLONE_ARCHIVE.removesuffix('.zip') + '/rclone'
        archive, digest = self.archive([member, '../../outside'])
        with patch.object(probes, 'RCLONE_SHA256', digest):
            binary = probes.install_rclone(archive, self.directory)
        self.assertEqual(b'fixture executable', binary.read_bytes())
        self.assertTrue(binary.stat().st_mode & 0o111)
        self.assertEqual({'fixture.zip', 'rclone'}, {p.name for p in self.directory.iterdir()})

    def test_tampering_and_missing_executable_fail_before_installation(self):
        archive, digest = self.archive(['other/file'])
        with patch.object(probes, 'RCLONE_SHA256', digest):
            with self.assertRaisesRegex(ValueError, 'inventory'):
                probes.install_rclone(archive, self.directory)
            archive.write_bytes(archive.read_bytes() + b'tampered')
            with self.assertRaisesRegex(ValueError, 'checksum'):
                probes.install_rclone(archive, self.directory)
        self.assertFalse((self.directory / 'rclone').exists())

    def test_duplicate_linked_or_oversized_executables_are_rejected(self):
        path = self.directory / 'fixture.zip'
        member = probes.RCLONE_ARCHIVE.removesuffix('.zip') + '/rclone'
        for linked in (False, True):
            with zipfile.ZipFile(path, 'w') as archive:
                info = zipfile.ZipInfo(member)
                info.external_attr = (0o120777 if linked else 0o100755) << 16
                archive.writestr(info, b'fixture')
                if not linked:
                    # Duplicate entries are intentional hostile archive input.
                    with self.assertWarns(UserWarning):
                        archive.writestr(member, b'duplicate')
            with patch.object(probes, 'RCLONE_SHA256', hashlib.sha256(path.read_bytes()).hexdigest()):
                with self.assertRaisesRegex(ValueError, 'inventory'):
                    probes.install_rclone(path, self.directory)
            self.assertFalse((self.directory / 'rclone').exists())
        with patch.object(probes, 'MAX_BYTES', 1):
            with self.assertRaisesRegex(ValueError, 'size bound'):
                probes.install_rclone(path, self.directory)

    def report(self, part, omitted=None, outcome='Passed', duplicate=False):
        root = ET.Element(probes.TRX + 'TestRun')
        results = ET.SubElement(root, probes.TRX + 'Results')
        for name in probes.PROBES[part]:
            if name != omitted:
                ET.SubElement(results, probes.TRX + 'UnitTestResult', testName=name, outcome=outcome)
        if duplicate:
            ET.SubElement(results, probes.TRX + 'UnitTestResult', testName=probes.PROBES[part][0], outcome='Passed')
        ET.SubElement(root, probes.TRX + 'StdOut').text = 'private transcript must not be retained'
        ET.ElementTree(root).write(self.directory / 'integration.trx')

    def test_both_partitions_require_every_selected_probe_to_have_passed(self):
        for part in probes.PROBES:
            self.report(part)
            with contextlib.redirect_stdout(io.StringIO()):
                probes.verify(part, self.directory)
            receipt = self.directory / 'network-probes.json'
            self.assertEqual('passed', json.loads(receipt.read_text())['result'])
            self.assertNotIn('private transcript', receipt.read_text())

    def test_skipped_failed_missing_or_duplicate_probes_cannot_qualify(self):
        for part in probes.PROBES:
            for outcome in ('NotExecuted', 'Failed'):
                self.report(part, outcome=outcome)
                with self.assertRaisesRegex(ValueError, 'Required network probe'):
                    probes.verify(part, self.directory)
            self.report(part, omitted=probes.PROBES[part][0])
            with self.assertRaisesRegex(ValueError, 'Required network probe'):
                probes.verify(part, self.directory)
            self.report(part, duplicate=True)
            with self.assertRaisesRegex(ValueError, 'Required network probe'):
                probes.verify(part, self.directory)

    def test_missing_or_multiple_reports_cannot_qualify(self):
        with self.assertRaisesRegex(ValueError, 'exactly one'):
            probes.verify('hosting', self.directory)
        self.report('hosting')
        nested = self.directory / 'stale'
        nested.mkdir()
        (nested / 'integration.trx').write_bytes((self.directory / 'integration.trx').read_bytes())
        with self.assertRaisesRegex(ValueError, 'exactly one'):
            probes.verify('hosting', self.directory)


if __name__ == '__main__':
    unittest.main()
