import hashlib
import io
import json
from pathlib import Path
import tarfile
import tempfile
from types import SimpleNamespace
import unittest

from evidence_archive import (MAX_FILE, archive_name, create_archive, read_archive,
                              unpack_archive)
from suite import REPORTS


def archive_fixture(root, *, revision='a' * 40, tag='v1.0.0-rc.1', bundle_sha=None):
    """Synthetic transport inputs; they do not model valid cryptographic attestations."""
    root.mkdir(parents=True, exist_ok=True)
    data = SimpleNamespace(root=root, revision=revision, tag=tag, metadata=root / 'metadata',
                           packaged=root / 'packaged', attestations=root / 'attestations.jsonl',
                           images=root / 'images.env', state=root / 'candidate.json',
                           archive=root / archive_name(tag))
    data.metadata.mkdir()
    data.packaged.mkdir()
    images = {'ROMD_' + role.upper() + '_IMAGE': 'ghcr.io/owner/romd-' + role + '@sha256:'
              + hashlib.sha256(role.encode()).hexdigest()
              for role in ('admin', 'consumer', 'worker', 'player', 'postgres')}
    bundle_sha = bundle_sha or hashlib.sha256(b'bundle').hexdigest()
    data.images.write_text(''.join(key + '=' + value + '\n' for key, value in images.items()))
    data.state.write_text(json.dumps(dict(sourceCommit=revision, releaseTag=tag, images=images,
                                          bundleSha256=bundle_sha, result='draft-unqualified')))
    data.attestations.write_text('{"synthetic":"transport-only"}\n')
    files = {}
    for name in REPORTS:
        path = data.packaged / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps({'syntheticReport': name}))
        files[name] = hashlib.sha256(path.read_bytes()).hexdigest()
    (data.packaged / 'suite.json').write_text(json.dumps(dict(schemaVersion=1, result='passed',
        evidenceType='disposable-release', sourceCommit=revision, releaseTag=tag, images=images,
        bundleSha256=bundle_sha, files=files)))
    sbom = data.metadata / 'admin.cdx.json'
    sbom.write_text('{"bomFormat":"CycloneDX","components":[]}\n')
    subjects = [dict(kind='image', role=role, name=images['ROMD_' + role.upper() + '_IMAGE'])
                for role in ('admin', 'consumer', 'worker', 'player', 'postgres')]
    subjects.append(dict(kind='bundle', name='deployment.tar.gz', sha256=bundle_sha))
    (data.metadata / 'release-metadata.json').write_text(json.dumps(dict(schemaVersion=1,
        result='passed', evidenceType='disposable-release', sourceCommit=revision, releaseTag=tag,
        subjects=subjects, files={sbom.name: hashlib.sha256(sbom.read_bytes()).hexdigest()})))
    return data


def create(data, output=None):
    return create_archive(data.metadata, data.attestations, data.images, data.state,
                          data.packaged, output or data.archive, data.revision, data.tag)


class EvidenceArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.data = archive_fixture(self.root / 'inputs')

    def rewrite_archive(self, transform):
        with tarfile.open(self.data.archive) as archive:
            entries = [(member, archive.extractfile(member).read()) for member in archive]
        entries = transform(entries)
        target = self.root / 'changed' / self.data.archive.name
        target.parent.mkdir(exist_ok=True)
        with tarfile.open(target, 'w:gz') as archive:
            for member, payload in entries:
                archive.addfile(member, io.BytesIO(payload))
        checksum = Path(str(target) + '.sha256')
        checksum.write_text(hashlib.sha256(target.read_bytes()).hexdigest() + '  ' + target.name + '\n')
        return target, checksum

    def test_complete_inventory_roundtrips_exact_bytes_and_archive_is_reproducible(self):
        manifest = create(self.data)
        output = self.root / 'unpacked'
        actual = unpack_archive(self.data.archive, Path(str(self.data.archive) + '.sha256'), output,
                                self.data.revision, self.data.tag)
        self.assertEqual(manifest, actual)
        for name, record in manifest['files'].items():
            payload = (output / name).read_bytes()
            self.assertEqual(record, {'size': len(payload), 'sha256': hashlib.sha256(payload).hexdigest()})
        self.assertEqual(self.data.attestations.read_bytes(), (output / 'attestations.jsonl').read_bytes())
        self.assertEqual(self.data.state.read_bytes(), (output / 'publisher/candidate.json').read_bytes())
        self.assertEqual(set(REPORTS) | {'suite.json'},
                         {str(path.relative_to(output / 'packaged')) for path in (output / 'packaged').rglob('*.json')})
        again = self.root / 'again' / self.data.archive.name
        again.parent.mkdir()
        create(self.data, again)
        self.assertEqual(self.data.archive.read_bytes(), again.read_bytes())

    def test_source_version_rehearsal_and_failed_runtime_evidence_cannot_be_laundered(self):
        for report_name, change in (
            ('metadata/release-metadata.json', {'evidenceType': 'disposable-rehearsal'}),
            ('metadata/release-metadata.json', {'sourceCommit': 'b' * 40}),
            ('packaged/suite.json', {'releaseTag': 'v9.9.9'}),
            ('packaged/suite.json', {'result': 'failed'}),
        ):
            with self.subTest(report=report_name, change=change):
                path = self.data.root / report_name
                original = path.read_text()
                report = json.loads(original)
                report.update(change)
                path.write_text(json.dumps(report))
                with self.assertRaises(ValueError):
                    create(self.data)
                self.assertFalse(self.data.archive.exists())
                path.write_text(original)

    def test_changed_metadata_runtime_or_original_publisher_state_blocks_packaging(self):
        for relative in ('metadata/admin.cdx.json', REPORTS[0], 'candidate.json'):
            path = (self.data.packaged / relative if relative in REPORTS else self.data.root / relative)
            original = path.read_bytes()
            path.write_bytes(b'{}')
            with self.subTest(relative=relative), self.assertRaises(ValueError):
                create(self.data)
            path.write_bytes(original)

    def test_traversal_absolute_alias_link_duplicate_missing_and_extra_members_reject(self):
        create(self.data)
        def altered(name=None, kind=None, duplicate=False, missing=False, extra=False):
            def change(entries):
                member, payload = entries[1]
                if missing:
                    return entries[:1] + entries[2:]
                if duplicate:
                    return entries + [(member, payload)]
                if extra:
                    member = tarfile.TarInfo('unexpected.json')
                    member.size = len(payload)
                    return entries + [(member, payload)]
                member.name = name or member.name
                if kind is not None:
                    member.type, member.linkname, member.size = kind, '../../outside', 0
                    payload = b''
                return entries[:1] + [(member, payload)] + entries[2:]
            return change
        controls = [altered(name=name) for name in ('../outside', '/outside', 'a//b', 'a/./b', 'a\\b')]
        controls += [altered(kind=kind) for kind in (tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.FIFOTYPE)]
        controls += [altered(duplicate=True), altered(missing=True), altered(extra=True)]
        for index, control in enumerate(controls):
            with self.subTest(control=index):
                archive, checksum = self.rewrite_archive(control)
                output = self.root / f'rejected-{index}'
                with self.assertRaises(ValueError):
                    unpack_archive(archive, checksum, output, self.data.revision, self.data.tag)
                self.assertFalse(output.exists())
        self.assertFalse((self.root / 'outside').exists())

    def test_outer_checksum_rewrite_cannot_hide_changed_indexed_metadata(self):
        create(self.data)
        def change(entries):
            manifest = json.loads(entries[0][1])
            for member, payload in entries:
                if member.name == 'metadata/admin.cdx.json':
                    replacement = b'{"changed":"SBOM"}'
                    member.size = len(replacement)
                    manifest['files'][member.name] = dict(size=len(replacement),
                                                          sha256=hashlib.sha256(replacement).hexdigest())
                    entries[entries.index((member, payload))] = member, replacement
                    break
            payload = json.dumps(manifest).encode()
            entries[0][0].size = len(payload)
            entries[0] = entries[0][0], payload
            return entries
        archive, checksum = self.rewrite_archive(change)
        with self.assertRaisesRegex(ValueError, 'metadata file checksum'):
            read_archive(archive, checksum, self.data.revision, self.data.tag)

    def test_oversized_and_case_alias_inventories_reject_without_extracting(self):
        create(self.data)
        for mode in ('oversized', 'case-alias'):
            def change(entries):
                manifest = json.loads(entries[0][1])
                name = 'metadata/admin.cdx.json'
                if mode == 'oversized':
                    manifest['files'][name]['size'] = MAX_FILE + 1
                else:
                    manifest['files']['METADATA/admin.cdx.json'] = manifest['files'][name]
                payload = json.dumps(manifest).encode()
                entries[0][0].size = len(payload)
                return [(entries[0][0], payload), *entries[1:]]
            with self.subTest(mode=mode):
                archive, checksum = self.rewrite_archive(change)
                with self.assertRaises(ValueError):
                    read_archive(archive, checksum, self.data.revision, self.data.tag)

    def test_existing_outputs_and_symlink_inputs_are_preserved(self):
        source = self.data.metadata / 'admin.cdx.json'
        source.unlink()
        source.symlink_to(self.data.attestations)
        with self.assertRaises(ValueError):
            create(self.data)
        source.unlink()
        source.write_text('{"bomFormat":"CycloneDX","components":[]}\n')
        create(self.data)
        before = self.data.archive.read_bytes()
        with self.assertRaises(ValueError):
            create(self.data)
        self.assertEqual(before, self.data.archive.read_bytes())
        output = self.root / 'existing'
        output.mkdir()
        (output / 'keep').write_text('existing evidence')
        with self.assertRaises(FileExistsError):
            unpack_archive(self.data.archive, Path(str(self.data.archive) + '.sha256'), output,
                           self.data.revision, self.data.tag)
        self.assertEqual(['keep'], [path.name for path in output.iterdir()])

    def test_unsafe_or_reserved_indexed_paths_cannot_enter_the_archive(self):
        path = self.data.metadata / 'release-metadata.json'
        original = json.loads(path.read_text())
        for name in ('../outside.json', '/absolute.json', 'back\\slash.json', '.env',
                     'release-metadata.json', 'RELEASE-METADATA.JSON'):
            index = dict(original, files={name: 'a' * 64})
            path.write_text(json.dumps(index))
            with self.subTest(name=name), self.assertRaises(ValueError):
                create(self.data)
            self.assertFalse(self.data.archive.exists())


if __name__ == '__main__':
    unittest.main()
