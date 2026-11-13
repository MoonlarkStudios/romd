#!/usr/bin/env python3
"""Qualify a downloaded, immutable Linux amd64 deployment on a disposable runner.

This runner never reads a checkout's .env, rebuilds images, or promotes a release.
The browser tooling stays on the qualification runner, outside the install bundle.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import secrets
import subprocess
import sys
import tarfile
import tempfile
import time


IMAGE_KEYS = {f'ROMD_{name}_IMAGE' for name in ('ADMIN', 'CONSUMER', 'WORKER', 'PLAYER', 'POSTGRES')}
DIGEST_REF = re.compile(r'[a-z0-9][a-z0-9./:_-]*@sha256:[0-9a-f]{64}')
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'deployment'))
from container_lifecycle import ContainerLifecycle, HOSTS

MAX_EXPANDED_BYTES = 32 * 1024 * 1024


def canonical_digest(reference):
    name, digest = reference.rsplit('@', 1)
    if ':' in name.rsplit('/', 1)[-1]:
        name = name.rsplit(':', 1)[0]
    first = name.split('/', 1)[0]
    if '.' not in first and ':' not in first and first != 'localhost':
        name = 'docker.io/' + name
    if name.startswith('docker.io/') and name.count('/') == 1:
        name = 'docker.io/library/' + name.split('/', 1)[1]
    return name + '@' + digest


def image_map(path):
    entries = {}
    for line in Path(path).read_text().splitlines():
        if not line or line.startswith('#'):
            continue
        key, separator, value = line.partition('=')
        if not separator or key not in IMAGE_KEYS or key in entries or not DIGEST_REF.fullmatch(value):
            raise ValueError('Image map must contain each required key once, with an immutable digest reference')
        entries[key] = value
    if set(entries) != IMAGE_KEYS:
        raise ValueError('Image map must contain all five release images')
    return entries


def member_path(name):
    path = PurePosixPath(name)
    if path.is_absolute() or '..' in path.parts or '\\' in name:
        raise ValueError('Unsafe archive member path')
    return path.as_posix().removeprefix('./')


def verify_bundle(bundle, checksum, expected_images, destination, revision, tag):
    """Verify before extraction; refuse links, duplicate paths, extra files and mixed releases."""
    checksum_fields = Path(checksum).read_text().strip().split()
    if (len(checksum_fields) != 2 or not re.fullmatch('[0-9a-f]{64}', checksum_fields[0])
            or checksum_fields[1].removeprefix('*') != Path(bundle).name):
        raise ValueError('Checksum must name exactly the downloaded bundle')
    with Path(bundle).open('rb') as source:
        digest = hashlib.file_digest(source, 'sha256').hexdigest()
    if digest != checksum_fields[0]:
        raise ValueError('Downloaded bundle checksum mismatch')
    expected = image_map(expected_images)
    files = {}
    total = 0
    with tarfile.open(bundle, 'r:gz') as archive:
        for index, member in enumerate(archive):
            if index >= 1024:
                raise ValueError('Deployment bundle contains too many archive members')
            name = member_path(member.name)
            if member.isdir():
                continue
            if not member.isfile() or name in files or name in ('', '.'):
                raise ValueError('Archive contains a link, special file or duplicate path')
            total += member.size
            if len(files) >= 512 or total > MAX_EXPANDED_BYTES:
                raise ValueError('Deployment bundle exceeds qualification size limits')
            files[name] = (member, archive.extractfile(member).read())
        if 'manifest.json' not in files:
            raise ValueError('Deployment manifest is required')
        manifest = json.loads(files['manifest.json'][1])
        if not isinstance(manifest, dict):
            raise ValueError('Deployment manifest must be an object')
        if (manifest.get('schemaVersion') != 1 or manifest.get('sourceCommit') != revision
                or manifest.get('releaseTag') != tag or manifest.get('platform') != 'linux/amd64'):
            raise ValueError('Manifest revision, tag, schema or supported platform mismatch')
        hashes = {name: hashlib.sha256(data).hexdigest()
                  for name, (_, data) in files.items() if name != 'manifest.json'}
        if manifest.get('files') != hashes or manifest.get('images') != expected:
            raise ValueError('Manifest file inventory, hashes or published image map mismatch')
        required = {'compose.yaml', 'images.env', '.env.example', 'scripts/deployment/compose.sh',
                    'scripts/deployment/validate.sh', 'scripts/deployment/verify.sh',
                    'scripts/backup/backup.sh', 'scripts/backup/restore.sh',
                    'deploy/examples/https/compose.yaml'}
        if not required <= set(files):
            raise ValueError('Deployment bundle lacks required installation/recovery files')
        if any(name.startswith(('.git/', '.agents/', '.codex/', '.github/', 'src/'))
               or name in ('.env', '.git') for name in files):
            raise ValueError('Deployment bundle includes source or installation secrets')
        destination.mkdir(mode=0o700)
        for name, (member, data) in files.items():
            target = destination / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            target.chmod(0o755 if member.mode & 0o111 else 0o644)
        # These are public bundle assets bind-mounted into non-root containers.
        # Keep the containing workspace private while making included directories
        # traversable even when the qualifier's caller uses umask 077.
        for directory in destination.rglob('*'):
            if directory.is_dir():
                directory.chmod(0o755)
    if image_map(destination / 'images.env') != expected:
        raise ValueError('Bundle image map differs from published digests')
    return {'sha256': digest, 'sourceCommit': revision, 'releaseTag': tag,
            'platform': 'linux/amd64', 'images': expected, 'fileCount': len(files)}


def verify_publisher(bundle, state_path, expected_images, revision, tag):
    state = json.loads(Path(state_path).read_text())
    with Path(bundle).open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    if (state.get('sourceCommit') != revision or state.get('releaseTag') != tag
            or state.get('bundleSha256') != digest or state.get('images') != image_map(expected_images)):
        raise ValueError('Downloaded bundle differs from the independent publisher subject map')


def clean_environment():
    # Do not inherit operator Compose overrides, .env selection, profiles, or secrets.
    return {key: value for key, value in os.environ.items()
            if not key.startswith(('ROMD_', 'COMPOSE_', 'IGDB_', 'CLOUDFLARE_'))}


def deployment_environment(work, bundle, nginx_image):
    env = clean_environment()
    for role in ('BOOTSTRAP', 'PROVISIONER', 'WORKER', 'ADMIN', 'CONSUMER'):
        env[f'ROMD_POSTGRES_{role}_PASSWORD'] = secrets.token_hex(32)
    for key in ('ROMD_JWT_SECRET', 'ROMD_CONSUMER_DELIVERY_SIGNING_SECRET'):
        env[key] = secrets.token_hex(32)
    env.update(ROMD_DEFAULT_ADMIN_EMAIL='browser-qualification@example.test',
               ROMD_DEFAULT_ADMIN_PASSWORD='Q-' + secrets.token_hex(20) + '!aA9',
               ROMD_ADMIN_HOST='admin.example.test', ROMD_CONSUMER_HOST='console.example.test',
               ROMD_PLAYER_HOST='play.example.test', ROMD_TLS_DIRECTORY=str(work),
               ROMD_NGINX_IMAGE=nginx_image, ROMD_ENV_FILE=str(work / 'empty.env'),
               ROMD_TRUSTED_PROXY='172.30.0.2', ROMD_EDGE_SUBNET='172.30.0.0/24',
               ROMD_EDGE_DYNAMIC_RANGE='172.30.0.128/25', ROMD_ADMIN_EDGE_IP='172.30.0.3',
               ROMD_CONSUMER_EDGE_IP='172.30.0.4', ROMD_PLAYER_EDGE_IP='172.30.0.5',
               ROMD_HTTPS_PORT='443', ROMD_HTTPS_BIND=f'127.{secrets.randbelow(200) + 20}.{secrets.randbelow(254) + 1}.{secrets.randbelow(254) + 1}',
               COMPOSE_PROJECT_NAME='romd-qualify-' + secrets.token_hex(6),
               COMPOSE_FILE=f'{bundle}/compose.yaml:{bundle}/deploy/examples/https/compose.yaml')
    for surface, host in (('ADMIN', 'admin'), ('CONSUMER', 'console'), ('PLAYER', 'play')):
        env[f'ROMD_{surface}_PUBLIC_URL'] = f'https://{host}.example.test'
    (work / 'empty.env').touch(mode=0o600)
    return env


class Runner:
    def __init__(self, work, evidence, bundle, env):
        self.work, self.evidence, self.bundle, self.env = work, evidence, bundle, env
        self.commands = []
        self.persisted_state = None

    def run(self, command, stage, timeout=600, env=None, input_bytes=None, binary_result=False):
        # Keep arbitrary subprocess output (which can include connection strings) private.
        # Evidence retains exact redacted argv and output digests, never raw logs.
        started = time.monotonic()
        record = {'stage': stage, 'command': [str(part).replace(str(self.work), '${WORK}').replace(str(HERE), '${QUALIFICATION_TOOLS}')
                                            for part in command], 'cwd': '${BUNDLE_ROOT}',
                  'environment': 'disposable-release/linux-amd64'}
        self.commands.append(record)
        try:
            result = subprocess.run([str(part) for part in command], cwd=self.bundle, env=env or self.env,
                                    capture_output=True, timeout=timeout, check=False, input=input_bytes)
            record.update(exitCode=result.returncode, seconds=round(time.monotonic() - started, 3),
                          stdoutSha256=hashlib.sha256(result.stdout).hexdigest(),
                          stderrSha256=hashlib.sha256(result.stderr).hexdigest())
            if result.returncode:
                raise RuntimeError(f'{stage} failed (exit {result.returncode}); sanitized evidence retained')
            return result.stdout if binary_result else result.stdout.decode()
        except (OSError, subprocess.TimeoutExpired) as error:
            record.update(error=type(error).__name__, seconds=round(time.monotonic() - started, 3))
            raise RuntimeError(f'{stage} could not complete; sanitized evidence retained') from None

    def compose(self, *args, stage, env=None):
        return self.run([self.bundle / 'scripts/deployment/compose.sh', *args], stage, env=env)

    def inspect_images(self, images, env=None):
        running = {}
        for key, reference in images.items():
            service = {'ADMIN': 'romd-admin', 'CONSUMER': 'romd-consumer', 'WORKER': 'romd-worker',
                       'PLAYER': 'romd-player', 'POSTGRES': 'postgres'}[key[5:-6]]
            image = json.loads(self.run(['docker', 'image', 'inspect', reference], f'image-{service}', env=env))[0]
            if ((image['Os'], image['Architecture']) != ('linux', 'amd64')
                    or canonical_digest(reference) not in [canonical_digest(value) for value in image.get('RepoDigests', [])]):
                raise RuntimeError('Pulled image architecture or repository digest mismatch')
            container = self.compose('ps', '-q', service, stage=f'container-id-{service}', env=env).strip()
            actual = json.loads(self.run(['docker', 'inspect', container], f'container-image-{service}', env=env))[0]
            if actual['Image'] != image['Id'] or actual['Config']['Image'] != reference:
                raise RuntimeError('Running container does not use the published image digest')
            running[service] = {'reference': reference, 'imageId': image['Id'], 'platform': 'linux/amd64'}
        return running

    def browser(self, stage, inject_fault):
        config = self.work / 'browser-input.json'
        config.write_text(json.dumps({'adminOrigin': self.env['ROMD_ADMIN_PUBLIC_URL'],
                                     'consumerOrigin': self.env['ROMD_CONSUMER_PUBLIC_URL'],
                                     'playerOrigin': self.env['ROMD_PLAYER_PUBLIC_URL'],
                                     'email': self.env['ROMD_DEFAULT_ADMIN_EMAIL'],
                                     'password': self.env['ROMD_DEFAULT_ADMIN_PASSWORD'],
                                     'hostAddress': self.env['ROMD_HTTPS_BIND'],
                                     'certificateSpki': self.certificate_spki,
                                     'allowProvision': stage == 'browser-initial',
                                     'expectedState': self.persisted_state,
                                     'evidenceDir': str(self.evidence / stage),
                                     'injectFault': inject_fault}))
        config.chmod(0o600)
        self.run(['node', HERE / 'browser/run.mjs', '--config', config], stage, timeout=1200)
        browser = json.loads((self.evidence / stage / 'browser-diagnostics.json').read_text())
        state = browser.get('persistedState')
        keys = ('serverInstanceSha256', 'adminUserSha256', 'librarySha256', 'titleSha256',
                'releaseSha256', 'contentSha256')
        if (browser.get('result') != 'passed' or not isinstance(state, dict)
                or any(not re.fullmatch('[0-9a-f]{64}', state.get(key, '')) for key in keys)):
            raise RuntimeError('Browser did not prove persistent identity and synthetic content')
        if self.persisted_state is not None and state != self.persisted_state:
            raise RuntimeError('Restart/restore changed persistent qualification identity')
        self.persisted_state = state


def qualify(args, evidence):
    report = {'schemaVersion': 1, 'result': 'failed', 'evidenceType': 'disposable-release',
              'injectedFault': args.inject_fault, 'commands': []}
    try:
        verify_publisher(args.bundle, args.publisher_state, args.expected_images, args.expected_revision, args.expected_tag)
        with tempfile.TemporaryDirectory(prefix='romd-packaged-qualification-') as temporary:
            work = Path(temporary)
            bundle = work / 'bundle'
            report['bundle'] = verify_bundle(args.bundle, args.checksum, args.expected_images, bundle,
                                             args.expected_revision, args.expected_tag)
            if args.verify_only:
                report.update(result='verified-bundle-only', evidenceType='local')
                return report
            env = deployment_environment(work, bundle, args.nginx_image)
            runner = Runner(work, evidence, bundle, env)
            report['commands'] = runner.commands
            server = json.loads(runner.run(['docker', 'version', '--format', '{{json .Server}}'], 'docker-version'))
            if (server.get('Os'), server.get('Arch')) != ('linux', 'amd64'):
                raise RuntimeError('Qualification requires a native Linux amd64 Docker daemon')
            report['docker'] = {key: server.get(key) for key in ('Version', 'Os', 'Arch')}
            runner.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                        '-keyout', work / 'privkey.pem', '-out', work / 'fullchain.pem', '-days', '1',
                        '-subj', '/CN=ROMD disposable qualification', '-addext',
                        'subjectAltName=DNS:admin.example.test,DNS:console.example.test,DNS:play.example.test'],
                       'fixture-certificate')
            public_key = runner.run(['openssl', 'x509', '-in', work / 'fullchain.pem', '-pubkey', '-noout'], 'certificate-public-key')
            # DER bytes need a binary subprocess boundary, not text decoding.
            spki = runner.run(['openssl', 'pkey', '-pubin', '-outform', 'DER'], 'certificate-spki',
                              input_bytes=public_key.encode(), binary_result=True)
            runner.certificate_spki = base64.b64encode(hashlib.sha256(spki).digest()).decode()
            restore_env = dict(env, COMPOSE_PROJECT_NAME=env['COMPOSE_PROJECT_NAME'] + '-restore')
            gate = ContainerLifecycle(env, (env['COMPOSE_PROJECT_NAME'], restore_env['COMPOSE_PROJECT_NAME']),
                                      args.nginx_image, evidence / 'lifecycle.json')
            cleanups = []
            for selected, role in ((restore_env, 'restored'), (env, 'disposable')):
                cleanups.append((selected['COMPOSE_PROJECT_NAME'],
                    lambda current=selected, name=role: runner.compose('stop', stage='cleanup-' + name + '-stop', env=current),
                    lambda current=selected, name=role: runner.compose('down', '--volumes', '--remove-orphans',
                        stage='cleanup-' + name + '-project', env=current)))
            with gate.monitor(cleanups):
                runner.run([bundle / 'scripts/deployment/validate.sh'], 'bundle-compose-validation')
                runner.compose('pull', stage='pull-published-digests')
                runner.compose('up', '-d', '--wait', '--wait-timeout', '300', 'postgres', 'romd-worker',
                               stage='worker-first-provisioning')
                runner.compose('up', '-d', '--wait', '--wait-timeout', '300', stage='packaged-startup')
                gate.checkpoint(env['COMPOSE_PROJECT_NAME'])
                report['runningImages'] = runner.inspect_images(report['bundle']['images'])
                runner.run([bundle / 'scripts/deployment/verify.sh'], 'packaged-http-verification')
                runner.browser('browser-initial', args.inject_fault)
                with gate.stopping(env['COMPOSE_PROJECT_NAME'], HOSTS):
                    runner.compose('stop', 'romd-admin', 'romd-consumer', 'romd-worker', stage='restart-stop-hosts')
                runner.compose('up', '-d', '--wait', '--wait-timeout', '300', 'postgres', 'romd-worker',
                               stage='restart-worker-first')
                runner.compose('up', '-d', '--wait', '--wait-timeout', '300', stage='restart-start-hosts')
                gate.checkpoint(env['COMPOSE_PROJECT_NAME'])
                runner.browser('browser-after-restart', 'none')
                with gate.stopping(env['COMPOSE_PROJECT_NAME'], HOSTS):
                    runner.run([bundle / 'scripts/backup/backup.sh', work / 'backups'], 'packaged-backup')
                # The backup's EXIT trap starts hosts asynchronously. Finish that
                # resume before stopping the source again, as the source smoke does.
                runner.compose('up', '-d', '--wait', '--wait-timeout', '300', stage='backup-resume-readiness')
                gate.checkpoint(env['COMPOSE_PROJECT_NAME'])
                backup_sets = list((work / 'backups').glob('*/manifest.json'))
                if len(backup_sets) != 1:
                    raise RuntimeError('Packaged backup did not produce exactly one complete backup set')
                backup_set = backup_sets[0].parent
                # Stop the source before exposing its restored identity, and free its ingress socket/network.
                with gate.stopping(env['COMPOSE_PROJECT_NAME']):
                    runner.compose('stop', stage='stop-source-before-restore')
                    gate.diagnostics(env['COMPOSE_PROJECT_NAME'])
                    runner.compose('down', stage='remove-source-before-restore')
                runner.compose('up', '-d', '--wait', '--wait-timeout', '300', 'postgres',
                               stage='restore-empty-postgres', env=restore_env)
                runner.compose('create', '--no-recreate', 'romd-worker', stage='restore-data-volume', env=restore_env)
                with gate.stopping(restore_env['COMPOSE_PROJECT_NAME'], HOSTS):
                    runner.run([bundle / 'scripts/backup/restore.sh', backup_set], 'packaged-restore', env=restore_env)
                runner.compose('up', '-d', '--wait', '--wait-timeout', '300', 'postgres', 'romd-worker',
                               stage='restore-worker-first', env=restore_env)
                runner.compose('up', '-d', '--wait', '--wait-timeout', '300', stage='restore-hosts', env=restore_env)
                gate.checkpoint(restore_env['COMPOSE_PROJECT_NAME'])
                report['restoredRunningImages'] = runner.inspect_images(report['bundle']['images'], env=restore_env)
                runner.run([bundle / 'scripts/deployment/verify.sh'], 'restored-http-verification', env=restore_env)
                runner.browser('browser-after-restore', 'none')
            report['result'] = 'passed'
    except (ValueError, RuntimeError, KeyError, OSError, IndexError, TypeError, AttributeError,
            subprocess.SubprocessError, json.JSONDecodeError, tarfile.TarError) as error:
        # Messages from our own fixed diagnostics contain no subprocess/browser payloads.
        report['failure'] = str(error) if type(error) in (ValueError, RuntimeError) else type(error).__name__
    if 'gate' in locals() and gate.receipt is not None:
        report['lifecycle'] = {key: gate.receipt[key] for key in ('result', 'complete', 'errors', 'cleanupErrors')}
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('bundle', 'checksum', 'expected-images', 'publisher-state', 'evidence-dir'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--expected-revision', required=True)
    parser.add_argument('--expected-tag', required=True)
    parser.add_argument('--nginx-image', help='Reviewed immutable ingress image reference')
    parser.add_argument('--inject-fault', choices=('none', 'portal-asset', 'player-startup'), default='none')
    parser.add_argument('--verify-only', action='store_true', help='Verify archive; this does not qualify an installation')
    args = parser.parse_args()
    if not re.fullmatch('[0-9a-f]{40}', args.expected_revision):
        parser.error('--expected-revision must be a full Git SHA-1')
    if not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?', args.expected_tag):
        parser.error('--expected-tag must be a release version tag')
    if not args.verify_only and not DIGEST_REF.fullmatch(args.nginx_image or ''):
        parser.error('--nginx-image must be digest-pinned')
    # Browser commands run from the extracted bundle, not the caller's cwd.
    args.evidence_dir = args.evidence_dir.resolve()
    args.evidence_dir.mkdir(parents=True, exist_ok=False, mode=0o700)
    report = qualify(args, args.evidence_dir)
    (args.evidence_dir / 'qualification.json').write_text(json.dumps(report, indent=2) + '\n')
    print(f'Packaged qualification: {report["result"]}; evidence: {args.evidence_dir}')
    return 0 if report['result'] in ('passed', 'verified-bundle-only') else 1


if __name__ == '__main__':
    raise SystemExit(main())
