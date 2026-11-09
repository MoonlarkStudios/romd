from pathlib import Path
import unittest
import yaml
from lint_workflows import GATE_CHECK, NETWORK_SETUP, NETWORK_VERIFY, concurrency_errors, environment_errors, gate_errors, policy_errors, release_gate_errors, early_errors, maintenance_errors, network_probe_errors

HEADER = 'permissions:\n  contents: read\njobs:\n  test:\n    steps:\n'


class WorkflowPolicyTests(unittest.TestCase):
    def test_network_probes_cannot_be_silently_skipped_or_verified_before_execution(self):
        text = (Path(__file__).resolve().parents[2] / '.github/workflows/ci.yml').read_text()
        self.assertEqual([], network_probe_errors(text, 'ci.yml'))
        for changed in (text.replace('python3 scripts/ci/network_probes.py prepare', 'echo skipped'),
                        text.replace(NETWORK_VERIFY, 'echo skipped'),
                        text.replace('      - name: Prepare verified loopback network probe tools\n',
                                     '      - name: Prepare verified loopback network probe tools\n        if: false\n'),
                        text.replace('      - name: Require executed nginx and rclone protocol probes\n',
                                     '      - name: Require executed nginx and rclone protocol probes\n        continue-on-error: true\n'),
                        text.replace('  integration:\n', '  integration:\n    if: false\n'),
                        text.replace('          - part: hosting\n', '          - part: renamed\n'),
                        text.replace('            !coverage/integration/**/integration.trx\n', '')):
            with self.subTest(changed=changed):
                self.assertNotEqual(text, changed)
                self.assertTrue(network_probe_errors(changed, 'ci.yml'))
        workflow = yaml.safe_load(text)
        steps = workflow['jobs']['integration']['steps']
        prepare = next(i for i, step in enumerate(steps) if step.get('run') == NETWORK_SETUP)
        verify = next(i for i, step in enumerate(steps) if step.get('run') == NETWORK_VERIFY)
        steps[prepare], steps[verify] = steps[verify], steps[prepare]
        self.assertTrue(network_probe_errors(yaml.safe_dump(workflow), 'ci.yml'))

    def test_early_checks_and_expensive_chd_conditions_cannot_be_bypassed(self):
        text = (Path(__file__).resolve().parents[2] / '.github/workflows/ci.yml').read_text()
        self.assertEqual([], early_errors(text, 'ci.yml'))
        for changed in (text.replace('    needs: source-checks\n', ''),
                        text.replace('  source-checks:\n', '  source-checks:\n    if: false\n'),
                        text.replace('python3 scripts/ci/secret_scan.py --gitleaks', 'echo skipped --gitleaks'),
                        text.replace('run: python3 scripts/ci/change_scope.py', 'run: echo chd=false'),
                        text.replace("${{ needs.source-checks.outputs.chd == 'true' }}", '${{ false }}'),
                        text.replace('python3 scripts/ci/nuget_locks.py', 'echo locks'),
                        text.replace('--job source-checks --evidence-parent', '--job backend --evidence-parent')):
            with self.subTest(changed=changed):
                self.assertNotEqual(text, changed)
                self.assertTrue(early_errors(changed, 'ci.yml'))

    def test_maintenance_is_scheduled_or_manual_with_only_sarif_write_access(self):
        text = (Path(__file__).resolve().parents[2] / '.github/workflows/security-maintenance.yml').read_text()
        self.assertEqual([], policy_errors(text, 'security-maintenance.yml'))
        self.assertEqual([], maintenance_errors(text, 'security-maintenance.yml'))
        for changed in (text.replace('  schedule:', '  pull_request:\n  schedule:'),
                        text.replace('47 6 * * 0', '47 6 * * *'),
                        text.replace('security-events: write', 'contents: write'),
                        text.replace('  codeql:\n', '  codeql:\n    continue-on-error: true\n'),
                        text.replace('csharp, javascript-typescript, python, actions', 'csharp'),
                        text.replace('scripts/ci/secret_scan.py --full', 'scripts/ci/secret_scan.py'),
                        text.replace('scripts/native/fuzz.sh gate', 'echo gate'),
                        text.replace("&& '1800'", "&& '60'"),
                        text.replace(' -m:1', ''),
                        text.replace('dotnet build Romd.sln', 'dotnet restore Romd.sln'),
                        text.replace('dotnet clean Romd.sln -c Release -m:1\n', ''),
                        text.replace('github/codeql-action/analyze@', 'github/codeql-action/autobuild@')):
            with self.subTest(changed=changed):
                self.assertNotEqual(text, changed)
                self.assertTrue(maintenance_errors(changed, 'security-maintenance.yml'))
        self.assertTrue(policy_errors(text.replace('security-events: write', 'packages: write'), 'security-maintenance.yml'))
        self.assertTrue(policy_errors(text, 'other.yml'))

    def test_dependency_gate_runs_on_pr_main_and_schedule_without_publish_permissions(self):
        text = (Path(__file__).resolve().parents[2] / '.github/workflows/dependencies.yml').read_text()
        self.assertEqual([], policy_errors(text, 'dependencies.yml'))
        self.assertEqual([], concurrency_errors(text, 'dependencies.yml'))
        for changed in (text.replace('  pull_request:\n', ''),
                        text.replace('  workflow_call:\n', ''),
                        text.replace("    - cron: '23 6 * * *'", "    - cron: '23 6 * * 1'"),
                        text.replace('  dependencies:\n', '  dependencies:\n    if: false\n'),
                        text.replace('  dependencies:\n', '  dependencies:\n    continue-on-error: true\n'),
                        text.replace('  dependencies:\n', '  dependencies:\n    permissions: {contents: write}\n'),
                        text.replace('  dependencies:\n', '  duplicate-ci:\n    uses: ./.github/workflows/ci.yml\n  dependencies:\n'),
                        text.replace('run: python3 scripts/validation/run.py', 'run: echo python3 scripts/validation/run.py')):
            with self.subTest(changed=changed):
                self.assertTrue(policy_errors(changed, 'dependencies.yml'))
        self.assertTrue(concurrency_errors(text.replace('github.run_id', 'github.ref'), 'dependencies.yml'))

    def test_release_cannot_publish_without_same_revision_advisories(self):
        text = (Path(__file__).resolve().parents[2] / '.github/workflows/release.yml').read_text()
        self.assertEqual([], release_gate_errors(text, 'release.yml'))
        for changed in (text.replace(', advisories]', ']'),
                        text.replace('  advisories:\n', '  advisories:\n    if: false\n'),
                        text.replace('  advisories:\n', '  advisories:\n    continue-on-error: true\n'),
                        text.replace('./.github/workflows/dependencies.yml', 'owner/repo/.github/workflows/dependencies.yml@main'),
                        text.replace('  publish:\n', '  publish:\n    if: ${{ always() }}\n'),
                        text.replace('  verify:\n', '  verify:\n    if: false\n')):
            with self.subTest(changed=changed):
                self.assertNotEqual(text, changed)
                self.assertTrue(release_gate_errors(changed, 'release.yml'))

    def test_pr_cancellation_cannot_cancel_release_or_other_workflow_invocations(self):
        ci = ("concurrency:\n  group: ci-${{ github.event_name == 'pull_request' && github.event.pull_request.number || github.run_id }}\n"
              "  cancel-in-progress: ${{ github.event_name == 'pull_request' }}\n")
        self.assertEqual([], concurrency_errors(ci, 'ci.yml'))
        for unsafe in (ci.replace("${{ github.event_name == 'pull_request' }}", 'true'),
                       ci.replace('github.run_id', 'github.ref')):
            self.assertTrue(concurrency_errors(unsafe, 'ci.yml'))
        release = 'concurrency: {group: "release-${{ github.ref }}", cancel-in-progress: false}\n'
        self.assertEqual([], concurrency_errors(release, 'release.yml'))
        self.assertTrue(concurrency_errors(release.replace('false', 'true'), 'release.yml'))

    def test_ci_gate_requires_every_job_to_succeed(self):
        text = (Path(__file__).resolve().parents[2] / '.github/workflows/ci.yml').read_text()
        self.assertEqual([], gate_errors(text, 'ci.yml'))
        self.assertEqual([], gate_errors(text.replace('ci-gate', 'other-name'), 'release.yml'))
        for changed in (text.replace('  ci-gate:\n', '  renamed-gate:\n'),
                        text.replace('    if: ${{ always() }}\n', "    if: ${{ !cancelled() }}\n"),
                        text.replace('    if: ${{ always() }}\n', '    if: ${{ always() }}\n    continue-on-error: true\n'),
                        text.replace(', production-compose]', ']'),
                        text.replace('needs: [', 'needs: [missing-job, '),
                        text.replace('    name: CI gate', '    name: Other check'),
                        text.replace(GATE_CHECK, 'true'),
                        text.replace('${{ toJSON(needs) }}', '${{ toJSON(needs.web) }}'),
                        text.replace('  ci-gate:\n', '  extra:\n    runs-on: ubuntu-24.04\n    steps: []\n  ci-gate:\n')):
            with self.subTest(changed=changed):
                self.assertNotEqual(text, changed)
                self.assertTrue(gate_errors(changed, 'ci.yml'))

    def test_publisher_waits_for_the_release_environment(self):
        text = (Path(__file__).resolve().parents[2] / '.github/workflows/release.yml').read_text()
        self.assertEqual([], environment_errors(text, 'release.yml'))
        self.assertEqual([], environment_errors(text.replace('    environment: release\n', ''), 'ci.yml'))
        mapped = text.replace('    environment: release\n', '    environment:\n      name: release\n')
        self.assertEqual([], environment_errors(mapped, 'release.yml'))
        for changed in (text.replace('    environment: release\n', ''),
                        text.replace('    environment: release\n', '    environment: staging\n'),
                        text.replace('    environment: release\n', '    environment:\n      name: other\n')):
            with self.subTest(changed=changed):
                self.assertNotEqual(text, changed)
                self.assertTrue(environment_errors(changed, 'release.yml'))

    def test_runner_images_are_pinned(self):
        def workflow(runner):
            return HEADER.replace('    steps:\n', f'    runs-on: {runner}\n    steps:\n') + '      - run: "true"\n'

        for runner in ('ubuntu-24.04', '[ubuntu-24.04]'):
            self.assertEqual([], policy_errors(workflow(runner), 'ci.yml'))
        for runner in ('ubuntu-latest', 'macos-latest', '[self-hosted, ubuntu-latest]', 'self-hosted',
                       '[self-hosted, linux]', 'ubuntu-24.04-16core', '{group: internal}', '${{ matrix.runner }}'):
            with self.subTest(runner=runner):
                self.assertTrue(policy_errors(workflow(runner), 'ci.yml'))

    def test_immutable_actions_and_local_workflows(self):
        self.assertEqual([], policy_errors(HEADER + '      - uses: a/b@' + 'a' * 40, 'ci.yml'))
        self.assertEqual([], policy_errors(HEADER + '      - uses: ./.github/workflows/ci.yml', 'ci.yml'))
        for pin in ('v1', 'abcdef'):
            self.assertTrue(policy_errors(HEADER + '      - uses: a/b@' + pin, 'ci.yml'))

    def test_only_publisher_gets_writes_regardless_of_yaml_indentation(self):
        for indent in (2, 4, 6):
            text = ('permissions: {contents: read}\njobs:\n' + ' ' * indent + 'publish:\n'
                    + ' ' * (indent + 2) + 'permissions: {packages: write}\n')
            self.assertTrue(policy_errors(text, 'ci.yml'))
            self.assertEqual([], policy_errors(text, 'release.yml'))
            self.assertTrue(policy_errors(text.replace('packages', 'issues'), 'release.yml'))

    def test_named_checkout_cannot_persist_credentials(self):
        text = HEADER + '      - name: Checkout\n        uses: actions/checkout@' + 'a' * 40 + '\n'
        self.assertTrue(policy_errors(text, 'ci.yml'))
        self.assertEqual([], policy_errors(text + '        with: {persist-credentials: false}\n', 'ci.yml'))

    def test_attestation_and_final_publication_permissions_are_separate(self):
        for role, allowed in (('publish-metadata', ('id-token', 'attestations')),
                              ('publish-complete', ('contents',))):
            for permission in ('contents', 'packages', 'id-token', 'attestations', 'issues'):
                workflow = f'permissions: {{contents: read}}\njobs:\n  {role}:\n    permissions: {{{permission}: write}}\n'
                self.assertEqual(permission not in allowed, bool(policy_errors(workflow, 'release.yml')))
                self.assertTrue(policy_errors(workflow, 'ci.yml'))

    def test_privileged_triggers_secret_inheritance_and_duplicate_maps_fail(self):
        self.assertTrue(policy_errors('on: [pull_request_target]\n' + HEADER, 'ci.yml'))
        self.assertTrue(policy_errors('permissions: write-all\n', 'ci.yml'))
        self.assertTrue(policy_errors('permissions: {contents: read}\njobs:\n  test:\n    secrets: inherit\n', 'ci.yml'))
        self.assertTrue(policy_errors('permissions: {contents: read}\npermissions: {}\n', 'ci.yml'))
