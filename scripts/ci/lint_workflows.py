#!/usr/bin/env python3
"""Parse workflow security boundaries; actionlint checks expressions and shell."""
from pathlib import Path
import re
import sys
import yaml

ROOT = Path(__file__).resolve().parents[2]


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
            labels = [runner] if isinstance(runner, str) else runner if isinstance(runner, list) else []
            if any(isinstance(label, str) and label.endswith('-latest') for label in labels):
                errors.append(f'{name}/{job_name}: pin the runner image instead of a -latest label')
            permissions = job.get('permissions', {})
            if not isinstance(permissions, dict):
                errors.append(f'{name}/{job_name}: use an explicit permission map')
                continue
            for permission, value in permissions.items():
                allowed = {'publish': {'contents', 'packages'}, 'publish-metadata': {'id-token', 'attestations'}, 'publish-complete': {'contents'}}
                allowed_write = name == 'release.yml' and permission in allowed.get(job_name, set())
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
              + release_gate_errors(path.read_text(), path.name)]
    for error in errors:
        print(error, file=sys.stderr)
    return bool(errors)


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


if __name__ == '__main__':
    sys.exit(main())
