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
        marker = '      - name: Verify the bundle and every image as an operator would, without credentials\n'
        self.assertIn(marker, workflow)
        for control in ('        if: ${{ false }}\n', '        continue-on-error: true\n',
                        '        shell: bash {0}\n'):
            with self.subTest(control=control), self.assertRaises(ValueError):
                completion_steps(workflow.replace(marker, marker + control))
        with self.assertRaises(ValueError):
            completion_steps(workflow.replace(marker, '      - if: ${{ false }}\n        name: Verification\n'))

    def execute(self, fault=''):
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary)
            for directory in ('bin', 'release-inputs/security-metadata', 'release-inputs/release-files',
                              'attestation-parts', 'packaged-evidence', 'scripts/deployment', 'native/chdman'):
                (work / directory).mkdir(parents=True)
            (work / 'scripts/deployment/base-images.json').write_text(json.dumps({'ingress': 'nginx@sha256:' + 'f' * 64}))
            (work / 'native/chdman/mame.json').write_text(json.dumps({'sourceUrl': 'https://example.test/mame0289.tar.gz'}))
            (work / 'release-inputs/release-files/romd-v1.2.3-rc.1-deployment.tar.gz').write_bytes(b'bundle')
            double = f'#!{sys.executable}\n' + '''import json, os, pathlib, sys
args = sys.argv[1:]
with open('calls.jsonl', 'a') as log:
    log.write(json.dumps([pathlib.Path(sys.argv[0]).name, *args]) + '\\n')
fault = os.environ.get('TEST_PUBLICATION_FAULT', '')
if pathlib.Path(sys.argv[0]).name == 'gh':
    if args[:2] == ['release', 'upload'] and fault == 'upload':
        sys.exit(1)
    if args[:2] == ['release', 'edit'] and fault == 'publish':
        sys.exit(1)
    sys.exit(0)
script = args[0]
if script.endswith('suite.py'):
    if '--verify-suite' not in args or '--rehearsal' in args or fault == 'runtime-suite':
        sys.exit(1)
    if args[args.index('--publisher-state') + 1] != 'release-inputs/release-files/candidate.json':
        sys.exit('Runtime evidence must be checked against the same-run publisher state')
elif script.endswith('attestation_files.py'):
    if fault == 'merge':
        sys.exit(1)
    pathlib.Path(args[args.index('--output') + 1]).write_text('{}\\n')
elif script.endswith('verify_release.py'):
    attestations = pathlib.Path(args[args.index('--attestations') + 1])
    if not attestations.is_file() or fault == 'verify-release':
        sys.exit(1)
    if args[args.index('--images') + 1] != 'release-inputs/release-files/images.env':
        sys.exit('Release images must come from the same-run publisher state')
elif script.endswith('release_notes.py'):
    if '--qualified' not in args or fault == 'qualification-notes':
        sys.exit(1)
    pathlib.Path(args[args.index('--output') + 1]).write_text('Qualified prerelease with installation links')
elif script.endswith('confirm_publication.py'):
    sys.exit(1 if fault == 'not-immutable' else 0)
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

    def index(self, calls, predicate):
        return next(i for i, command in enumerate(calls) if predicate(command))

    def test_release_is_published_once_only_after_runtime_evidence_and_attestations_verify(self):
        result, calls = self.execute()
        self.assertEqual(0, result.returncode, result.stderr)
        suite = self.index(calls, lambda command: 'scripts/release/suite.py' in command)
        merge = self.index(calls, lambda command: 'scripts/release/attestation_files.py' in command)
        verify = self.index(calls, lambda command: 'scripts/release/verify_release.py' in command)
        upload = self.index(calls, lambda command: command[:3] == ['gh', 'release', 'upload'])
        edit = self.index(calls, lambda command: command[:3] == ['gh', 'release', 'edit'])
        confirm = self.index(calls, lambda command: 'scripts/release/confirm_publication.py' in command)
        self.assertEqual(sorted([suite, merge, verify, upload, edit, confirm]), [suite, merge, verify, upload, edit, confirm])
        self.assertEqual(['romd-v1.2.3-rc.1-attestations.jsonl'], [part for part in calls[upload] if part.endswith('.jsonl')])
        self.assertIn('--draft=false', calls[edit])
        self.assertIn('--prerelease', calls[edit])
        self.assertEqual(1, sum(command[:3] == ['gh', 'release', 'edit'] for command in calls))
        verified = calls[verify]
        self.assertEqual('romd-v1.2.3-rc.1-attestations.jsonl', verified[verified.index('--attestations') + 1])
        confirmed = calls[confirm]
        self.assertEqual(3, confirmed.count('--asset'))
        self.assertEqual('mame0289.tar.gz', confirmed[confirmed.index('--source-name') + 1])
        self.assertFalse(any('--clobber' in command for command in calls))

    def test_failed_evidence_verification_or_upload_never_publishes(self):
        for fault in ('runtime-suite', 'merge', 'verify-release', 'upload', 'qualification-notes'):
            with self.subTest(fault=fault):
                result, calls = self.execute(fault)
                self.assertNotEqual(0, result.returncode)
                self.assertFalse(any(command[:3] == ['gh', 'release', 'edit'] for command in calls))

    def test_unconfirmed_publication_fails_the_release(self):
        for fault in ('publish', 'not-immutable'):
            with self.subTest(fault=fault):
                result, _ = self.execute(fault)
                self.assertNotEqual(0, result.returncode)


if __name__ == '__main__':
    unittest.main()
