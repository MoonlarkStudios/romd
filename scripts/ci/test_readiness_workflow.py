from pathlib import Path
import unittest

from lint_workflows import policy_errors, readiness_errors


class ReadinessWorkflowTests(unittest.TestCase):
    def test_rehearsal_cannot_drop_coverage_ignore_failure_or_gain_publication(self):
        text = (Path(__file__).resolve().parents[2] / '.github/workflows/release-readiness.yml').read_text()
        self.assertEqual([], policy_errors(text, 'release-readiness.yml'))
        self.assertEqual([], readiness_errors(text, 'release-readiness.yml'))
        for changed in (
            text.replace('17 6 * * *', '17 6 * * 1'),
            text.replace('  workflow_dispatch:\n', '  push:\n'),
            text.replace('  rehearse:\n', '  rehearse:\n    if: false\n'),
            text.replace('  rehearse:\n', '  rehearse:\n    continue-on-error: true\n'),
            text.replace('contents: read', 'contents: write'),
            text.replace('cancel-in-progress: false', 'cancel-in-progress: true'),
            text.replace('          ref: ${{ inputs.revision || github.sha }}', '          ref: main'),
            text.replace('python3 scripts/release/readiness.py', 'echo readiness'),
            text.replace('      - name: Build and run the common rehearsal gates\n',
                         '      - name: Build and run the common rehearsal gates\n        if: false\n'),
            text.replace('      - name: Build and run the common rehearsal gates\n',
                         '      - name: Build and run the common rehearsal gates\n        continue-on-error: true\n'),
            text.replace('retention-days: 7', 'retention-days: 90'),
            text.replace('if-no-files-found: error', 'if-no-files-found: ignore'),
            text.replace('    steps:\n', '    steps:\n      - run: docker push ghcr.io/example/image\n'),
            text.replace('    steps:\n', '    steps:\n      - run: gh release create v1.0.0\n'),
            text.replace('    steps:\n', '    steps:\n      - run: echo token\n        env: {GH_TOKEN: "${{ secrets.GITHUB_TOKEN }}"}\n'),
        ):
            with self.subTest(changed=changed):
                self.assertNotEqual(text, changed)
                self.assertTrue(policy_errors(changed, 'release-readiness.yml')
                                + readiness_errors(changed, 'release-readiness.yml'))


if __name__ == '__main__':
    unittest.main()
