#!/usr/bin/env python3
"""Report destinations must never overwrite operator-owned fixtures or prior evidence."""
import hashlib
import json
import platform
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zlib


class InspectionReportTests(unittest.TestCase):
    def test_cd_report_preserves_inputs_and_existing_report_aliases(self):
        native = self.cd_native_directory()
        for source_name in ('config', 'chd'):
            for kind in ('direct', 'symlink', 'hardlink'):
                with self.subTest(source=source_name, kind=kind):
                    with tempfile.TemporaryDirectory(prefix='romd-cd-report-') as directory:
                        root = Path(directory).resolve()
                        chd = root / 'operator.chd'
                        chd.write_bytes(b'MComprHD' + b'operator-owned artifact' * 10)
                        config = root / 'configuration.json'
                        config.write_text(json.dumps(dict(NativeDirectory=str(native), DataDirectory=str(root / 'fresh-data'),
                            SourceDescription='Report preservation control', Inputs=[dict(ChdPath=str(chd), ExpectedTracks=None)])))
                        source = dict(config=config, chd=chd)[source_name]
                        originals = {p: p.read_bytes() for p in (config, chd)}
                        report = source
                        if kind != 'direct':
                            report = root / f'report.{kind}'
                            if kind == 'symlink':
                                report.symlink_to(source)
                            else:
                                report.hardlink_to(source)
                        result = subprocess.run(['dotnet', PROBE, '--chd-cd-inspection-probe', str(config), str(report)],
                            capture_output=True, timeout=30)
                        self.assertNotEqual(0, result.returncode)
                        for path, expected in originals.items():
                            self.assertEqual(expected, path.read_bytes(), str(path))

    def test_cd_wrong_track_oracle_cannot_report_success(self):
        native = self.cd_native_directory()
        repo = next(p for p in Path(PROBE).parents if (p / 'Romd.sln').exists())
        fixtures = repo / 'artifacts/fixtures/libchdr-final'
        source = (fixtures / 'cd-source.bin').read_bytes()
        expected = []
        for number, track in enumerate((source[:16 * 2352], source[16 * 2352:]), 1):
            expected.append(dict(Number=number, BinBytes=len(track), Sha1=hashlib.sha1(track).hexdigest(),
                Md5=hashlib.md5(track).hexdigest(), Crc32=f'{zlib.crc32(track):08x}'))
        expected[0]['Sha1'] = '1' * 40
        with tempfile.TemporaryDirectory(prefix='romd-cd-oracle-') as directory:
            root = Path(directory).resolve()
            config = root / 'configuration.json'
            config.write_text(json.dumps(dict(NativeDirectory=str(native), DataDirectory=str(root / 'fresh-data'),
                SourceDescription='Deliberately incorrect independent track hash', Inputs=[dict(
                    ChdPath=str(fixtures / 'cd-cdlz.chd'), ExpectedTracks=expected)])))
            report = root / 'report.json'
            result = subprocess.run(['dotnet', PROBE, '--chd-cd-inspection-probe', str(config), str(report)],
                capture_output=True, timeout=30)
            self.assertEqual(1, result.returncode, result.stderr.decode(errors='replace'))
            self.assertEqual('failed', json.loads(report.read_text())['Status'])
            self.assertIn(b'track size and three digests', result.stderr)

    @staticmethod
    def cd_native_directory():
        repo = next(p for p in Path(PROBE).parents if (p / 'Romd.sln').exists())
        arch = {'arm64': 'arm64', 'aarch64': 'arm64', 'x86_64': 'x64', 'amd64': 'x64'}[platform.machine().lower()]
        os_name = {'darwin': 'osx', 'linux': 'linux', 'win32': 'win'}[sys.platform]
        directory = repo / 'artifacts/native/libchdr' / f'{os_name}-{arch}'
        if not (directory / 'build-manifest.json').exists():
            raise AssertionError('Pinned native input is missing; run mise run native:test-inputs')
        return directory

    def test_encoding_failure_exits_normally_with_nonzero_status(self):
        with tempfile.TemporaryDirectory(prefix='romd-encoding-exit-') as directory:
            root = Path(directory)
            fixture = root / 'fixtures.json'
            fixture.write_text(json.dumps(dict(Fixtures=[], OracleExecutable=str(root / 'missing-oracle'))))
            result = subprocess.run(['dotnet', PROBE, '--encoding-probe', str(fixture), str(root / 'report.json')],
                capture_output=True, timeout=30)
            self.assertEqual(1, result.returncode, result.stderr.decode(errors='replace'))
            self.assertIn(b'FAILED: empty ordinary encoding runs cannot qualify', result.stderr)

    def test_existing_inputs_and_their_aliases_are_preserved(self):
        for source in ('fixture', 'iso', 'rvz', 'dat'):
            for kind in ('direct', 'symlink', 'hardlink'):
                with self.subTest(source=source, kind=kind):
                    self.check_case(source, kind)

    def test_supervisor_report_preserves_existing_inputs_and_aliases(self):
        for source in ('fixture', 'iso', 'rvz', 'dat'):
            for kind in ('direct', 'symlink', 'hardlink'):
                with self.subTest(source=source, kind=kind):
                    self.check_case(source, kind, '--supervisor-probe')

    def test_iso_inspection_report_preserves_existing_inputs_and_aliases(self):
        for source in ('fixture', 'iso', 'rvz', 'dat'):
            for kind in ('direct', 'symlink', 'hardlink'):
                with self.subTest(source=source, kind=kind):
                    self.check_case(source, kind, '--iso-inspection-probe')

    def test_encoding_report_preserves_existing_inputs_and_aliases(self):
        for source in ('fixture', 'iso', 'rvz', 'dat'):
            for kind in ('direct', 'symlink', 'hardlink'):
                with self.subTest(source=source, kind=kind):
                    self.check_case(source, kind, '--encoding-probe')

    def check_case(self, source_name, kind, mode='--inspection-probe'):
        with tempfile.TemporaryDirectory(prefix='romd-inspection-report-') as directory:
            root = Path(directory)
            iso = root / 'original.iso'
            rvz = root / 'upload.rvz'
            dat = root / 'oracle.dat'
            fixture = root / 'fixtures.json'
            iso.write_bytes(b'operator-owned ISO oracle')
            rvz.write_bytes(b'RVZ\x01operator-owned artifact')
            dat.write_bytes(b'<datafile><game id="1"><rom size="25" /></game></datafile>')
            fixture.write_text(json.dumps([dict(Name='report safety', UploadedRvz=str(rvz),
                NormalizedRvz=str(rvz), Iso=str(iso), Dat=str(dat), DatId='1')]))
            if mode == '--supervisor-probe':
                fixture.write_text(json.dumps(dict(ProductionWorker=str(rvz), FaultWorker=str(rvz),
                    InputsDirectory=str(root), OracleIso=str(iso))))
            elif mode == '--encoding-probe':
                fixture.write_text(json.dumps(dict(Fixtures=[dict(Name='report safety',
                    UploadedRvz=str(rvz), NormalizedRvz=str(rvz), Iso=str(iso), Dat=str(dat), DatId='1')],
                    OracleExecutable=str(rvz))))
            source = dict(fixture=fixture, iso=iso, rvz=rvz, dat=dat)[source_name]
            originals = {path: path.read_bytes() for path in (fixture, iso, rvz, dat)}
            report = source
            if kind != 'direct':
                report = root / f'{source.name}.{kind}'
                if kind == 'symlink':
                    report.symlink_to(source)
                else:
                    report.hardlink_to(source)
            result = subprocess.run(['dotnet', PROBE, mode, str(fixture), str(report)],
                capture_output=True, timeout=30)
            self.assertNotEqual(0, result.returncode)
            for path, expected in originals.items():
                self.assertEqual(hashlib.sha256(expected).digest(), hashlib.sha256(path.read_bytes()).digest(), str(path))
            self.assertIn(b'already exists', result.stderr.lower())


if __name__ == '__main__':
    if len(sys.argv) < 2:
        raise SystemExit('usage: test-inspection-reports.py <compiled probe.dll> [unittest options]')
    PROBE = str(Path(sys.argv.pop(1)).resolve())
    unittest.main()
