#!/usr/bin/env python3
"""Parse workflow security boundaries; actionlint checks expressions and shell."""
from pathlib import Path
import re
import sys
import yaml

ROOT = Path(__file__).resolve().parents[2]
NETWORK_SETUP = ('sudo apt-get update\n'
                 'sudo apt-get install --yes --no-install-recommends nginx\n'
                 'python3 scripts/ci/network_probes.py prepare "$RUNNER_TEMP/network-probes"\n')
NETWORK_VERIFY = "python3 scripts/ci/network_probes.py verify '${{ matrix.part }}' coverage/integration"
INTEGRATION_RUN = "python3 scripts/validation/run.py --ci --job integration --filter '${{ matrix.filter }}' --evidence-parent coverage/integration"


class UniqueLoader(yaml.SafeLoader):
    pass


def unique_map(loader, node, deep=False):
    result = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node, deep=deep)
        if key in result:
            raise ValueError('Duplicate workflow key')
        result[key] = loader.construct_object(value_node, deep=deep)
    return result


UniqueLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, unique_map)


def policy_errors(text, name):
    errors = []
    try:
        workflow = yaml.load(text, Loader=UniqueLoader)
        if not isinstance(workflow, dict):
            return [f'{name}: workflow must be a mapping']
        if workflow.get('permissions') != {'contents': 'read'}:
            errors.append(f'{name}: default permissions must be contents: read')
        # SafeLoader follows YAML1.1; GitHub's unquoted `on` becomes True.
        triggers = workflow.get('on', workflow.get(True, {}))
        if isinstance(triggers, str):
            triggers = [triggers]
        if 'pull_request_target' in triggers:
            errors.append(f'{name}: privileged contributor trigger')
        if name == 'dependencies.yml':
            if (not isinstance(triggers, dict) or not all(key in triggers for key in
                    ('pull_request', 'push', 'schedule', 'workflow_dispatch', 'workflow_call'))
                    or triggers.get('push', {}).get('branches') != ['main']
                    or triggers.get('schedule') != [{'cron': '23 6 * * *'}]):
                errors.append(f'{name}: source advisories must run for PRs, main, releases and the daily schedule')
            jobs = workflow.get('jobs', {})
            if set(jobs) != {'dependencies'}:
                errors.append(f'{name}: only the source advisory job may run on the schedule')
            else:
                job = jobs['dependencies']
                if ('if' in job or job.get('continue-on-error', False) is not False
                        or job.get('permissions', {'contents': 'read'}) != {'contents': 'read'}):
                    errors.append(f'{name}: source advisory job must run without elevated permissions')
                expected = 'python3 scripts/validation/run.py --ci --job dependencies --evidence-parent coverage/dependencies'
                if not any(step.get('run') == expected and 'if' not in step
                           and step.get('continue-on-error', False) is False for step in job.get('steps', [])):
                    errors.append(f'{name}: executable registered source advisory route missing')
        for job_name, job in workflow.get('jobs', {}).items():
            runner = job.get('runs-on', [])
            if 'uses' not in job and 'runs-on' in job and runner not in ('ubuntu-24.04', ['ubuntu-24.04']):
                errors.append(f'{name}/{job_name}: only the standard ubuntu-24.04 hosted runner is approved')
            if 'uses' in job and not job['uses'].startswith('./.github/workflows/'):
                errors.append(f'{name}/{job_name}: reusable workflows must use reviewed local runner policies')
            permissions = job.get('permissions', {})
            if not isinstance(permissions, dict):
                errors.append(f'{name}/{job_name}: use an explicit permission map')
                continue
            for permission, value in permissions.items():
                allowed = {'publish': {'contents', 'packages'}, 'publish-metadata': {'id-token', 'attestations'}, 'publish-complete': {'contents'}}
                allowed_write = name == 'release.yml' and permission in allowed.get(job_name, set())
                allowed_write |= name == 'security-maintenance.yml' and job_name == 'codeql' and permission == 'security-events'
                if value not in ('read', 'none') and not (value == 'write' and allowed_write):
                    errors.append(f'{name}/{job_name}: unexpected {permission}: {value}')
            if job.get('secrets') == 'inherit':
                errors.append(f'{name}/{job_name}: inherited secrets are prohibited')
            uses = [job] if 'uses' in job else job.get('steps', [])
            for step in uses:
                use = step.get('uses')
                if not use:
                    continue
                if not use.startswith('./') and not re.fullmatch(r'[^@]+@[0-9a-f]{40}', use):
                    errors.append(f'{name}: action must use immutable commit: {use}')
                if use.startswith('actions/checkout@') and step.get('with', {}).get('persist-credentials') is not False:
                    errors.append(f'{name}: checkout must disable persisted credentials')
    except (yaml.YAMLError, ValueError, TypeError, AttributeError) as error:
        errors.append(f'{name}: unsupported or invalid workflow: {type(error).__name__}')
    return errors


def main():
    errors = [error for path in sorted((ROOT / '.github/workflows').glob('*.y*ml'))
              for error in policy_errors(path.read_text(), path.name) + concurrency_errors(path.read_text(), path.name)
              + gate_errors(path.read_text(), path.name) + environment_errors(path.read_text(), path.name)
              + release_gate_errors(path.read_text(), path.name) + early_errors(path.read_text(), path.name)
              + maintenance_errors(path.read_text(), path.name) + network_probe_errors(path.read_text(), path.name)]
    for error in errors:
        print(error, file=sys.stderr)
    return bool(errors)


def network_probe_errors(text, name):
    if name != 'ci.yml':
        return []
    try:
        job = yaml.load(text, Loader=UniqueLoader)['jobs']['integration']
        if ('if' in job or job.get('continue-on-error', False) is not False
                or len(job['strategy']['matrix']['include']) != 2
                or {part['part'] for part in job['strategy']['matrix']['include']} != {'hosting', 'endpoints'}):
            return ['ci.yml/integration: both network-probe partitions must be required']
        commands = [step.get('run') for step in job['steps']
                    if 'if' not in step and step.get('continue-on-error', False) is False]
        required = [NETWORK_SETUP, INTEGRATION_RUN, NETWORK_VERIFY]
        if (not all(command in commands for command in required)
                or [commands.index(command) for command in required] != sorted(commands.index(command) for command in required)):
            return ['ci.yml/integration: prepare tools, run tests and verify executed network probes in that order']
        upload = next(step for step in job['steps'] if step.get('name') == 'Upload integration coverage and hang diagnostics')
        if '!coverage/integration/**/integration.trx' not in upload.get('with', {}).get('path', '').splitlines():
            return ['ci.yml/integration: retain the small network receipt, not raw TRX transcripts']
        return []
    except (yaml.YAMLError, KeyError, ValueError, TypeError, AttributeError, StopIteration):
        return ['ci.yml: invalid network-probe routing']


def concurrency_errors(text, name):
    try:
        concurrency = yaml.load(text, Loader=UniqueLoader).get('concurrency', {})
        if name in ('ci.yml', 'dependencies.yml'):
            prefix = 'ci' if name == 'ci.yml' else 'dependencies'
            expected = {'group': prefix + "-${{ github.event_name == 'pull_request' && github.event.pull_request.number || github.run_id }}",
                        'cancel-in-progress': "${{ github.event_name == 'pull_request' }}"}
            if concurrency != expected:
                return [f'{name}: cancellation must be restricted to superseded pull-request runs']
        if name == 'release.yml' and concurrency != {'group': 'release-${{ github.ref }}', 'cancel-in-progress': False}:
            return ['release.yml: release publication must not be cancelled by concurrency']
    except (yaml.YAMLError, ValueError, TypeError, AttributeError):
        return [f'{name}: invalid concurrency configuration']
    return []


GATE_CHECK = """jq -e 'all(.[]; .result == "success")' <<< "$NEEDS" > /dev/null"""


def gate_errors(text, name):
    """ci.yml's one required status check must fail unless every other job succeeded."""
    if name != 'ci.yml':
        return []
    try:
        jobs = yaml.load(text, Loader=UniqueLoader).get('jobs', {})
        gate = jobs.get('ci-gate')
        if not isinstance(gate, dict):
            return ['ci.yml: ci-gate job is required for branch protection']
        errors = []
        if gate.get('name') != 'CI gate':
            errors.append('ci.yml/ci-gate: preserve the required CI gate check name')
        needs = gate.get('needs', [])
        if gate.get('if') != '${{ always() }}' or gate.get('continue-on-error', False) is not False:
            errors.append('ci.yml/ci-gate: must run with if: ${{ always() }} and without continue-on-error')
        if set([needs] if isinstance(needs, str) else needs) != set(jobs) - {'ci-gate'}:
            errors.append('ci.yml/ci-gate: needs must list every other CI job')
        if not any(step.get('env', {}).get('NEEDS') == '${{ toJSON(needs) }}' and GATE_CHECK in step.get('run', '')
                   and 'if' not in step and step.get('continue-on-error', False) is False
                   for step in gate.get('steps', [])):
            errors.append('ci.yml/ci-gate: missing the all-success check over toJSON(needs)')
        return errors
    except (yaml.YAMLError, ValueError, TypeError, AttributeError):
        return ['ci.yml: invalid ci-gate configuration']


def environment_errors(text, name):
    """The release publisher waits for the protected release Environment before writing anything."""
    if name != 'release.yml':
        return []
    try:
        environment = yaml.load(text, Loader=UniqueLoader).get('jobs', {}).get('publish', {}).get('environment')
        if environment not in ('release', {'name': 'release'}):
            return ['release.yml/publish: must use the protected release environment']
    except (yaml.YAMLError, ValueError, TypeError, AttributeError):
        return ['release.yml: invalid publish environment']
    return []


def release_gate_errors(text, name):
    """Publication requires successful CI and fresh advisories from the tagged source."""
    if name != 'release.yml':
        return []
    try:
        jobs = yaml.load(text, Loader=UniqueLoader).get('jobs', {})
        for job, workflow in (('verify', 'ci.yml'), ('advisories', 'dependencies.yml')):
            if jobs.get(job) != {'needs': 'preflight', 'uses': './.github/workflows/' + workflow}:
                return [f'release.yml/{job}: must call the current revision workflow after preflight without bypasses']
        publish = jobs.get('publish', {})
        if (not {'preflight', 'verify', 'advisories'} <= set(publish.get('needs', []))
                or 'if' in publish or publish.get('continue-on-error', False) is not False):
            return ['release.yml/publish: must require successful preflight, CI and source advisories']
    except (yaml.YAMLError, ValueError, TypeError, AttributeError):
        return ['release.yml: invalid publication gate']
    return []


CHD_CONDITION = "${{ needs.source-checks.outputs.chd == 'true' }}"
CHD_STEPS = {'Build images and exercise production deployment', 'Reclaim disposable CI build cache',
             'Synthetic PS2 DVD CHD end to end'}


def early_errors(text, name):
    if name != 'ci.yml':
        return []
    try:
        jobs = yaml.load(text, Loader=UniqueLoader)['jobs']
        source = jobs['source-checks']
        errors = []
        if 'if' in source or source.get('continue-on-error', False) is not False:
            errors.append('ci.yml/source-checks: early checks cannot be optional')
        if source.get('outputs') != {'chd': '${{ steps.scope.outputs.chd }}'}:
            errors.append('ci.yml/source-checks: missing exact CHD selection output')
        commands = {'python3 scripts/ci/change_scope.py',
                    'python3 scripts/ci/secret_scan.py --gitleaks "$RUNNER_TEMP/gitleaks/gitleaks"',
                    'python3 scripts/ci/nuget_locks.py',
                    'python3 scripts/validation/run.py --ci --job source-checks --evidence-parent coverage/source-checks'}
        present = {step.get('run') for step in source.get('steps', [])
                   if 'if' not in step and step.get('continue-on-error', False) is False}
        if not commands <= present:
            errors.append('ci.yml/source-checks: scope, secret, complete lock and registered style checks must execute')
        if not any(step.get('id') == 'scope' and step.get('run') == 'python3 scripts/ci/change_scope.py'
                   for step in source.get('steps', [])):
            errors.append('ci.yml/source-checks: CHD output must come from the tested scope selector')
        for job_name in ('console', 'native', 'backend', 'infrastructure', 'integration', 'web', 'production-compose'):
            needs = jobs[job_name].get('needs', [])
            if 'source-checks' not in ([needs] if isinstance(needs, str) else needs):
                errors.append(f'ci.yml/{job_name}: expensive work must wait for early checks')
        steps = {step.get('name'): step for step in jobs['production-compose']['steps']}
        if any(steps.get(label, {}).get('if') != CHD_CONDITION
               or steps[label].get('continue-on-error', False) is not False for label in CHD_STEPS):
            errors.append('ci.yml/production-compose: expensive drill steps must share the exact tested scope condition')
        return errors
    except (yaml.YAMLError, KeyError, ValueError, TypeError, AttributeError):
        return ['ci.yml: invalid early verification or CHD routing']


def maintenance_errors(text, name):
    if name != 'security-maintenance.yml':
        return []
    try:
        workflow = yaml.load(text, Loader=UniqueLoader)
        triggers = workflow.get('on', workflow.get(True, {}))
        if (set(triggers) != {'schedule', 'workflow_dispatch'}
                or triggers['schedule'] != [{'cron': '47 6 * * 0'}]):
            return ['security-maintenance.yml: heavy security work is weekly/manual only']
        if set(workflow['jobs']) != {'secrets', 'codeql', 'native-fuzz'}:
            return ['security-maintenance.yml: unexpected maintenance jobs']
        codeql = workflow['jobs']['codeql']
        if (codeql.get('permissions') != {'contents': 'read', 'security-events': 'write'}
                or codeql['strategy']['matrix']['language'] != ['csharp', 'javascript-typescript', 'python', 'actions']):
            return ['security-maintenance.yml: preserve the explicit CodeQL scope and limited SARIF permission']
        if any(job.get('continue-on-error', False) is not False for job in workflow['jobs'].values()):
            return ['security-maintenance.yml: maintenance failures must remain visible']
        build = ('dotnet clean Romd.sln -c Release -m:1\n'
                 'dotnet build Romd.sln -c Release -m:1 -p:SkipMissingFrontendWarning=true\n')
        if not any(step.get('run') == build and step.get('if') == "${{ matrix.language == 'csharp' }}"
                   and step.get('continue-on-error', False) is False for step in codeql['steps']):
            return ['security-maintenance.yml: C# must clean once before the complete serial solution build']
        def runs(job, command):
            return any(step.get('run') == command and 'if' not in step
                       and step.get('continue-on-error', False) is False for step in job['steps'])
        if not runs(workflow['jobs']['secrets'], 'python3 scripts/ci/secret_scan.py --full'):
            return ['security-maintenance.yml: full-history secret scan must execute']
        native = workflow['jobs']['native-fuzz']
        seconds = "${{ github.event_name == 'schedule' && '1800' || inputs.fuzz_seconds || '60' }}"
        if (not runs(native, 'scripts/native/build.sh test-inputs')
                or not runs(native, 'scripts/native/fuzz.sh gate "$ROMD_FUZZ_SECONDS"')
                or native['env']['ROMD_FUZZ_SECONDS'] != seconds
                or triggers['workflow_dispatch']['inputs']['fuzz_seconds']['options'] != ['60', '1800']):
            return ['security-maintenance.yml: retain pinned inputs and the weekly full/manual smoke fuzz gate']
        for action in ('init', 'analyze'):
            if not any(step.get('uses', '').startswith('github/codeql-action/' + action + '@')
                       and 'if' not in step and step.get('continue-on-error', False) is False
                       for step in codeql['steps']):
                return ['security-maintenance.yml: CodeQL initialization and analysis must execute']
    except (yaml.YAMLError, KeyError, ValueError, TypeError, AttributeError):
        return ['security-maintenance.yml: invalid maintenance policy']
    return []


if __name__ == '__main__':
    sys.exit(main())
