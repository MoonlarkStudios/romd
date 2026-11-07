import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import tomllib
import unittest
from unittest.mock import patch

from routes import check_registry
from run import execute

ROOT = Path(__file__).resolve().parents[2]


class RouteTests(unittest.TestCase):
    def setUp(self):
        self.registry = json.loads((ROOT / 'scripts/validation/checks.json').read_text())
        self.mise = tomllib.loads((ROOT / '.mise.toml').read_text())

    def errors(self, registry=None, workflow=None):
        errors = []
        check_registry(ROOT, [], self.mise, errors, registry or self.registry, workflow)
        return errors

    def test_current_routes_are_executable(self):
        self.assertEqual([], self.errors())

    def test_postgresql_routes_cannot_lose_runtime_environment(self):
        for id in ('backend.infrastructure', 'backend.integration'):
            registry = copy.deepcopy(self.registry)
            next(c for c in registry['checks'] if c['id'] == id).pop('container_environment')
            self.assertTrue(any(id + ': local PostgreSQL' in error for error in self.errors(registry)))

    def test_runtime_environment_is_scoped_to_local_checks_and_ci_stays_docker(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            subprocess.run(['git', 'init', '--quiet', str(root)], check=True)
            subprocess.run(['git', '-C', str(root), '-c', 'user.name=Test', '-c', 'user.email=test@example.test',
                            'commit', '--allow-empty', '-qm', 'fixture'], check=True)
            script = 'import os; print(os.environ["ROMD_CONTAINER_ENGINE"])'
            check = {'id': 'runtime', 'cwd': '.', 'container_environment': True,
                     'local': [sys.executable, '-c', script], 'ci': [sys.executable, '-c', script]}
            checks = [check, check | {'id': 'runtime-second'}]
            for ci, expected in ((False, 'podman'), (True, 'docker')):
                evidence = root / str(ci)
                evidence.mkdir()
                with patch('run.Runtime') as selected:
                    selected.return_value.child_environment.return_value = os.environ | {'ROMD_CONTAINER_ENGINE': 'podman'}
                    self.assertEqual(0, execute(checks, root, evidence, ci=ci))
                    if ci:
                        selected.assert_not_called()
                    else:
                        selected.assert_called_once()
                        self.assertEqual(selected.return_value.child_environment.call_count, 2)
                        selected.return_value.child_environment.assert_called_with(tests=True)
                self.assertEqual(expected, (evidence / 'runtime.log').read_text().strip())
                self.assertEqual(expected, (evidence / 'runtime-second.log').read_text().strip())

    def test_omitted_recipe_local_route_fails(self):
        next(c for c in self.registry['checks'] if c['id'] == 'media.recipe-reports')['groups'] = []
        self.assertTrue(any('media.recipe-reports' in e for e in self.errors()))

    def test_recipe_is_required_in_ci_and_has_real_commands(self):
        for field, value in [('required_ci', False), ('ci', ['echo', 'passed']),
                             ('ci', ['echo', 'scripts/ci/recipe_reports.py']), ('local', [])]:
            registry = copy.deepcopy(self.registry)
            next(c for c in registry['checks'] if c['id'] == 'media.recipe-reports')[field] = value
            self.assertTrue(any('media.recipe-reports' in e for e in self.errors(registry)))

    def test_missing_process_check_fails(self):
        self.registry['checks'] = [c for c in self.registry['checks'] if c['id'] != 'media.extraction']
        self.assertTrue(any('media.extraction' in e for e in self.errors()))

    def test_console_analysis_theme_and_full_tests_cannot_be_optional(self):
        for id in ('console.analyze', 'console.tests', 'console.check_widget_style_literals'):
            registry = copy.deepcopy(self.registry)
            next(c for c in registry['checks'] if c['id'] == id)['required_ci'] = False
            self.assertTrue(any(id in e for e in self.errors(registry)))

    def test_source_advisories_cannot_be_omitted_disabled_or_lost_locally(self):
        for field, value in (('required_ci', False), ('groups', []), ('ci_job', 'other'),
                             ('local', ['echo', 'passed']), ('ci', ['echo', 'passed'])):
            registry = copy.deepcopy(self.registry)
            next(c for c in registry['checks'] if c['id'] == 'dependencies.source')[field] = value
            self.assertTrue(any('dependencies.source' in e for e in self.errors(registry)))
        mise = copy.deepcopy(self.mise)
        mise['tasks']['dependencies:check']['run'] = 'echo passed'
        errors = []
        check_registry(ROOT, [], mise, errors, self.registry)
        self.assertTrue(any('dependencies:check' in error for error in errors))

    def test_required_ci_label_without_workflow_route_fails(self):
        self.assertTrue(any('no executable workflow route' in e for e in self.errors(workflow='run: echo skipped')))

    def test_inventory_short_circuit_and_skipped_workflows_do_not_count(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        route = 'python3 scripts/validation/run.py --ci --job backend'
        for replacement in (route + ' --list', route + ' && true', 'echo ' + route):
            errors = self.errors(workflow=workflow.replace(route, replacement))
            self.assertTrue(any('media.recipe-reports: required CI' in e for e in errors))
        for condition in ('if: false', 'continue-on-error: true'):
            errors = self.errors(workflow=workflow.replace('  backend:\n', '  backend:\n    ' + condition + '\n'))
            self.assertTrue(any('media.recipe-reports: required CI' in e for e in errors))

    def test_partitioned_checks_require_their_filter_route(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        for job, id in (('infrastructure', 'backend.infrastructure'), ('integration', 'backend.integration')):
            route = f"python3 scripts/validation/run.py --ci --job {job} --filter '${{{{ matrix.filter }}}}'"
            self.assertIn(route, workflow)
            errors = self.errors(workflow=workflow.replace(route, f'python3 scripts/validation/run.py --ci --job {job}'))
            self.assertTrue(any(e.startswith(f'{id}: required CI') for e in errors), errors)

    def parity_errors(self, workflow, mise=None):
        errors = []
        check_registry(ROOT, [], mise or self.mise, errors, self.registry, ci_workflow_text=workflow)
        return [e for e in errors if e.startswith(('ci.yml', 'CI_ONLY_STEPS'))]

    def test_unlisted_ci_step_without_local_route_fails_parity(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        self.assertEqual([], self.parity_errors(workflow))
        anchor = '      - name: Run registered web checks\n'
        added = workflow.replace(anchor, '      - name: New hosted-only gate\n'
                                 '        run: python3 scripts/new_gate.py\n' + anchor)
        self.assertTrue(any('web/New hosted-only gate: CI step has no local route' in e
                            for e in self.parity_errors(added)))
        for route in ('python3 scripts/validation/run.py --ci --job missing-job',
                      'python3 scripts/validation/run.py --ci --id missing.id',
                      'python3 scripts/validation/run.py --job web',
                      'echo python3 scripts/validation/run.py --ci --job web'):
            with self.subTest(route=route):
                unrouted = workflow.replace('python3 scripts/validation/run.py --ci --job web --evidence-parent coverage/web',
                                            route)
                self.assertTrue(any('web/Run registered web checks: CI step has no local route' in e
                                    for e in self.parity_errors(unrouted)))

    def test_allowlisted_ci_steps_cannot_be_repurposed_or_go_stale(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        repurposed = workflow.replace('run: python3 scripts/deployment/smoke.py --build --vfs',
                                      'run: python3 scripts/deployment/other.py')
        self.assertTrue(any('command changed from allowlisted' in e for e in self.parity_errors(repurposed)))
        removed = workflow.replace('      - name: Build images and exercise production deployment\n'
                                   "        if: ${{ needs.source-checks.outputs.chd == 'true' }}\n"
                                   '        run: python3 scripts/deployment/smoke.py --build --vfs --evidence-dir "$RUNNER_TEMP/production-smoke"\n', '')
        self.assertNotEqual(workflow, removed)
        self.assertTrue(any('stale allowlist entry' in e for e in self.parity_errors(removed)))
        mise = copy.deepcopy(self.mise)
        mise['tasks']['native:test-inputs']['run'] = 'scripts/native/build.sh libchdr'
        self.assertTrue(any('local task native:test-inputs does not run this command' in e
                            for e in self.parity_errors(workflow, mise)))

    def test_new_test_project_requires_local_and_ci_routes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            relative = 'tests/New.Tests/New.Tests.csproj'
            path = root / relative
            path.parent.mkdir(parents=True)
            path.write_text('<Project><IsTestProject>true</IsTestProject></Project>')
            errors = []
            check_registry(root, [relative], self.mise, errors, self.registry, '')
            self.assertTrue(any(relative + ': missing local' in e for e in errors))
            self.assertTrue(any(relative + ': missing required CI' in e for e in errors))

    def test_failure_retains_command_exit_status_and_log(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            subprocess.run(['git', 'init', '--quiet', str(root)], check=True)
            subprocess.run(['git', '-C', str(root), '-c', 'user.name=Test', '-c', 'user.email=test@example.test',
                            'commit', '--allow-empty', '-qm', 'fixture'], check=True)
            evidence = root / 'evidence'
            evidence.mkdir()
            checks = [{'id': 'failure', 'cwd': '.', 'local': [sys.executable, '-c', 'print("retained"); raise SystemExit(7)']}]
            self.assertEqual(7, execute(checks, root, evidence))
            self.assertIn('retained', (evidence / 'failure.log').read_text())
            self.assertEqual(7, json.loads((evidence / 'results.json').read_text())['checks'][0]['exit_code'])
