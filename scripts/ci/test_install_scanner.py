"""Keep authenticated scanner installation independent of retired launch tooling."""
import hashlib
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import install_scanner
import secret_scan


class ScannerInstallationTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.binary = b'synthetic scanner executable\n'
        self.package = self.archive(('gitleaks', self.binary))
        self.pin = {'version': '8.30.0', 'archives': {
            'linux_x64': hashlib.sha256(self.package).hexdigest()}}
        self.addCleanup(os.umask, os.umask(0o077))

    def archive(self, *files):
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode='w:gz') as package:
            for name, data in files:
                member = tarfile.TarInfo(name)
                if data is None:
                    member.type = tarfile.SYMTYPE
                    member.linkname = 'elsewhere'
                else:
                    member.size = len(data)
                package.addfile(member, None if data is None else io.BytesIO(data))
        return stream.getvalue()

    def install(self, target):
        with patch.object(install_scanner, 'scanner_pin', return_value=self.pin), \
                patch.object(install_scanner.platform, 'system', return_value='Linux'), \
                patch.object(install_scanner.platform, 'machine', return_value='x86_64'), \
                patch.object(install_scanner.urllib.request, 'urlopen', return_value=io.BytesIO(self.package)):
            return install_scanner.install(target)

    def test_retains_authenticated_archive_binary_and_receipt_with_private_modes(self):
        target = self.root / 'installed'
        binary = self.install(target)
        self.assertEqual(self.binary, binary.read_bytes())
        self.assertEqual(self.package, (target / install_scanner.ARCHIVE_NAME).read_bytes())
        receipt = json.loads((target / 'receipt.json').read_text())
        self.assertEqual(self.pin['archives']['linux_x64'], receipt['archive_sha256'])
        self.assertEqual(hashlib.sha256(self.binary).hexdigest(), receipt['binary_sha256'])
        self.assertEqual(install_scanner.ARCHIVE_NAME, receipt['archive_file'])
        self.assertEqual(self.pin['version'], receipt['version'])
        for path, mode in ((target, 0o700), (binary, 0o700),
                           (target / install_scanner.ARCHIVE_NAME, 0o600),
                           (target / 'receipt.json', 0o600)):
            self.assertEqual(mode, path.stat().st_mode & 0o777)

    def test_wrong_checksum_is_rejected_before_retaining_any_bytes(self):
        self.pin['archives']['linux_x64'] = '0' * 64
        target = self.root / 'rejected'
        with self.assertRaisesRegex(ValueError, 'checksum differs'):
            self.install(target)
        self.assertEqual([], list(target.iterdir()))

    def test_existing_installation_is_not_overwritten(self):
        binary = self.install(self.root / 'installed')
        with self.assertRaises(FileExistsError):
            self.install(binary.parent)
        self.assertEqual(self.binary, binary.read_bytes())

    def test_scanner_requires_one_regular_archive_member(self):
        for files in ((), (('gitleaks', None),),
                      (('gitleaks', self.binary), ('gitleaks', self.binary))):
            with self.subTest(files=files), self.assertRaises(ValueError):
                install_scanner.scanner_binary(self.archive(*files))

    def test_authentication_rejects_changed_archive_or_executable(self):
        binary = self.install(self.root / 'installed')
        with patch.object(secret_scan, 'scanner_pin', return_value=self.pin):
            secret_scan.authenticate(binary)
            binary.write_bytes(b'changed executable')
            with self.assertRaisesRegex(ValueError, 'differs from its authenticated archive'):
                secret_scan.authenticate(binary)
            binary.write_bytes(self.binary)
            binary.with_name(install_scanner.ARCHIVE_NAME).write_bytes(b'changed archive')
            with self.assertRaisesRegex(ValueError, 'does not match the reviewed pin'):
                secret_scan.authenticate(binary)


if __name__ == '__main__':
    unittest.main()
