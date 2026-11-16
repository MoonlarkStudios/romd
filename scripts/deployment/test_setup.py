import base64
import copy
import ipaddress
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import setup
from test_contract import ROOT, render, test_environment
from validate import validate

PASSWORD = 'Correct-Horse-42'
TOKEN = base64.b64encode(json.dumps({'a': 'account', 't': 'tunnel', 's': 'c2VjcmV0'}).encode()).decode()


class Session:
    """Scripted answers for the prompts, recording everything setup prints."""

    def __init__(self, answers=(), hidden=()):
        self.answers, self.hidden_answers, self.output = list(answers), list(hidden), []

    def ask(self, label):
        self.output.append(label)
        return self.answers.pop(0)

    def hidden(self, label):
        self.output.append(label)
        return self.hidden_answers.pop(0)

    def say(self, *parts):
        self.output.append(' '.join(str(part) for part in parts))

    def text(self):
        return '\n'.join(self.output)


class SetupTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.target = self.work / '.env'
        environment = patch.dict(os.environ, {'ROMD_ENV_FILE': str(self.target)})
        environment.start()
        self.addCleanup(environment.stop)
        self.library = self.work / 'roms'
        self.library.mkdir()
        self.secret_files = {}
        for name, value in (('password', PASSWORD), ('token', f'sudo cloudflared service install {TOKEN}')):
            path = self.work / name
            path.write_text(value + '\n')
            self.secret_files[name] = str(path)

    def run_setup(self, argv, session=None, subnets=()):
        session = session or Session()
        code = setup.main(argv, ask=session.ask, hidden=session.hidden, say=session.say,
                          subnets=lambda: None if subnets is None else [ipaddress.ip_network(s) for s in subnets],
                          terminal=lambda: True)
        return code, session

    def written(self):
        return dict(line.split('=', 1) for line in self.target.read_text().splitlines()
                    if re.match(r'[A-Z][A-Z0-9_]*=', line))

    def scripted(self, ingress, *extra):
        return ['--non-interactive', '--domain', 'Example.org', '--admin-email', 'owner@example.org',
                '--admin-password-file', self.secret_files['password'], '--ingress', ingress, *extra]

    def assert_valid_stack(self, values):
        """The generated .env renders the selected overlays into a configuration validation accepts."""
        environment = {key: value for key, value in test_environment().items()
                       if not key.startswith(('ROMD_', 'IGDB_', 'CLOUDFLARE_')) or key.endswith('_IMAGE')}
        environment.pop('ROMD_NGINX_IMAGE', None)
        environment.update({key: value for key, value in values.items() if not key.startswith('COMPOSE_')})
        overlays = values['COMPOSE_FILE'].split(':')
        self.assertEqual('compose.yaml', overlays[0])
        config = render(*overlays[1:], environment=environment)
        self.assertEqual([], validate(config))
        return config

    def test_cloudflare_install_generates_every_secret_and_a_valid_private_env(self):
        code, session = self.run_setup(self.scripted('cloudflare', '--tunnel-token-file', self.secret_files['token'],
                                                     '--library', str(self.library)))
        self.assertEqual(0, code, session.text())
        self.assertEqual(0o600, stat.S_IMODE(self.target.stat().st_mode))
        values = self.written()
        self.assertEqual('https://admin.example.org', values['ROMD_ADMIN_PUBLIC_URL'])
        self.assertEqual('console.example.org', values['ROMD_CONSUMER_HOST'])
        self.assertEqual('https://play.example.org', values['ROMD_PLAYER_PUBLIC_URL'])
        self.assertEqual(TOKEN, values['CLOUDFLARE_TUNNEL_TOKEN'])
        self.assertEqual('romd', values['COMPOSE_PROJECT_NAME'])
        self.assertEqual('compose.yaml:deploy/examples/cloudflare/compose.yaml:deploy/examples/library/compose.yaml',
                         values['COMPOSE_FILE'])
        generated = [values[key] for key in ('ROMD_JWT_SECRET', 'ROMD_CONSUMER_DELIVERY_SIGNING_SECRET',
                                             *(f'ROMD_POSTGRES_{role}_PASSWORD' for role in setup.DATABASE_ROLES))]
        self.assertTrue(all(re.fullmatch('[0-9a-f]{64}', value) for value in generated))
        self.assertEqual(len(generated), len(set(generated)))
        config = self.assert_valid_stack(values)
        self.assertEqual('/library', config['services']['romd-worker']['environment']['Romd__AllowedImportPaths__0'])
        mount = next(volume for volume in config['services']['romd-admin']['volumes'] if volume['target'] == '/library')
        self.assertEqual((str(self.library), True), (mount['source'], mount['read_only']))
        # No secret reaches the terminal.
        for value in [*generated, PASSWORD, TOKEN]:
            self.assertNotIn(value, session.text())
        self.assertIn('http://ingress:80', session.text())

    def test_https_and_existing_proxy_installs_render_valid_stacks(self):
        tls = self.work / 'tls'
        tls.mkdir()
        (tls / 'fullchain.pem').write_text('certificate')
        (tls / 'privkey.pem').write_text('key')
        code, session = self.run_setup(self.scripted('https', '--tls-directory', str(tls)))
        self.assertEqual(0, code, session.text())
        values = self.written()
        self.assertEqual(('0.0.0.0', str(tls)), (values['ROMD_HTTPS_BIND'], values['ROMD_TLS_DIRECTORY']))
        self.assertEqual('compose.yaml:deploy/examples/https/compose.yaml', values['COMPOSE_FILE'])
        self.assert_valid_stack(values)
        self.target.unlink()
        code, session = self.run_setup(self.scripted('proxy', '--trusted-proxy', '192.168.1.10'))
        self.assertEqual(0, code, session.text())
        values = self.written()
        self.assertEqual(('compose.yaml', '192.168.1.10'), (values['COMPOSE_FILE'], values['ROMD_TRUSTED_PROXY']))
        self.assertNotIn('ROMD_LIBRARY_PATH', {key for key, value in values.items() if value})
        self.assert_valid_stack(values)

    def test_interactive_prompts_accept_defaults_reprompt_bad_answers_and_hide_secrets(self):
        session = Session(
            answers=['https://example.org', 'example.org', '', 'not-an-email', 'owner@example.org', '', '', ''],
            hidden=['short', PASSWORD, 'Different-Horse-42', PASSWORD, PASSWORD, TOKEN])
        code, _ = self.run_setup([], session)
        self.assertEqual(0, code, session.text())
        values = self.written()
        self.assertEqual(('owner@example.org', PASSWORD), (values['ROMD_DEFAULT_ADMIN_EMAIL'], values['ROMD_DEFAULT_ADMIN_PASSWORD']))
        self.assertEqual('compose.yaml:deploy/examples/cloudflare/compose.yaml', values['COMPOSE_FILE'])
        text = session.text()
        for message in ('without https://', 'Enter an email address.', 'at least 12 characters', 'The two entries differ.'):
            self.assertIn(message, text)
        self.assertNotIn(PASSWORD, text)
        self.assertNotIn(TOKEN, text)
        self.assert_valid_stack(values)

    def test_custom_hostnames_must_be_distinct_dns_names(self):
        session = Session(answers=['example.org', 'n', 'romd.example.org', 'games.example.org', 'player.example.org',
                                   'owner@example.org', '3', '10.0.0.2', ''], hidden=[PASSWORD, PASSWORD])
        code, _ = self.run_setup([], session)
        self.assertEqual(0, code, session.text())
        self.assertEqual('https://games.example.org', self.written()['ROMD_CONSUMER_PUBLIC_URL'])
        self.target.unlink()
        code, session = self.run_setup(self.scripted('proxy', '--trusted-proxy', '10.0.0.2',
                                                     '--admin-host', 'same.example.org', '--consumer-host', 'same.example.org'))
        self.assertEqual(1, code)
        self.assertIn('must all differ', session.text())
        self.assertFalse(self.target.exists())

    def test_edge_addresses_move_to_a_free_subnet_together(self):
        code, session = self.run_setup(self.scripted('cloudflare', '--tunnel-token-file', self.secret_files['token']),
                                       subnets=('172.28.0.0/16', '172.29.0.0/24'))
        self.assertEqual(0, code, session.text())
        values = self.written()
        self.assertEqual(('172.30.0.0/24', '172.30.0.128/25', '172.30.0.2', '172.30.0.6'),
                         (values['ROMD_EDGE_SUBNET'], values['ROMD_EDGE_DYNAMIC_RANGE'], values['ROMD_TRUSTED_PROXY'],
                          values['ROMD_CLOUDFLARED_EDGE_IP']))
        self.assert_valid_stack(values)
        self.target.unlink()
        code, session = self.run_setup(self.scripted('cloudflare', '--tunnel-token-file', self.secret_files['token']),
                                       subnets=None)
        self.assertEqual(0, code)
        self.assertIn('could not be listed', session.text())
        self.assertEqual('172.28.0.0/24', self.written()['ROMD_EDGE_SUBNET'])
        self.target.unlink()
        code, session = self.run_setup(self.scripted('cloudflare', '--tunnel-token-file', self.secret_files['token']),
                                       subnets=('0.0.0.0/0',))
        self.assertEqual(1, code)
        self.assertIn('by hand', session.text())

    def test_an_existing_env_is_never_replaced_and_failures_write_nothing(self):
        self.target.write_text('KEEP=1\n')
        code, session = self.run_setup(self.scripted('proxy', '--trusted-proxy', '10.0.0.2'))
        self.assertEqual(1, code)
        self.assertIn('already exists', session.text())
        self.assertEqual('KEEP=1\n', self.target.read_text())
        self.target.unlink()
        for argv in (['--non-interactive'], self.scripted('https', '--tls-directory', str(self.work)),
                     self.scripted('proxy', '--trusted-proxy', 'proxy.local'),
                     self.scripted('proxy', '--trusted-proxy', '10.0.0.2', '--library', 'relative/path'),
                     self.scripted('proxy', '--trusted-proxy', '10.0.0.2', '--project', 'Bad Name')):
            with self.subTest(argv=argv):
                code, session = self.run_setup(argv)
                self.assertEqual(1, code)
                self.assertFalse(self.target.exists(), session.text())
        weak = self.work / 'weak'
        weak.write_text('password\n')
        code, session = self.run_setup(['--non-interactive', '--domain', 'example.org', '--admin-email', 'a@example.org',
                                        '--admin-password-file', str(weak), '--ingress', 'proxy', '--trusted-proxy', '10.0.0.2'])
        self.assertEqual(1, code)
        self.assertIn('at least 12 characters', session.text())
        code = setup.main([], say=lambda *_: None, terminal=lambda: False)
        self.assertEqual(1, code)
        # A source checkout's root .env is the development environment; setup never targets it implicitly.
        with patch.dict(os.environ, {'ROMD_ENV_FILE': ''}):
            code, session = self.run_setup(self.scripted('proxy', '--trusted-proxy', '10.0.0.2'))
        self.assertEqual(1, code)
        self.assertIn('ROMD_ENV_FILE', session.text())

    def test_tunnel_token_is_taken_from_a_pasted_install_command(self):
        self.assertEqual(TOKEN, setup.tunnel_token(f'cloudflared service install {TOKEN}'))
        self.assertTrue(setup.looks_like_tunnel_token(TOKEN))
        self.assertFalse(setup.looks_like_tunnel_token('not-a-token'))

    def test_template_keeps_comments_and_fills_every_setup_value(self):
        template = (ROOT / 'deploy/.env.example').read_text()
        keys = {line.split('=', 1)[0] for line in template.splitlines() if re.match(r'[A-Z][A-Z0-9_]*=', line)}
        code, _ = self.run_setup(self.scripted('cloudflare', '--tunnel-token-file', self.secret_files['token'],
                                               '--library', str(self.library)))
        self.assertEqual(0, code)
        text = self.target.read_text()
        self.assertNotIn('# Added by setup.sh', text)
        self.assertIn('# Exact HTTPS origins', text)
        self.assertEqual(keys, set(self.written()))


class ImportMountValidationTests(unittest.TestCase):
    def test_admin_and_worker_must_share_each_import_root_at_the_same_path(self):
        environment = dict(test_environment(), ROMD_LIBRARY_PATH='/srv/roms')
        original = render('deploy/examples/library/compose.yaml', environment=environment)
        self.assertEqual([], validate(original))
        message = 'must be inside the same mount on romd-admin and romd-worker'
        for change in ('drop-worker-mount', 'other-source', 'nested-root'):
            config = copy.deepcopy(original)
            admin, worker = config['services']['romd-admin'], config['services']['romd-worker']
            if change == 'drop-worker-mount':
                worker['volumes'] = [volume for volume in worker['volumes'] if volume['target'] != '/library']
            elif change == 'other-source':
                next(volume for volume in worker['volumes'] if volume['target'] == '/library')['source'] = '/srv/other'
            else:
                admin['environment']['Romd__AllowedImportPaths__0'] = '/library/nes'
                worker['environment']['Romd__AllowedImportPaths__0'] = '/library/nes'
            with self.subTest(change=change):
                errors = validate(config)
                self.assertEqual([] if change == 'nested-root' else [f'Path import root /library {message}'], errors)
        config = copy.deepcopy(original)
        config['services']['romd-worker']['environment'].pop('Romd__AllowedImportPaths__0')
        self.assertIn('Path import roots must be identical on romd-admin and romd-worker', validate(config))


class ReleaseLayoutTests(unittest.TestCase):
    def test_setup_from_an_extracted_release_feeds_compose_the_recorded_project_and_overlays(self):
        with tempfile.TemporaryDirectory() as temporary:
            work = Path(temporary)
            images = work / 'images.env'
            images.write_text(''.join(f'ROMD_{name}_IMAGE=example/{name.lower()}@sha256:{"a" * 64}\n'
                                      for name in ('ADMIN', 'CONSUMER', 'WORKER', 'PLAYER', 'POSTGRES')))
            archive = work / 'romd-v1.2.3-deployment.tar.gz'
            subprocess.run([ROOT / 'scripts/deployment/bundle.sh', images, archive, 'a' * 40, 'v1.2.3'],
                           check=True, capture_output=True)
            with tarfile.open(archive) as bundle:
                bundle.extractall(work, filter='data')
            release = work / 'romd-v1.2.3'
            password = work / 'password'
            password.write_text(PASSWORD)
            environment = {key: value for key, value in os.environ.items() if not key.startswith(('ROMD_', 'COMPOSE_'))}
            result = subprocess.run([release / 'scripts/deployment/setup.sh', '--non-interactive', '--domain', 'example.org',
                                     '--admin-email', 'owner@example.org', '--admin-password-file', password,
                                     '--ingress', 'proxy', '--trusted-proxy', '10.0.0.2'],
                                    cwd=work, env=environment, capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual(0o600, stat.S_IMODE((release / '.env').stat().st_mode))
            self.assertNotIn(PASSWORD, result.stdout + result.stderr)
            # A stand-in docker reports what compose.sh hands to Compose, from an unrelated directory.
            tools = work / 'bin'
            tools.mkdir()
            (tools / 'docker').write_text('#!/bin/sh\nprintf "%s\\n" "$COMPOSE_PROJECT_NAME" "$COMPOSE_FILE" "$@"\n')
            (tools / 'docker').chmod(0o755)
            environment['PATH'] = f'{tools}:{environment["PATH"]}'
            seen = subprocess.run([release / 'scripts/deployment/compose.sh', 'config'], cwd=tools, env=environment,
                                  capture_output=True, text=True, check=True).stdout.splitlines()
            self.assertEqual(['romd', f'{release}/compose.yaml', 'compose', '--project-directory', str(release),
                              '--env-file', f'{release}/images.env', '--env-file', f'{release}/.env', 'config'], seen)
            # Values the caller exports keep precedence over the env file.
            seen = subprocess.run([release / 'scripts/deployment/compose.sh', 'config'], cwd=tools,
                                  env=dict(environment, COMPOSE_PROJECT_NAME='other', COMPOSE_FILE='/x.yaml'),
                                  capture_output=True, text=True, check=True).stdout.splitlines()
            self.assertEqual(['other', '/x.yaml'], seen[:2])


if __name__ == '__main__':
    unittest.main()
