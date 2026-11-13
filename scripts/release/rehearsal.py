#!/usr/bin/env python3
"""Rehearse a source-free package using already-built images and a loopback registry.

Creates no public registry tags, GitHub releases or assets. Cleanup is limited to
the uniquely named registry container, volume and image aliases this invocation owns.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

from preflight import valid_tag
from qualify import clean_environment
from rehearsal_evidence import ROLES, verify_rehearsal
from suite import digest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts/deployment'))
from container_lifecycle import bounded_command


class Commands:
    def __init__(self, work, output, env):
        self.work, self.output, self.env, self.records = work, output, env, []

    def run(self, command, stage, *, env=None, timeout=600):
        code, data, reason = bounded_command(command, env or self.env, timeout=timeout)
        self.records.append(dict(stage=stage, command=[str(part).replace(str(self.work), '${WORK}')
            .replace(str(self.output), '${OUTPUT}') for part in command], exitCode=code,
            outputSha256=hashlib.sha256(data).hexdigest()))
        if code or reason:
            raise RuntimeError(f'{stage} failed; private command output is not published')
        return data.decode()


def candidate_images(command, revision, tag):
    images = {}
    for role in ROLES:
        local = json.loads(command(['docker', 'image', 'inspect', f'romd-{role}:test'], 'inspect-' + role))[0]
        labels = local.get('Config', {}).get('Labels', {}) or {}
        if ((local.get('Os'), local.get('Architecture')) != ('linux', 'amd64')
                or labels.get('org.opencontainers.image.revision') != revision
                or labels.get('org.opencontainers.image.version') != tag
                or not re.fullmatch('sha256:[0-9a-f]{64}', local.get('Id', ''))):
            raise ValueError('Rehearsal requires Linux amd64 images labelled with this source and version')
        images[role] = local['Id']
    return images


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, *args):
        return None


def local_json(url):
    if not re.fullmatch(r'http://127\.0\.0\.1:[0-9]+/v2/[^\s]*', url):
        raise ValueError('Registry requests must remain on loopback')
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    request = urllib.request.Request(url, headers={'Accept': 'application/vnd.oci.image.index.v1+json, '
        'application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.manifest.v1+json, '
        'application/vnd.docker.distribution.manifest.v2+json'})
    with opener.open(request, timeout=5) as response:
        data = response.read(2 * 1024 * 1024 + 1)
        if len(data) > 2 * 1024 * 1024:
            raise ValueError('Registry response exceeded its bound')
        return json.loads(data), 'sha256:' + hashlib.sha256(data).hexdigest(), response.headers.get('Docker-Content-Digest')


def cleanup_registry(command, container, volume, aliases, session, diagnostics=None):
    """Attempt every owned cleanup even if another cleanup or the drill failed."""
    errors = []
    def attempt(stage, action):
        try:
            action()
        except (ValueError, RuntimeError, OSError, KeyError, TypeError, IndexError) as error:
            errors.append(dict(stage=stage, error=type(error).__name__))

    def remove_container():
        state = json.loads(command(['docker', 'inspect', container], 'registry-owner'))[0]
        if state.get('Config', {}).get('Labels', {}).get('io.romd.rehearsal') != session:
            raise ValueError('Registry ownership changed; cleanup refused')
        command(['docker', 'stop', container], 'stop-registry')
        state = json.loads(command(['docker', 'inspect', container], 'registry-exit'))[0]['State']
        if diagnostics is not None:
            diagnostics['registryExit'] = dict(exitCode=state.get('ExitCode'), oomKilled=state.get('OOMKilled'))
        command(['docker', 'rm', '--volumes', container], 'remove-registry')
        if state.get('ExitCode') != 0 or state.get('OOMKilled'):
            raise ValueError('Disposable registry did not exit cleanly')

    def remove_volume():
        state = json.loads(command(['docker', 'volume', 'inspect', volume], 'registry-data-owner'))[0]
        if state.get('Labels', {}).get('io.romd.rehearsal') != session:
            raise ValueError('Registry data ownership changed; cleanup refused')
        command(['docker', 'volume', 'rm', volume], 'remove-registry-data')

    if container:
        attempt('registry', remove_container)
    for alias in reversed(aliases):
        attempt('image-alias', lambda current=alias: command(['docker', 'image', 'rm', current], 'remove-owned-alias'))
    if volume:
        attempt('registry-volume', remove_volume)
    return errors


def rehearse(output, revision, tag):
    if not re.fullmatch('[0-9a-f]{40}', revision) or not valid_tag(tag):
        raise ValueError('Rehearsal requires a full revision and strict version')
    output.mkdir(parents=True, mode=0o700, exist_ok=False)
    receipt = dict(schemaVersion=1, result='failed', evidenceType='disposable-rehearsal',
                   sourceCommit=revision, releaseTag=tag, platform='linux/amd64')
    pins = json.loads((ROOT / 'scripts/deployment/base-images.json').read_text())
    tool = json.loads((ROOT / 'scripts/release/rehearsal.json').read_text())
    if tool.get('schemaVersion') != 1 or not re.fullmatch(r'registry:[0-9.]+@sha256:[0-9a-f]{64}', tool.get('registry', '')):
        raise ValueError('Rehearsal registry tool must be an immutable official image')
    command = None
    with tempfile.TemporaryDirectory(prefix='romd-rehearsal-') as temporary:
        work = Path(temporary)
        env = clean_environment()
        for key in ('GH_TOKEN', 'GITHUB_TOKEN', 'ACTIONS_RUNTIME_TOKEN', 'DOCKER_AUTH_CONFIG'):
            env.pop(key, None)
        env.pop('DOCKER_CONTEXT', None)
        if env.get('DOCKER_HOST') and not env['DOCKER_HOST'].startswith('unix://'):
            raise ValueError('Loopback rehearsal requires a local Docker daemon')
        (work / 'docker').mkdir(mode=0o700)
        env['DOCKER_CONFIG'] = str(work / 'docker')
        env['REGISTRY_HTTP_SECRET'] = secrets.token_hex(32)
        command = Commands(work, output, env)
        session = secrets.token_hex(8)
        name = 'romd-rehearsal-' + session
        container = None
        volume = None
        aliases = []
        try:
            server = json.loads(command.run(['docker', 'version', '--format', '{{json .Server}}'], 'native-docker'))
            if ((server.get('Os'), server.get('Arch')) != ('linux', 'amd64')
                    or not any(item.get('Name') == 'Engine' for item in server.get('Components', []))):
                raise ValueError('Packaged rehearsal requires a native Linux amd64 Docker Engine')
            images = candidate_images(command.run, revision, tag)
            receipt.update(imageIds=images, dependencies=pins)
            command.run(['docker', 'pull', tool['registry']], 'pull-registry-tool')
            volume = name + '-registry'
            command.run(['docker', 'volume', 'create', '--label', 'io.romd.rehearsal=' + session, volume], 'create-registry-data')
            container = command.run(['docker', 'create', '--name', name, '--label', 'io.romd.rehearsal=' + session,
                '--publish', '127.0.0.1::5000',
                '--mount', f'type=volume,source={volume},target=/var/lib/registry',
                '--env', 'REGISTRY_HTTP_SECRET', '--env', 'OTEL_TRACES_EXPORTER=none',
                # Distribution installs its SIGTERM handler only when drain is enabled.
                '--env', 'REGISTRY_HTTP_DRAINTIMEOUT=5s', tool['registry']], 'create-registry').strip()
            if not re.fullmatch('[0-9a-f]{64}', container):
                raise ValueError('Registry create did not return an owned container ID')
            command.run(['docker', 'start', container], 'start-registry')
            state = json.loads(command.run(['docker', 'inspect', container], 'registry-state'))[0]
            ports = state['NetworkSettings']['Ports']['5000/tcp']
            if len(ports) != 1 or ports[0]['HostIp'] != '127.0.0.1' or not ports[0]['HostPort'].isdigit():
                raise ValueError('Rehearsal registry must bind only a fresh loopback port')
            authority = '127.0.0.1:' + ports[0]['HostPort']
            deadline = time.monotonic() + 30
            while True:
                try:
                    local_json(f'http://{authority}/v2/')
                    break
                except (OSError, urllib.error.URLError):
                    if time.monotonic() >= deadline:
                        raise RuntimeError('Disposable registry did not become ready') from None
                    time.sleep(.1)
            references = {}
            for role, identifier in images.items():
                repository = name + '/romd-' + role
                alias = f'{authority}/{repository}:rehearsal'
                command.run(['docker', 'tag', identifier, alias], 'stage-' + role)
                aliases.append(alias)
                command.run(['docker', 'push', alias], 'stage-digest-' + role)
                _, actual, announced = local_json(f'http://{authority}/v2/{repository}/manifests/rehearsal')
                if actual != announced:
                    raise ValueError('Disposable registry manifest digest mismatch')
                reference = f'{authority}/{repository}@{actual}'
                references[f'ROMD_{role.upper()}_IMAGE'] = reference
            references['ROMD_POSTGRES_IMAGE'] = pins['postgres']
            image_file = output / 'images.env'
            image_file.write_text(''.join(f'{key}={value}\n' for key, value in references.items()))
            bundle = output / 'rehearsal-bundle.tar.gz'
            command.run([ROOT / 'scripts/deployment/bundle.sh', image_file, bundle, revision, tag], 'package-rehearsal')
            state_file = output / 'candidate.json'
            state_file.write_text(json.dumps(dict(sourceCommit=revision, releaseTag=tag,
                bundleSha256=digest(bundle), images=references), indent=2) + '\n')
            command.run([sys.executable, ROOT / 'scripts/release/suite.py', '--bundle', bundle,
                '--checksum', str(bundle) + '.sha256', '--expected-images', image_file,
                '--publisher-state', state_file, '--expected-revision', revision, '--expected-tag', tag,
                '--nginx-image', pins['ingress'], '--rehearsal', '--evidence-dir', output / 'packaged'],
                'packaged-positive-and-negative-suite', timeout=2400)
            positive = json.loads((output / 'packaged/positive/qualification.json').read_text())
            receipt.update(images=references, bundleSha256=digest(bundle), suiteSha256=digest(output / 'packaged/suite.json'),
                payloadFiles={key: value for key, value in positive['bundle']['files'].items() if key != 'images.env'})
            receipt['result'] = 'passed'
        except (ValueError, RuntimeError, OSError, KeyError, TypeError, IndexError) as error:
            receipt['failure'] = str(error) if type(error) in (ValueError, RuntimeError) else type(error).__name__
        finally:
            cleanup_errors = cleanup_registry(command.run, container, volume, aliases, session, receipt)
            try:
                if receipt.get('imageIds'):
                    receipt['finalImageIds'] = candidate_images(command.run, revision, tag)
                    if receipt['finalImageIds'] != receipt['imageIds']:
                        raise ValueError('Built candidate images changed during rehearsal')
            except (ValueError, RuntimeError, OSError, KeyError, TypeError, IndexError) as error:
                cleanup_errors.append(dict(stage='candidate-images', error=type(error).__name__))
            if cleanup_errors:
                receipt.update(result='failed', cleanupFailures=cleanup_errors)
            else:
                receipt['registryCleanup'] = 'passed'
    receipt['commands'] = command.records if command else []
    path = output / 'receipt.json'
    path.write_text(json.dumps(receipt, indent=2) + '\n')
    if receipt['result'] == 'passed':
        try:
            verify_rehearsal(path, revision, tag, receipt['imageIds'], pins)
        except (ValueError, RuntimeError, OSError, KeyError, TypeError) as error:
            receipt.update(result='failed', failure=str(error) if type(error) is ValueError else type(error).__name__)
            path.write_text(json.dumps(receipt, indent=2) + '\n')
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision')
    parser.add_argument('--tag')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    revision = args.revision or subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    tag = args.tag or 'v0.0.0-rehearsal.r' + revision[:12]
    receipt = rehearse(args.output.resolve(), revision, tag)
    print(f'Packaged rehearsal: {receipt["result"]}; evidence: {args.output}')
    return 0 if receipt['result'] == 'passed' else 1


if __name__ == '__main__':
    raise SystemExit(main())
