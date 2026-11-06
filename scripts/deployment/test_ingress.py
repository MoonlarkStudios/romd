#!/usr/bin/env python3
"""Exercise the shipped tunnel template through real nginx HTTP requests.

Uses one pinned nginx container with no network or published ports. Loopback
source addresses model the pinned connector and an untrusted edge peer; nginx
echo upstreams expose the headers the application actually receives. This is
ingress behavior coverage, not Docker production-image/release qualification.
"""
from contextlib import contextmanager
import json
import re
from pathlib import Path
import select
import subprocess
import sys
import tempfile
import time
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
from containers.runtime import Runtime


class CloudflareIngressTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.runtime = Runtime()
        cls.environment = cls.runtime.child_environment()
        temporary = tempfile.TemporaryDirectory(prefix='romd-ingress-contract-')
        cls.addClassCleanup(temporary.cleanup)
        work = Path(temporary.name)
        template = (ROOT / 'deploy/examples/cloudflare/templates/default.conf.template').read_text()
        for index, surface in enumerate(('admin', 'consumer', 'player'), 1):
            template = template.replace(f'http://romd-{surface}:8080', f'http://127.0.0.1:{8080 + index}')
        (work / 'default.conf.template').write_text(template)
        (work / 'nginx.conf').write_text('''worker_processes 1;
error_log /dev/stderr warn;
events { worker_connections 64; }
http { access_log /dev/stdout combined; include /etc/nginx/conf.d/*.conf; }
''')
        (work / 'echo.conf').write_text('\n'.join(
            f'''server {{
    listen {8080 + index};
    access_log off;
    location / {{
        return 200 '{surface}|$http_x_forwarded_for|$http_x_forwarded_host|$http_x_forwarded_proto|$http_cf_connecting_ip|$http_cf_connecting_ipv6|$request_uri|$http_upgrade|$http_connection';
    }}
}}''' for index, surface in enumerate(('admin', 'consumer', 'player'), 1)))
        cls.container = 'romd-ingress-contract-' + uuid.uuid4().hex[:12]
        cls.addClassCleanup(cls.remove_container)
        image = json.loads((ROOT / 'scripts/deployment/base-images.json').read_text())['ingress']
        command = ['run', '--detach', '--name', cls.container, '--network', 'none',
                   '--memory', '96m', '--cpus', '1',
                   '--env', 'ROMD_ADMIN_HOST=admin.example.test',
                   '--env', 'ROMD_CONSUMER_HOST=console.example.test',
                   '--env', 'ROMD_PLAYER_HOST=play.example.test',
                   '--env', 'ROMD_CLOUDFLARED_EDGE_IP=127.0.0.2',
                   '--env', 'NGINX_ENVSUBST_FILTER=^ROMD_',
                   '--volume', f'{work / "nginx.conf"}:/etc/nginx/nginx.conf:ro,Z',
                   '--volume', f'{work / "echo.conf"}:/etc/nginx/conf.d/echo.conf:ro,Z',
                   '--volume', f'{work / "default.conf.template"}:/etc/nginx/templates/default.conf.template:ro,Z',
                   image]
        cls.run_engine(*command)
        for _ in range(30):
            response = cls.request(headers={'CF-Connecting-IP': '198.51.100.10'}, check=False)
            if response and response[0] == 200:
                break
            time.sleep(0.1)
        else:
            logs = cls.run_engine('logs', cls.container)
            raise AssertionError(f'nginx did not become ready (last response {response}; '
                                 f'{getattr(cls, "last_client_error", "")}): {logs.stdout}{logs.stderr}')

    @classmethod
    def run_engine(cls, *arguments, **options):
        return subprocess.run(cls.runtime.engine_command(*arguments), env=cls.environment,
                              capture_output=True, text=True, timeout=60, check=True, **options)

    @classmethod
    def remove_container(cls):
        # Scope cleanup to the one randomly named container created by this class.
        subprocess.run(cls.runtime.engine_command('rm', '--force', cls.container),
                       env=cls.environment, capture_output=True, timeout=30, check=True)

    @classmethod
    def request(cls, *, source='127.0.0.2', host='console.example.test', path='/',
                headers=None, check=True):
        lines = [f'GET {path} HTTP/1.1', f'Host: {host}', 'Connection: close']
        values = headers.items() if isinstance(headers, dict) else headers or []
        lines += [f'{key}: {value}' for key, value in values]
        # BusyBox nc exits on stdin EOF. Retain its input until the complete
        # Content-Length-delimited response arrives, without a timing sleep.
        output = b''
        with subprocess.Popen(cls.runtime.engine_command('exec', '-i', cls.container,
                              'nc', '-s', source, '-w', '5', '127.0.0.1', '80'),
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              env=cls.environment) as process:
            try:
                process.stdin.write(('\r\n'.join(lines) + '\r\n\r\n').encode())
                process.stdin.flush()
                deadline = time.monotonic() + 5
                while time.monotonic() < deadline:
                    ready, _, _ = select.select([process.stdout], [], [], max(0, deadline - time.monotonic()))
                    if not ready:
                        break
                    chunk = process.stdout.read1(65536)
                    if not chunk:
                        break
                    output += chunk
                    head, separator, body = output.partition(b'\r\n\r\n')
                    if separator:
                        length = next((int(line.split(b':', 1)[1]) for line in head.split(b'\r\n')
                                       if line.lower().startswith(b'content-length:')), None)
                        if length is not None and len(body) >= length:
                            break
                tail, errors = process.communicate(timeout=5)
                output += tail
            except BaseException:
                process.kill()
                process.communicate()
                raise
        decoded = output.decode(errors='replace')
        if not decoded.startswith('HTTP/1.1 '):
            cls.last_client_error = f'exit={process.returncode} stdout={decoded!r} stderr={errors!r}'
            if check:
                raise AssertionError(f'HTTP request failed: {cls.last_client_error}')
            return None
        head, _, body = decoded.partition('\r\n\r\n')
        return int(head.split()[1]), body

    def test_distinct_ipv4_and_ipv6_visitors_replace_injected_forwarded_headers(self):
        for address in ('198.51.100.10', '198.51.100.11', '2001:db8::1234', '::ffff:192.0.2.20'):
            with self.subTest(address=address):
                status, body = self.request(headers={'CF-Connecting-IP': address,
                    'CF-Connecting-IPv6': '2001:db8::bad',
                    'X-Forwarded-For': '203.0.113.66, 203.0.113.67',
                    'X-Forwarded-Host': 'attacker.example.test', 'X-Forwarded-Proto': 'http'})
                self.assertEqual(200, status)
                self.assertEqual(f'consumer|{address}|console.example.test|https|||/||close', body)

    def test_untrusted_direct_peer_cannot_claim_connector_or_visitor_identity(self):
        for headers in ({}, {'CF-Connecting-IP': '198.51.100.10',
                            'X-Forwarded-For': '127.0.0.2', 'X-Real-IP': '127.0.0.2'},
                        {'CF-Connecting-IP': '127.0.0.2'}):
            with self.subTest(headers=headers):
                status, _ = self.request(source='127.0.0.3', headers=headers)
                self.assertEqual(403, status)

    def test_missing_malformed_chained_and_port_qualified_identity_fail_closed(self):
        for address in (None, '', 'not-an-ip', '999.1.2.3', '2001:db8:::1',
                        '198.51.100.10, 198.51.100.11', '198.51.100.10,198.51.100.11',
                        '198.51.100.10:443', '[2001:db8::1]:443', '127.0.0.2'):
            with self.subTest(address=address):
                headers = {} if address is None else {'CF-Connecting-IP': address}
                status, _ = self.request(headers=headers)
                self.assertEqual(400, status)
        status, _ = self.request(headers=[('CF-Connecting-IP', '198.51.100.10'),
                                          ('CF-Connecting-IP', '198.51.100.11')])
        self.assertEqual(400, status)

    def test_surface_routing_unknown_host_and_escaped_vfs_path_are_preserved(self):
        for surface, host in (('admin', 'admin'), ('consumer', 'console'), ('player', 'play')):
            with self.subTest(surface=surface):
                status, body = self.request(host=f'{host}.example.test', path='/vfs/a%20b%23c',
                                           headers={'CF-Connecting-IP': '198.51.100.10'})
                self.assertEqual(200, status)
                self.assertEqual(f'{surface}|198.51.100.10|{host}.example.test|https|||/vfs/a%20b%23c||close', body)
        status, _ = self.request(host='attacker.example.test', headers={'CF-Connecting-IP': '198.51.100.10'})
        self.assertEqual(421, status)

    def test_websocket_upgrade_headers_reach_upstream(self):
        status, body = self.request(path='/hubs/admin', headers={'CF-Connecting-IP': '198.51.100.10',
                                                               'Upgrade': 'websocket'})
        self.assertEqual(200, status)
        self.assertTrue(body.endswith('/hubs/admin|websocket|upgrade'), body)


class IngressLoggingTests(unittest.TestCase):
    """Both shipped templates retain useful status events without request credentials."""

    @contextmanager
    def fixture(self, kind):
        runtime = Runtime()
        environment = runtime.child_environment()
        container = 'romd-ingress-logging-' + uuid.uuid4().hex[:12]

        def engine(*arguments, check=True):
            return subprocess.run(runtime.engine_command(*arguments), env=environment,
                                  capture_output=True, text=True, timeout=60, check=check)

        with tempfile.TemporaryDirectory(prefix='romd-ingress-logging-') as temporary:
            work = Path(temporary)
            template = (ROOT / f'deploy/examples/{kind}/templates/default.conf.template').read_text()
            for index, surface in enumerate(('admin', 'consumer', 'player'), 1):
                template = template.replace(f'http://romd-{surface}:8080', f'http://127.0.0.1:{8080 + index}')
            template = template.replace('/etc/romd/tls/', '/romd-test/')
            (work / 'default.conf.template').write_text(template)
            # Model the image's normal inherited logs. Only the backend fixture's
            # own logs are disabled; the shipped ingress policy must override this.
            (work / 'nginx.conf').write_text('''worker_processes 1;
error_log /dev/stderr warn;
events { worker_connections 64; }
http { access_log /dev/stdout combined; include /etc/nginx/conf.d/*.conf; }
''')
            (work / 'echo.conf').write_text('\n'.join(
                f'''server {{
    listen {8080 + index};
    access_log off;
    location / {{
        if ($http_x_romd_test_fail = 1) {{ return 444; }}
        return 200 'upstream-ready';
    }}
}}''' for index in range(1, 4)))
            if kind == 'https':
                subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                                '-keyout', str(work / 'privkey.pem'), '-out', str(work / 'fullchain.pem'),
                                '-days', '1', '-subj', '/CN=ROMD ingress logging test'],
                               capture_output=True, check=True, timeout=30)
            image = json.loads((ROOT / 'scripts/deployment/base-images.json').read_text())['ingress']
            engine('run', '--detach', '--name', container, '--network', 'none',
                   '--memory', '96m', '--cpus', '1',
                   '--env', 'ROMD_ADMIN_HOST=admin.example.test',
                   '--env', 'ROMD_CONSUMER_HOST=console.example.test',
                   '--env', 'ROMD_PLAYER_HOST=play.example.test',
                   '--env', 'ROMD_CLOUDFLARED_EDGE_IP=127.0.0.1',
                   '--env', 'NGINX_ENVSUBST_FILTER=^ROMD_',
                   '--volume', f'{work}:/romd-test:ro,Z',
                   '--volume', f'{work / "nginx.conf"}:/etc/nginx/nginx.conf:ro,Z',
                   '--volume', f'{work / "echo.conf"}:/etc/nginx/conf.d/echo.conf:ro,Z',
                   '--volume', f'{work / "default.conf.template"}:/etc/nginx/templates/default.conf.template:ro,Z',
                   image)
            try:
                def request(path, fail=False):
                    scheme = 'https' if kind == 'https' else 'http'
                    command = ['exec', container, 'wget', '--no-check-certificate', '-S', '-O', '-',
                               '--header', 'Host: console.example.test',
                               '--header', 'CF-Connecting-IP: 198.51.100.10']
                    if fail:
                        command += ['--header', 'X-ROMD-Test-Fail: 1']
                    result = engine(*command, f'{scheme}://127.0.0.1{path}', check=False)
                    statuses = re.findall(r'HTTP/1\.[01] (\d{3})', result.stderr)
                    return int(statuses[-1]) if statuses else None

                for _ in range(30):
                    if request('/ready') == 200:
                        break
                    time.sleep(0.1)
                else:
                    self.fail('nginx logging fixture did not become ready')
                yield request, lambda: engine('logs', container)
            finally:
                engine('rm', '--force', container)

    def test_credentials_are_absent_from_success_and_upstream_failure_logs(self):
        for kind in ('https', 'cloudflare'):
            with self.subTest(kind=kind), self.fixture(kind) as (request, logs):
                capability = 'synthetic-path-' + uuid.uuid4().hex
                credential = 'synthetic-query-' + uuid.uuid4().hex
                paths = [f'/delivery/content/{capability}', f'/delivery/derived-descriptor/{capability}',
                         f'/api/download-tickets/{capability}', '/auth/callback', '/hubs/jobs']
                for path in paths:
                    path += f'?code={credential}&access_token={credential}&user_code={credential}'
                    self.assertEqual(200, request(path))
                    self.assertEqual(502, request(path, fail=True))
                output = logs()
                combined = output.stdout + output.stderr
                self.assertNotIn(capability, combined)
                self.assertNotIn(credential, combined)
                events = [json.loads(line) for line in output.stdout.splitlines() if line.startswith('{')]
                events = [event for event in events if event.get('event') == 'ingress_request']
                self.assertGreaterEqual(sum(event['status'] == 200 for event in events), len(paths))
                self.assertEqual(len(paths), sum(event['status'] == 502 for event in events))
                for event in events:
                    self.assertEqual('consumer', event['surface'])
                    self.assertRegex(event['request_id'], r'^[a-f0-9]{32}$')
                    self.assertGreaterEqual(event['duration'], 0)
                    self.assertTrue(set(event).isdisjoint({'request', 'uri', 'args', 'query', 'headers'}))


if __name__ == '__main__':
    unittest.main(verbosity=2)
