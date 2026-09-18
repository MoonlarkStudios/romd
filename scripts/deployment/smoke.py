#!/usr/bin/env python3
"""Disposable real-Compose startup, HTTPS/PKCE, restart and backup/restore drill.
Only destroys uniquely named projects created by this invocation. Never uses .env.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
from urllib.parse import urlencode, urlsplit, parse_qs

from test_contract import ROOT, test_environment


def run(args, env, capture=False):
    return subprocess.run([str(x) for x in args], env=env, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None).stdout


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--build', action='store_true')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='romd-compose-smoke-') as temporary:
        work = Path(temporary)
        env = test_environment()
        env['ROMD_DEFAULT_ADMIN_EMAIL'] = 'admin@example.test'
        env['ROMD_TLS_DIRECTORY'] = str(work)
        env['ROMD_HTTPS_PORT'] = '0'
        env['ROMD_NGINX_IMAGE'] = os.environ.get('ROMD_NGINX_IMAGE', 'nginx:1.27-alpine')
        env['COMPOSE_PROJECT_NAME'] = 'romd-smoke-' + secrets.token_hex(5)
        source_project = env['COMPOSE_PROJECT_NAME']
        target_project = source_project + '-restore'
        env['ROMD_ENV_FILE'] = str(work / 'empty.env')
        (work / 'empty.env').touch()
        compose = ROOT / 'scripts/deployment/compose.sh'
        run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', work / 'privkey.pem',
             '-out', work / 'fullchain.pem', '-days', '1', '-subj', '/CN=example.test'], env, capture=True)
        if args.build:
            env['COMPOSE_FILE'] = f'{ROOT}/compose.yaml:{ROOT}/compose.build.yaml'
            run([compose, 'build'], env)
        env['COMPOSE_FILE'] = f'{ROOT}/compose.yaml:{ROOT}/deploy/examples/https/compose.yaml'
        env.update(ROMD_EDGE_SUBNET='172.30.0.0/24', ROMD_EDGE_DYNAMIC_RANGE='172.30.0.128/25', ROMD_TRUSTED_PROXY='172.30.0.2', ROMD_ADMIN_EDGE_IP='172.30.0.3', ROMD_CONSUMER_EDGE_IP='172.30.0.4', ROMD_PLAYER_EDGE_IP='172.30.0.5')

        def request(host, path, form=None, token=None):
            # Self-signed TLS is deliberate in this isolated CI fixture only.
            command = [compose, 'exec', '-T', 'romd-admin', 'curl', '-ski', '--max-time', '20', '--noproxy', '*',
                       '--resolve', f'{host}:443:{env["ROMD_TRUSTED_PROXY"]}', '-b', '/tmp/smoke-cookies', '-c', '/tmp/smoke-cookies']
            if form is not None:
                command += ['--data', urlencode(form)]
            if token:
                command += ['-H', 'Authorization: Bearer ' + token]
            command += ['https://' + host + path]
            response = run(command, env, capture=True)
            headers, body = response.split('\n\n', 1)
            status = int(headers.splitlines()[0].split()[1])
            fields = dict(line.split(': ', 1) for line in headers.splitlines()[1:] if ': ' in line)
            return status, {k.lower(): v for k, v in fields.items()}, body

        def verify():
            run([ROOT / 'scripts/deployment/verify.sh'], env)
            direct_status = run([compose, 'exec', '-T', 'romd-admin', 'curl', '-s', '-o', '/dev/null',
                '-w', '%{http_code}', '-H', 'X-Forwarded-Proto: https', '-H', 'Host: admin.example.test',
                'http://localhost:8080/.well-known/openid-configuration'], env, capture=True)
            assert direct_status != '200', 'untrusted peer can spoof HTTPS'

            for host in ('admin.example.test', 'console.example.test'):
                status, _, body = request(host, '/.well-known/openid-configuration')
                assert status == 200 and json.loads(body)['issuer'] == f'https://{host}/', 'discovery origin mismatch'
                assert request(host, '/')[0] == 200, 'portal unavailable'
            assert request('play.example.test', '/player-config.json')[0] == 200
            assert request('unknown.example.test', '/')[0] == 421, 'unknown hostname accepted'
            # Authenticate with authorization-code + PKCE through the real HTTPS proxy.
            host = 'admin.example.test'
            verifier = secrets.token_urlsafe(32)
            challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).decode().rstrip('=')
            redirect = f'https://{host}/auth/callback'
            authorize = '/connect/authorize?' + urlencode(dict(client_id='romd-admin-spa', response_type='code',
                scope='openid profile email roles offline_access', redirect_uri=redirect, code_challenge=challenge,
                code_challenge_method='S256', state='smoke'))
            status, _, _ = request(host, '/connect/login', dict(login=env['ROMD_DEFAULT_ADMIN_EMAIL'],
                password=env['ROMD_DEFAULT_ADMIN_PASSWORD'], returnUrl=authorize))
            assert status == 302, 'bootstrap login failed'
            status, headers, _ = request(host, authorize)
            assert status == 302, 'authorization failed'
            code = parse_qs(urlsplit(headers['location']).query)['code'][0]
            status, _, body = request(host, '/connect/token', dict(grant_type='authorization_code',
                client_id='romd-admin-spa', redirect_uri=redirect, code=code, code_verifier=verifier))
            assert status == 200, 'token exchange failed'
            token = json.loads(body)['access_token']
            assert request(host, '/api/auth/me', token=token)[0] == 200, 'authenticated API failed'
            status, _, identity = request('console.example.test', '/api/server/identity')
            assert status == 200
            return json.loads(identity)

        try:
            run([compose, 'up', '-d', '--wait', '--wait-timeout', '240'], env)
            identity = verify()
            run([compose, 'exec', '-T', 'romd-worker', 'sh', '-c',
                 'mkdir -p /var/lib/romd/content/smoke && printf restore-proof > /var/lib/romd/content/smoke/proof'], env)
            run([compose, 'restart', 'romd-worker', 'romd-admin', 'romd-consumer'], env)
            run([compose, 'up', '-d', '--wait', '--wait-timeout', '240'], env)
            assert identity == verify(), 'identity changed across restart'
            backup_root = work / 'backups'
            run([ROOT / 'scripts/backup/backup.sh', backup_root], env)
            backup = next(backup_root.iterdir())
            # Keep source data intact, but stop its containers before exposing a restored identity.
            run([compose, 'stop'], env)
            env['COMPOSE_PROJECT_NAME'] = target_project
            env.update(ROMD_EDGE_SUBNET='172.31.0.0/24', ROMD_EDGE_DYNAMIC_RANGE='172.31.0.128/25', ROMD_TRUSTED_PROXY='172.31.0.2', ROMD_ADMIN_EDGE_IP='172.31.0.3', ROMD_CONSUMER_EDGE_IP='172.31.0.4', ROMD_PLAYER_EDGE_IP='172.31.0.5')
            run([compose, 'up', '-d', '--wait', 'postgres'], env)
            run([compose, 'create', '--no-recreate', 'romd-worker'], env)
            run([ROOT / 'scripts/backup/restore.sh', backup], env)
            run([compose, 'up', '-d', '--wait', '--wait-timeout', '240'], env)
            assert identity == verify(), 'restored identity differs'
            proof = run([compose, 'exec', '-T', 'romd-worker', 'cat', '/var/lib/romd/content/smoke/proof'], env, capture=True)
            assert proof == 'restore-proof', 'content did not survive restore'
            print('Compose startup, HTTPS discovery, PKCE login, restart and restore drill passed.', flush=True)
        except BaseException:
            subprocess.run([str(compose), 'logs', '--tail', '80'], env=env)
            raise
        finally:
            for project in (source_project, target_project):
                env['COMPOSE_PROJECT_NAME'] = project
                subprocess.run([str(compose), 'down', '--volumes', '--remove-orphans'], env=env)


if __name__ == '__main__':
    main()
