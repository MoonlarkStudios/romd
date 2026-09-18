#!/usr/bin/env python3
"""Exercise Docker's merged configuration, including regressions in network topology."""
import copy
import json
import os
from pathlib import Path
import subprocess
import unittest
import tempfile
import tarfile

from validate import validate

ROOT = Path(__file__).resolve().parents[2]


def test_environment():
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(('ROMD_', 'COMPOSE_', 'IGDB_', 'CLOUDFLARE_'))}
    for index, role in enumerate(('BOOTSTRAP', 'PROVISIONER', 'WORKER', 'ADMIN', 'CONSUMER'), 1):
        env[f'ROMD_POSTGRES_{role}_PASSWORD'] = str(index) * 64
    for name in ('ADMIN', 'CONSUMER', 'WORKER', 'PLAYER'):
        env[f'ROMD_{name}_IMAGE'] = f'romd-{name.lower()}:test'
    env.update(ROMD_POSTGRES_IMAGE='postgres:18-bookworm', ROMD_JWT_SECRET='a' * 64,
               ROMD_DEFAULT_ADMIN_PASSWORD='TestAdmin123!', ROMD_CONSUMER_DELIVERY_SIGNING_SECRET='b' * 64,
               ROMD_ADMIN_PUBLIC_URL='https://admin.example.test', ROMD_CONSUMER_PUBLIC_URL='https://console.example.test',
               ROMD_PLAYER_PUBLIC_URL='https://play.example.test', ROMD_TRUSTED_PROXY='172.28.0.2',
               ROMD_ADMIN_HOST='admin.example.test', ROMD_CONSUMER_HOST='console.example.test', ROMD_PLAYER_HOST='play.example.test',
               ROMD_NGINX_IMAGE='nginx:1.27-alpine', ROMD_CLOUDFLARED_IMAGE='cloudflare/cloudflared:test',
               CLOUDFLARE_TUNNEL_TOKEN='test-only', ROMD_TLS_DIRECTORY='/tmp/romd-test-certs')
    return env


def render(*overlays):
    args = ['docker', 'compose', '--env-file', '/dev/null', '-f', str(ROOT / 'compose.yaml')]
    for overlay in overlays:
        args += ['-f', str(ROOT / overlay)]
    args += ['config', '--format', 'json']
    return json.loads(subprocess.check_output(args, env=test_environment()))


class ProductionContractTests(unittest.TestCase):
    def test_base_has_no_builds_ports_or_personal_container_names(self):
        config = render()
        self.assertEqual([], validate(config))
        for service in config['services'].values():
            for key in ('build', 'ports', 'container_name'):
                self.assertNotIn(key, service)
            self.assertIn('max-size', service['logging']['options'])

    def test_rejects_disconnected_database_regression(self):
        config = render()
        config['services']['romd-consumer']['networks'] = {'edge': None}
        self.assertIn('romd-consumer: no shared network with PostgreSQL', validate(config))

    def test_ingress_examples_preserve_backend_connectivity_and_isolation(self):
        for example in ('https', 'cloudflare'):
            config = render(f'deploy/examples/{example}/compose.yaml')
            self.assertEqual([], validate(config))
            self.assertEqual({'edge'}, set(config['services']['ingress']['networks']))
            addresses = [config['services'][name]['networks']['edge']['ipv4_address']
                         for name in ('ingress', 'romd-admin', 'romd-consumer', 'romd-player')]
            self.assertEqual(4, len(set(addresses)))
            if example == 'cloudflare':
                self.assertFalse(any(s.get('ports') for s in config['services'].values()))
            template = (ROOT / f'deploy/examples/{example}/templates/default.conf.template').read_text()
            self.assertIn('return 421', template)
            self.assertNotIn('$http_x_forwarded_proto', template)

    def test_secrets_origins_and_player_boundary_are_validated(self):
        original = render()
        config = copy.deepcopy(original)
        config['services']['postgres']['environment']['ROMD_POSTGRES_ADMIN_PASSWORD'] = 'unsafe;password'
        self.assertTrue(validate(config))
        config = copy.deepcopy(original)
        config['services']['romd-worker']['environment']['Romd__Auth__ConsumerSpa__PostLogoutRedirectUris__0'] = 'http://console.example.test'
        self.assertTrue(validate(config))
        config = copy.deepcopy(original)
        config['services']['romd-player']['networks']['backend'] = None
        self.assertTrue(validate(config))

    def test_ingress_host_and_address_mismatch_are_rejected(self):
        config = render('deploy/examples/https/compose.yaml')
        config['services']['ingress']['environment']['ROMD_ADMIN_HOST'] = 'wrong.example.test'
        self.assertTrue(validate(config))
        config = render('deploy/examples/https/compose.yaml')
        config['services']['ingress']['networks']['edge']['ipv4_address'] = '172.28.0.3'
        self.assertTrue(validate(config))

    def test_release_bundle_is_source_free_and_contains_restore_dependencies(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            images = work / 'images.env'
            images.write_text(''.join(f'ROMD_{name}_IMAGE=example/{name.lower()}@sha256:{"a" * 64}\n'
                                      for name in ('ADMIN', 'CONSUMER', 'WORKER', 'PLAYER', 'POSTGRES')))
            archive = work / 'release.tar.gz'
            subprocess.run([str(ROOT / 'scripts/deployment/bundle.sh'), str(images), str(archive)], check=True)
            with tarfile.open(archive) as bundle:
                names = {name.removeprefix('./') for name in bundle.getnames()}
                for required in ('compose.yaml', 'images.env', '.env.example',
                                 'scripts/backup/restore.sh', 'scripts/deployment/compose.sh',
                                 'deploy/postgres/init/10-romd-roles.sh',
                                 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'TRADEMARKS.md',
                                 'brand/Funnel-OFL.txt',
                                 'web/packages/romd-foundation/src/fonts/Archivo-OFL.txt',
                                 'web/packages/romd-foundation/src/fonts/IBMPlexMono-OFL.txt',
                                 'reference-data/assets/presentation/platforms/ATTRIBUTION.md',
                                 'reference-data/assets/presentation/ratings/ATTRIBUTION.md'):
                    self.assertIn(required, names)
                self.assertNotIn('.env', names)
                self.assertFalse(any(name.startswith(('src/', '.git/')) for name in names))
                web_files = {member.name.removeprefix('./') for member in bundle.getmembers()
                             if member.isfile() and member.name.removeprefix('./').startswith('web/')}
                self.assertEqual({
                    'web/packages/romd-foundation/src/fonts/Archivo-OFL.txt',
                    'web/packages/romd-foundation/src/fonts/IBMPlexMono-OFL.txt',
                }, web_files)
            subprocess.run(['shasum', '-a', '256', '-c', 'release.tar.gz.sha256'], cwd=work, check=True)

    def test_missing_secret_fails_compose_interpolation(self):
        env = test_environment()
        env['ROMD_POSTGRES_ADMIN_PASSWORD'] = ''
        result = subprocess.run(['docker', 'compose', '--env-file', '/dev/null', '-f', str(ROOT / 'compose.yaml'), 'config', '--quiet'], env=env, capture_output=True)
        self.assertNotEqual(0, result.returncode)


if __name__ == '__main__':
    unittest.main()
