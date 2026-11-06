"""Execute the real final-publication shell steps with disposable command doubles."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]


def completion_steps(workflow=None):
    if workflow is None:
        workflow = (ROOT / '.github/workflows/release.yml').read_text()
    block = workflow.split('\n  publish-complete:\n', 1)[1]
    steps, command, controls = [], None, []
    def finish():
        if command is not None:
            # The shell driver models required default-success steps. Optional
            # steps or a relaxed shell would invalidate that failure model.
            if controls:
                raise ValueError('Final publication gates must use required default-success shell steps')
            steps.append('\n'.join(command))
    for line in block.splitlines():
        if line.startswith('      - '):
            finish()
            command, controls = None, []
            if line[8:].startswith(('if:', 'continue-on-error:', 'shell:')):
                controls.append(line)
        elif line.startswith(('        if:', '        continue-on-error:', '        shell:')):
            controls.append(line)
        elif line == '        run: |':
            command = []
        elif command is not None:
            if line.startswith('          '):
                command.append(line[10:])
            elif line.strip():
                finish()
                command = None
    if command is not None:
        finish()
    return steps


class PublicationCompletionTests(unittest.TestCase):
    def test_publication_steps_cannot_be_made_optional_or_ignore_shell_failures(self):
        workflow = (ROOT / '.github/workflows/release.yml').read_text()
        marker = '      - name: Download and verify the actual public assets without credentials\n'
        self.assertIn(marker, workflow)
        for control in ('        if: ${{ false }}\n', '        continue-on-error: true\n',
                        '        shell: bash {0}\n'):
            with self.subTest(control=control), self.assertRaises(ValueError):
                completion_steps(workflow.replace(marker, marker + control))
        with self.assertRaises(ValueError):
            completion_steps(workflow.replace(marker, '      - if: ${{ false }}\n        name: Public download\n'))

    def execute(self, fault=''):
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary)
            for directory in ('bin', 'release-inputs/security-metadata', 'release-inputs/release-files',
                              'attestation-parts', 'packaged-evidence'):
                (work / directory).mkdir(parents=True)
            (work / 'release-inputs/security-metadata/release-metadata.json').write_text('{}')
            (work / 'release-inputs/release-files/romd-v1.2.3-rc.1-deployment.tar.gz').write_bytes(b'bundle')
            double = f'#!{sys.executable}\n' + '''import json, os, pathlib, sys
args = sys.argv[1:]
with open('calls.jsonl', 'a') as log:
    log.write(json.dumps([pathlib.Path(sys.argv[0]).name, *args]) + '\\n')
fault = os.environ.get('TEST_PUBLICATION_FAULT', '')
if pathlib.Path(sys.argv[0]).name == 'gh':
    if args[:2] == ['release', 'upload'] and fault == 'upload':
        sys.exit(1)
    sys.exit(0)
script = args[0]
if script.endswith('attestation_files.py'):
    pathlib.Path('attestations.jsonl').write_text('{}\\n')
elif script.endswith('download_release.py'):
    if fault in ('private-404', 'missing-asset', 'altered-download'):
        sys.exit(1)
    directory = pathlib.Path(args[args.index('--output') + 1])
    directory.mkdir()
    (directory / 'romd-v1.2.3-rc.1-deployment.tar.gz').write_bytes(b'bundle')
    (directory / 'release-metadata.json').write_text('{}')
    (directory / 'attestations.jsonl').write_text('{}\\n')
elif script.endswith('verify.py'):
    bundle = pathlib.Path(args[args.index('--bundle') + 1])
    directory = pathlib.Path(args[args.index('--metadata-dir') + 1])
    attestations = pathlib.Path(args[args.index('--attestations') + 1])
    if not bundle.is_file() or not (directory / 'release-metadata.json').is_file() or not attestations.is_file():
        sys.exit(1)
    if 'tampered' in bundle.parts:
        sys.exit(0 if fault == 'accept-tampered' else 1)
    if fault == 'public-verification' and directory == pathlib.Path('public-download'):
        sys.exit(1)
    if fault == 'source-verification' and directory != pathlib.Path('public-download'):
        sys.exit(1)
else:
    sys.exit('Unexpected command double')
'''
            for name in ('gh', 'python3'):
                path = work / 'bin' / name
                path.write_text(double)
                path.chmod(0o700)
            environment = dict(os.environ, PATH=str(work / 'bin') + os.pathsep + os.environ['PATH'],
                               RELEASE_TAG='v1.2.3-rc.1', SOURCE_REVISION='a' * 40,
                               GITHUB_REPOSITORY='owner/repo', TEST_PUBLICATION_FAULT=fault)
            result = None
            for command in completion_steps():
                result = subprocess.run(['bash', '-e', '-o', 'pipefail', '-c', command],
                                        cwd=work, env=environment, capture_output=True, text=True)
                if result.returncode:
                    break
            calls = [json.loads(line) for line in (work / 'calls.jsonl').read_text().splitlines()]
            return result, calls

    def test_only_verified_actual_anonymous_downloads_can_receive_qualification_notes(self):
        result, calls = self.execute()
        self.assertEqual(0, result.returncode, result.stderr)
        upload = next(i for i, command in enumerate(calls) if command[:3] == ['gh', 'release', 'upload'])
        download = next(i for i, command in enumerate(calls) if 'scripts/security/download_release.py' in command)
        verifications = [(i, command) for i, command in enumerate(calls) if 'scripts/security/verify.py' in command]
        self.assertEqual(3, len(verifications))
        public, tampered = verifications[1:]
        self.assertEqual('public-download', public[1][public[1].index('--metadata-dir') + 1])
        self.assertEqual('public-download/attestations.jsonl', public[1][public[1].index('--attestations') + 1])
        self.assertEqual('public-download/romd-v1.2.3-rc.1-deployment.tar.gz',
                         public[1][public[1].index('--bundle') + 1])
        self.assertEqual('public-download', tampered[1][tampered[1].index('--metadata-dir') + 1])
        edit = next(i for i, command in enumerate(calls) if command[:3] == ['gh', 'release', 'edit'])
        self.assertLess(verifications[0][0], upload)
        self.assertLess(upload, download)
        self.assertLess(download, public[0])
        self.assertLess(public[0], tampered[0])
        self.assertLess(tampered[0], edit)
        self.assertFalse(any('--clobber' in command for command in calls))

    def test_failed_upload_download_verification_or_tamper_gate_never_marks_qualified(self):
        for fault in ('upload', 'private-404', 'missing-asset', 'altered-download',
                      'public-verification', 'source-verification', 'accept-tampered'):
            with self.subTest(fault=fault):
                result, calls = self.execute(fault)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(any(command[:3] == ['gh', 'release', 'edit'] for command in calls))


if __name__ == '__main__':
    unittest.main()
