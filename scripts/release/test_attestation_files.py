import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from attestation_files import ROLES, append_bundle, merge_bundles, select_subject


class AttestationTransportTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)

    def parts(self):
        parts = self.work / 'parts'
        parts.mkdir()
        for role in ROLES:
            records = [{'mediaType': 'controlled-provenance'}]
            if role != 'index':
                records.append({'mediaType': 'controlled-sbom'})
            (parts / (role + '.jsonl')).write_text(''.join(json.dumps(item) + '\n' for item in records))
        return parts

    def test_action_bundles_without_newlines_are_normalized_and_all_thirteen_records_preserved(self):
        bundle, output = self.work / 'action.json', self.work / 'part.jsonl'
        bundle.write_text('{"mediaType":"controlled"}')
        append_bundle(bundle, output)
        append_bundle(bundle, output)
        self.assertEqual([{'mediaType': 'controlled'}] * 2, [json.loads(line) for line in output.read_text().splitlines()])
        merged = self.work / 'complete.jsonl'
        merge_bundles(self.parts(), merged)
        self.assertEqual(13, len(merged.read_text().splitlines()))
        with self.assertRaises(FileExistsError):
            merge_bundles(self.work / 'parts', merged)

    def test_missing_role_extra_role_missing_sbom_and_invalid_json_fail_closed(self):
        parts = self.parts()
        for change in ('missing-role', 'extra-role', 'missing-sbom', 'malformed', 'non-object'):
            path = parts / ('extra.jsonl' if change == 'extra-role' else 'worker.jsonl')
            original = path.read_bytes() if path.exists() else None
            if change == 'missing-role':
                path.unlink()
            else:
                path.write_text({'extra-role': '{}\n', 'missing-sbom': '{}\n', 'malformed': 'bad\n',
                                 'non-object': '[]\n[]\n'}[change])
            with self.subTest(change=change), self.assertRaises((ValueError, json.JSONDecodeError)):
                merge_bundles(parts, self.work / 'rejected.jsonl')
            if original is None:
                path.unlink()
            else:
                path.write_bytes(original)
            self.assertFalse((self.work / 'rejected.jsonl').exists())

    def test_append_rejects_malformed_bundle_or_incomplete_existing_record(self):
        bundle, output = self.work / 'action.json', self.work / 'part.jsonl'
        bundle.write_text('[]')
        with self.assertRaises(ValueError):
            append_bundle(bundle, output)
        bundle.write_text('{"mediaType":"controlled"}')
        output.write_text('{}')
        with self.assertRaises(ValueError):
            append_bundle(bundle, output)

    def test_subject_selection_verifies_entire_subject_binding_before_producing_action_inputs(self):
        directory = self.work / 'security-metadata'
        directory.mkdir()
        index = {'subjects': [{'kind': 'bundle', 'name': 'release.tar.gz', 'sbom': 'bundle.cdx.json'},
            {'kind': 'image', 'role': 'postgres', 'name': 'docker.io/library/postgres:18@sha256:' + 'a' * 64,
             'sbom': 'postgres.cdx.json'}]}
        (directory / 'release-metadata.json').write_text(json.dumps(index))
        with patch('attestation_files.verify_files', return_value=index) as verify:
            subject = select_subject(self.work, 'postgres', 'b' * 40, 'v1.0.0-rc.1')
            self.assertEqual('docker.io/library/postgres', subject['name'])
            self.assertEqual('sha256:' + 'a' * 64, subject['digest'])
            verify.assert_called_once_with(directory, self.work / 'release-files/release.tar.gz', 'b' * 40, 'v1.0.0-rc.1')
        with patch('attestation_files.verify_files', side_effect=ValueError('tampered binding')):
            for role in ROLES:
                with self.subTest(role=role), self.assertRaisesRegex(ValueError, 'tampered binding'):
                    select_subject(self.work, role, 'b' * 40, 'v1.0.0-rc.1')
        index['subjects'][0]['name'] = '../escape.tar.gz'
        (directory / 'release-metadata.json').write_text(json.dumps(index))
        with self.assertRaisesRegex(ValueError, 'safe deployment'):
            select_subject(self.work, 'bundle', 'b' * 40, 'v1.0.0-rc.1')
