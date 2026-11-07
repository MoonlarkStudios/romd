"""Structural coverage checks, separate from execution and hosted qualification."""
import json
from pathlib import Path
import re
import shlex
import yaml

PROCESS = {
    'media.staging': ['dotnet', '{probe}', '--staging-probe', '{evidence}/staging.json'],
    'media.extraction': ['dotnet', '{probe}', '--extraction-probe', '{evidence}/extraction.json'],
    'media.inspection-reports': ['python3', 'tools/Romd.Media.Probe/test-inspection-reports.py', '{probe}'],
    'media.recipe-reports': ['python3', 'scripts/ci/recipe_reports.py', '{probe}', '--evidence-parent', '{evidence}'],
}
CONSOLE = {
    **{'console.' + name: ['tool/' + name + '.sh'] for name in
       ('check_token_imports', 'check_theme_token_registry', 'check_theme_token_usage',
        'check_theme_extension_contracts', 'check_widget_style_literals')},
    'console.analyze': ['flutter', 'analyze'],
    'console.tests': ['flutter', 'test'],
}
DEPENDENCIES = ['.artifacts/ci-python/bin/python', 'scripts/security/source_advisories.py',
                '--output', '{evidence}/dependencies.json']


# CI -> registry parity: every `run:` step in ci.yml must execute registered checks through
# `python3 scripts/validation/run.py --ci`, or be listed here. Keep this list to setup, install,
# build, packaging and tooling steps plus checks that already have an exact local task.
# Key: (job, step name). `command` must prefix the step's first line, so a listed step cannot be
# repurposed silently. `local` names a .mise.toml task whose single command must equal (or
# token-prefix) the CI command; entries without one have no local route and say why.
CI_ONLY_STEPS = {
    ('source-checks', 'Prepare hash-locked workflow parser'):
        dict(command='python3 scripts/ci/prepare.py', local='workflows:lint'),
    ('source-checks', 'Enforce workflow security policy'):
        dict(command='.artifacts/ci-python/bin/python scripts/ci/lint_workflows.py', local='workflows:lint'),
    ('source-checks', 'Install authenticated secret scanner'):
        dict(command='python3 scripts/publication/install_scanner.py', reason='checksum-pinned tool setup in runner temporary storage'),
    ('source-checks', 'Test workflow security and CI helpers with the actual scanner'):
        dict(command='.artifacts/ci-python/bin/python -B -W error -m unittest discover -s scripts/ci', local='workflows:lint'),
    ('source-checks', 'Scan new commits for secrets'):
        dict(command='python3 scripts/ci/secret_scan.py', local='secrets:check'),
    ('source-checks', 'Select expensive CHD verification'):
        dict(command='python3 scripts/ci/change_scope.py', reason='conservative PR-only selection; main and releases always full'),
    ('source-checks', 'Lint workflows'):
        dict(command='curl --fail', reason='downloads hash-pinned actionlint; local workflows:lint uses mise pin'),
    ('agent-infrastructure', 'Prepare hash-locked workflow parser'):
        dict(command='python3 scripts/ci/prepare.py', local='workflows:lint', reason='tool setup'),
    ('agent-infrastructure', 'Check agent infrastructure'):
        dict(command='.artifacts/ci-python/bin/python scripts/agents/check.py', local='agents:check'),
    ('agent-infrastructure', 'Test infrastructure checker'):
        dict(command='.artifacts/ci-python/bin/python -B -m unittest discover -s scripts/agents', local='agents:check'),
    ('agent-infrastructure', 'Test validation runner'):
        dict(command='.artifacts/ci-python/bin/python -B -m unittest discover -s scripts/validation', local='agents:check'),
    ('console', 'Restore committed console dependencies'):
        dict(command='flutter pub get --enforce-lockfile', reason='dependency install and lockfile pin'),
    ('console', 'Install ripgrep for the theme governance scripts'):
        dict(command='sudo apt-get update', reason='tool install; the hosted runner image has no ripgrep'),
    **{(job, 'Unpack the native linux-x64 build'):
       dict(command='tar -xzf native-linux-x64.tar.gz', reason='artifact transfer from the native job')
       for job in ('backend', 'infrastructure', 'integration')},
    ('source-checks', 'Verify portable and Linux image dependency locks'):
        dict(command='python3 scripts/ci/nuget_locks.py', local='dependencies:locks'),
    ('backend', 'Build backend'): dict(command='dotnet build --configuration Release', reason='restore portable locked assets on this runner, then build before checks'),
    ('infrastructure', 'Restore infrastructure test dependencies'):
        dict(command='dotnet restore tests/Romd.Infrastructure.Tests/', reason='dependency install'),
    ('infrastructure', 'Build infrastructure test surface'):
        dict(command='dotnet build', reason='build before registered checks'),
    ('integration', 'Restore integration dependencies'):
        dict(command='dotnet restore', reason='dependency install'),
    ('integration', 'Build integration test surface'):
        dict(command='dotnet build', reason='build before registered checks'),
    ('native', 'Check the libchdr submodule against its pin'):
        dict(command='test "$(git -C native/libchdr/upstream rev-parse HEAD)"',
             reason='hosted checkout integrity gate; no local route (compare git submodule status with libchdr.json)'),
    ('native', 'Key the chdman build by MAME tarball SHA-256, recipe version, recipe sources and RID'):
        dict(command='sha="$(jq -er .sourceSha256 native/chdman/mame.json)"', reason='cache key'),
    ('native', 'Build native code, fixtures and the drift check'):
        dict(command='scripts/native/build.sh test-inputs', local='native:test-inputs'),
    ('native', 'Package the native linux-x64 build for the backend jobs'):
        dict(command='tar -czf native-linux-x64.tar.gz', reason='artifact packaging'),
    ('web', 'Install web dependencies'): dict(command='pnpm install --frozen-lockfile', reason='dependency install'),
    ('web', 'Stage the once-built frontend assets'):
        dict(command='python3 scripts/ci/frontend_assets.py --stage', local='build:prebuilt'),
    ('web', 'Build production hosts with staged frontend'):
        dict(command='dotnet build -c Release -m:1', local='build:prebuilt'),
    ('web', 'Verify current frontend publication bindings'):
        dict(command='python3 scripts/ci/frontend_assets.py', local='build:prebuilt'),
    ('production-compose', 'Build images and exercise production deployment'):
        dict(command='python3 scripts/deployment/smoke.py --build --vfs',
             reason='Docker-only image build and recovery drill; run it directly with Docker'),
    # Resource setup only: deployment.contracts tests refusal outside hosted CI with fake Docker.
    ('production-compose', 'Reclaim disposable CI build cache'):
        dict(command='bash scripts/ci/reclaim_build_cache.sh',
             reason='disposable GitHub-hosted builder cache only; local/self-hosted cleanup is forbidden'),
    ('production-compose', 'Synthetic PS2 DVD CHD end to end'):
        dict(command='python3 scripts/e2e/chd.py --synthetic', local='e2e:chd:synthetic'),
    ('ci-gate', 'Require every CI job to succeed'):
        dict(command="jq -r 'to_entries[]", reason='aggregates the other job results into one required status check'),
}


def selects_registered_checks(command, checks):
    """A `run.py --ci` step whose --job/--id selection resolves to registered checks."""
    try:
        tokens = shlex.split(command, comments=True)
    except ValueError:
        return False
    if tokens[:3] != ['python3', 'scripts/validation/run.py', '--ci']:
        return False
    options = dict(zip(tokens[3::2], tokens[4::2]))
    if len(tokens) % 2 == 0 or len(options) != (len(tokens) - 3) // 2:
        return False
    if '--job' in options:
        return any(c.get('ci_job') == options['--job'] and c.get('required_ci') for c in checks)
    return '--id' in options and any(c.get('id') == options['--id'] for c in checks)


def check_ci_parity(text, checks, mise, errors, allowed=None):
    allowed = CI_ONLY_STEPS if allowed is None else allowed
    tasks = mise.get('tasks', {})
    seen = set()
    workflow = yaml.safe_load(text) or {}
    for job_name, job in (workflow.get('jobs') or {}).items():
        for step in (job or {}).get('steps') or []:
            if 'run' not in step:
                continue
            command = str(step['run']).strip()
            key = (job_name, step.get('name'))
            if selects_registered_checks(command, checks):
                continue
            entry = allowed.get(key)
            if entry is None:
                errors.append(f'ci.yml {job_name}/{key[1]}: CI step has no local route; register it in '
                              'scripts/validation/checks.json and run it via scripts/validation/run.py --ci, '
                              'or add a commented CI_ONLY_STEPS entry in scripts/validation/routes.py')
                continue
            seen.add(key)
            if not command.splitlines()[0].startswith(entry['command']):
                errors.append(f'ci.yml {job_name}/{key[1]}: command changed from allowlisted {entry["command"]!r}')
            if entry.get('local'):
                run = tasks.get(entry['local'], {}).get('run', [])
                run = [run] if isinstance(run, str) else run
                ci_tokens = shlex.split(command)
                prefixes = [shlex.split(local) for local in run]
                if not any(prefix and prefix == ci_tokens[:len(prefix)] for prefix in prefixes):
                    errors.append(f'ci.yml {job_name}/{key[1]}: local task {entry["local"]} does not run this command')
            elif not entry.get('reason'):
                errors.append(f'ci.yml {job_name}/{key[1]}: CI-only allowlist entry needs a reason')
    for key in sorted(set(allowed) - seen, key=str):
        errors.append(f'CI_ONLY_STEPS {key}: stale allowlist entry; remove it')


def execution_route(command, check):
    try:
        tokens = shlex.split(command, comments=True)
    except ValueError:
        return False
    if tokens[:3] != ['python3', 'scripts/validation/run.py', '--ci']:
        return False
    options = {}
    for index in range(3, len(tokens), 2):
        if index + 1 >= len(tokens) or tokens[index] not in ('--job', '--id', '--evidence-parent', '--filter'):
            return False
        if tokens[index] in options or tokens[index + 1].startswith('-'):
            return False
        options[tokens[index]] = tokens[index + 1]
    if '--job' in options and '--id' in options:
        return False
    selected = options.get('--job') == check['ci_job'] or options.get('--id') == check['id']
    if '{filter}' in check.get('ci', []) and '--filter' not in options:
        return False
    return selected


def workflow_routes(text, check):
    # Parse YAML so alternate indentation, flow mappings and step/job conditions
    # cannot turn a required check into an inventory-only or skipped route.
    for workflow in yaml.safe_load_all(text):
        if not isinstance(workflow, dict):
            continue
        job = workflow.get('jobs', {}).get(check['ci_job'], {})
        if 'if' in job or job.get('continue-on-error', False) is not False:
            continue
        for step in job.get('steps', []):
            if 'if' not in step and step.get('continue-on-error', False) is False:
                if execution_route(step.get('run', ''), check):
                    return True
    return False


def test_target(command):
    tokens = list(command)
    if tokens[:3] == ['nice', '-n', '10']:
        tokens = tokens[3:]
    return set(tokens[2:]) if tokens[:2] == ['dotnet', 'test'] else set()


def check_registry(root, files, mise, errors, registry=None, workflow_text=None, ci_workflow_text=None):
    if registry is None:
        registry = json.loads((root / 'scripts/validation/checks.json').read_text())
    checks = registry.get('checks', [])
    if ci_workflow_text is None:
        ci_path = root / '.github/workflows/ci.yml'
        if ci_path.is_file():
            ci_workflow_text = ci_path.read_text()
        else:
            errors.append('.github/workflows/ci.yml: missing; CI parity cannot be checked')
    if ci_workflow_text is not None:
        check_ci_parity(ci_workflow_text, checks, mise, errors)
    ids = [c.get('id') for c in checks]
    if registry.get('version') != 1 or len(ids) != len(set(ids)):
        errors.append('validation registry: unsupported version or duplicate check ID')
    by_id = {c['id']: c for c in checks}
    for id in ('backend.infrastructure', 'backend.integration'):
        if by_id.get(id, {}).get('container_environment') is not True:
            errors.append(f'{id}: local PostgreSQL checks require selected container environment')
    if workflow_text is None:
        workflow_text = '\n---\n'.join(p.read_text() for p in (root / '.github/workflows').glob('*.y*ml'))
    for check in checks:
        id = check['id']
        for mode in ('local', 'ci'):
            command = check.get(mode)
            if not isinstance(command, list) or not command or not all(isinstance(t, str) and t for t in command):
                errors.append(f'{id}: missing executable {mode} command')
        if not (root / check.get('cwd', '__missing__')).is_dir():
            errors.append(f'{id}: working directory missing')
        if check.get('required_ci'):
            if not workflow_routes(workflow_text, check):
                errors.append(f'{id}: required CI check has no executable workflow route')
    for id, expected_command in PROCESS.items():
        check = by_id.get(id, {})
        if ('backend' not in check.get('groups', []) or not check.get('required_ci')
                or check.get('ci_job') != 'backend'
                or any(check.get(mode) != expected_command for mode in ('local', 'ci'))):
            errors.append(f'{id}: process safety check must run locally and in required backend CI')
    for id, expected_command in CONSOLE.items():
        check = by_id.get(id, {})
        if (not check.get('required_ci') or check.get('ci_job') != 'console'
                or any(check.get(mode) != expected_command for mode in ('local', 'ci'))):
            errors.append(f'{id}: console analysis, theme checks and full tests must remain required')
    advisory_check = by_id.get('dependencies.source', {})
    if (not advisory_check.get('required_ci') or advisory_check.get('ci_job') != 'dependencies'
            or 'dependencies' not in advisory_check.get('groups', [])
            or any(advisory_check.get(mode) != DEPENDENCIES for mode in ('local', 'ci'))):
        errors.append('dependencies.source: source advisory gate must run locally and in required CI')
    tasks = mise.get('tasks', {})
    group_tasks = {'backend': 'test', 'integration': 'test:integration', 'mount-dev': 'test:mount-dev',
                   'dependencies': 'dependencies:check'}
    for group, task in group_tasks.items():
        run = tasks.get(task, {}).get('run', [])
        run = [run] if isinstance(run, str) else run
        route = ['python3', 'scripts/validation/run.py', '--group', group]
        if not any(shlex.split(command) == route for command in run):
            errors.append(f'tasks.{task}: missing executable registered {group} route')
    # Recognize all projects detected by the existing agent checker, including new ones.
    from xml.etree import ElementTree as ET
    for relative in files:
        if not (relative.startswith('tests/') and relative.endswith('.csproj')):
            continue
        project = ET.parse(root / relative)
        if not (any(n.text and n.text.strip().lower() == 'true' for n in project.iter('IsTestProject'))
                or any(n.get('Include') == 'Microsoft.NET.Test.Sdk' for n in project.iter('PackageReference'))):
            continue
        group = ('integration' if 'Romd.Hosting.IntegrationTests/' in relative else
                 'mount-dev' if 'Romd.Mount.Tests/' in relative else 'backend')
        candidates = [c for c in checks if group in c.get('groups', [])]
        if not any(relative in test_target(c.get('local', [])) for c in candidates):
            errors.append(f'{relative}: missing local {group} registry route')
        if not any(relative in test_target(c.get('ci', [])) and c.get('required_ci') for c in candidates):
            errors.append(f'{relative}: missing required CI registry route')
