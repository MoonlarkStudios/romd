#!/usr/bin/env python3
"""Exercise the shipped tunnel template through real nginx HTTP requests.

Uses one pinned nginx container with no network or published ports. Loopback
source addresses model the pinned connector and an untrusted edge peer; nginx
echo upstreams expose the headers the application actually receives. This is
ingress behavior coverage, not Docker production-image/release qualification.
"""
from contextlib import contextmanager
import hashlib
import json
import re
from pathlib import Path
import select
import socket
import ssl
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
                headers=None, check=True, port=80):
        lines = [f'GET {path} HTTP/1.1', f'Host: {host}', 'Connection: close']
        values = headers.items() if isinstance(headers, dict) else headers or []
        lines += [f'{key}: {value}' for key, value in values]
        # BusyBox nc exits on stdin EOF. Retain its input until the complete
        # Content-Length-delimited response arrives, without a timing sleep.
        output = b''
        with subprocess.Popen(cls.runtime.engine_command('exec', '-i', cls.container,
                              'nc', '-s', source, '-w', '5', '127.0.0.1', str(port)),
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


class BuiltInIngressTests(CloudflareIngressTests):
    """The default ingress: one host name, a listener per surface. TLS is removed from the
    fixture so the pinned image's own client can reach the listeners; routing and headers
    are what this covers."""

    @classmethod
    def setUpClass(cls):
        cls.runtime = Runtime()
        cls.environment = cls.runtime.child_environment()
        temporary = tempfile.TemporaryDirectory(prefix='romd-built-in-ingress-contract-')
        cls.addClassCleanup(temporary.cleanup)
        work = Path(temporary.name)
        template = (ROOT / 'deploy/ingress/templates/default.conf.template').read_text()
        for index, surface in enumerate(('admin', 'consumer', 'player'), 1):
            template = template.replace(f'http://romd-{surface}:8080', f'http://127.0.0.1:{8080 + index}')
        lines = [line.replace(' ssl default_server;', ' default_server;').replace(' ssl;', ';')
                 for line in template.splitlines() if not line.strip().startswith(('ssl_', 'error_page 497'))]
        assert len(lines) == len(template.splitlines()) - 4 and sum(' default_server;' in line for line in lines) == 3
        assert not any(' ssl' in line for line in lines if line.strip().startswith('listen'))
        (work / 'default.conf.template').write_text('\n'.join(lines) + '\n')
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
        return 200 '{surface}|$http_host|$http_x_forwarded_for|$http_x_forwarded_host|$http_x_forwarded_proto|$request_uri';
    }}
}}''' for index, surface in enumerate(('admin', 'consumer', 'player'), 1)))
        cls.container = 'romd-built-in-ingress-contract-' + uuid.uuid4().hex[:12]
        cls.addClassCleanup(cls.remove_container)
        image = json.loads((ROOT / 'scripts/deployment/base-images.json').read_text())['ingress']
        cls.run_engine('run', '--detach', '--name', cls.container, '--network', 'none', '--memory', '96m', '--cpus', '1',
                       '--env', 'ROMD_HOST=nas.example.test', '--env', 'ROMD_CONSUMER_PORT=7663',
                       '--env', 'ROMD_ADMIN_PORT=9443', '--env', 'ROMD_PLAYER_PORT=7665',
                       '--env', 'NGINX_ENVSUBST_FILTER=^ROMD_',
                       '--volume', f'{work / "nginx.conf"}:/etc/nginx/nginx.conf:ro,Z',
                       '--volume', f'{work / "echo.conf"}:/etc/nginx/conf.d/echo.conf:ro,Z',
                       '--volume', f'{work / "default.conf.template"}:/etc/nginx/templates/default.conf.template:ro,Z',
                       image)
        for _ in range(30):
            response = cls.request(host='nas.example.test', port=7663, check=False)
            if response and response[0] == 200:
                break
            time.sleep(0.1)
        else:
            logs = cls.run_engine('logs', cls.container)
            raise AssertionError(f'nginx did not become ready: {logs.stdout}{logs.stderr}')

    def test_each_listener_routes_to_its_surface_with_the_published_origin(self):
        # The admin listener is published on another host port; the application must see that port.
        for port, surface, published in ((7663, 'consumer', 7663), (7664, 'admin', 9443), (7665, 'player', 7665)):
            with self.subTest(surface=surface):
                status, body = self.request(host='nas.example.test', port=port, path='/vfs/a%20b%23c?x=1',
                                            headers={'X-Forwarded-For': '203.0.113.9', 'X-Forwarded-Host': 'evil.test',
                                                     'X-Forwarded-Proto': 'http'})
                self.assertEqual(200, status)
                self.assertEqual([surface, f'nas.example.test:{published}', '127.0.0.2', f'nas.example.test:{published}',
                                  'https', '/vfs/a%20b%23c?x=1'], body.split('|'))

    def test_port_in_the_host_header_cannot_change_the_origin_the_application_sees(self):
        status, body = self.request(host='nas.example.test:1', port=7663)
        self.assertEqual((200, 'nas.example.test:7663'), (status, body.split('|')[1]))

    def test_other_host_names_are_refused_on_every_listener(self):
        for port in (7663, 7664, 7665):
            for host in ('evil.test', 'nas.example.test.evil.test', 'xnas.example.test', '192.0.2.1'):
                with self.subTest(host=host, port=port):
                    self.assertEqual(421, self.request(host=host, port=port)[0])
            # nginx itself rejects a request without a host name before any server is chosen.
            self.assertEqual(400, self.request(host='', port=port)[0])

    def test_host_names_match_without_regard_to_case(self):
        status, body = self.request(host='NAS.Example.TEST', port=7663)
        self.assertEqual((200, 'nas.example.test:7663'), (status, body.split('|')[1]))


# The inherited tunnel cases belong to CloudflareIngressTests alone.
for _name in [name for name in vars(CloudflareIngressTests) if name.startswith('test_')]:
    setattr(BuiltInIngressTests, _name, None)


class BuiltInIngressTlsTests(unittest.TestCase):
    """The shipped template and certificate reloader, unmodified, over real TLS on a loopback port."""

    @classmethod
    def setUpClass(cls):
        cls.runtime = Runtime()
        cls.environment = cls.runtime.child_environment()
        temporary = tempfile.TemporaryDirectory(prefix='romd-built-in-ingress-tls-')
        cls.addClassCleanup(temporary.cleanup)
        cls.work = Path(temporary.name)
        (cls.work / 'tls').mkdir()
        cls.first = cls.issue('first')
        cls.container = 'romd-built-in-ingress-tls-' + uuid.uuid4().hex[:12]
        cls.addClassCleanup(lambda: subprocess.run(cls.runtime.engine_command('rm', '--force', cls.container),
                                                    env=cls.environment, capture_output=True, timeout=30, check=True))
        image = json.loads((ROOT / 'scripts/deployment/base-images.json').read_text())['ingress']
        ingress = ROOT / 'deploy/ingress'
        # Mixed case, as an operator may type it. The upstreams do not exist: only the listeners are exercised.
        subprocess.run(cls.runtime.engine_command(
            'run', '--detach', '--name', cls.container, '--memory', '96m', '--cpus', '1',
            '--publish', '127.0.0.1::7663', '--env', 'ROMD_HOST=NAS.example.test',
            '--env', 'ROMD_CONSUMER_PORT=7663', '--env', 'ROMD_ADMIN_PORT=7664', '--env', 'ROMD_PLAYER_PORT=7665',
            '--env', 'ROMD_CERTIFICATE_CHECK_SECONDS=1', '--env', 'NGINX_ENVSUBST_FILTER=^ROMD_',
            '--volume', f'{ingress / "templates"}:/etc/nginx/templates:ro,Z',
            '--volume', f'{ingress / "entrypoint/40-romd-reload-certificate.sh"}:/docker-entrypoint.d/40-romd-reload-certificate.sh:ro,Z',
            '--volume', f'{cls.work / "tls"}:/etc/romd/tls:ro,Z', image),
            env=cls.environment, capture_output=True, text=True, timeout=60, check=True)
        published = subprocess.run(cls.runtime.engine_command('port', cls.container, '7663/tcp'), env=cls.environment,
                                   capture_output=True, text=True, timeout=30, check=True).stdout.split()[0]
        cls.port = int(published.rsplit(':', 1)[1])
        for _ in range(100):
            if cls.served() == cls.first:
                break
            time.sleep(0.1)
        else:
            raise AssertionError('The ingress did not start serving its certificate')

    @classmethod
    def issue(cls, name):
        """Replace the certificate files as romd-init does, and return the new certificate's DER SHA-256."""
        for suffix in ('key', 'crt'):
            (cls.work / f'{name}.{suffix}').unlink(missing_ok=True)
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'ec', '-pkeyopt', 'ec_paramgen_curve:P-256', '-nodes',
                        '-keyout', cls.work / f'{name}.key', '-out', cls.work / f'{name}.crt', '-days', '2',
                        '-subj', f'/CN={name}', '-addext', 'subjectAltName=DNS:nas.example.test'],
                       capture_output=True, check=True)
        for source, target in ((f'{name}.key', 'privkey.pem'), (f'{name}.crt', 'fullchain.pem')):
            (cls.work / source).chmod(0o644)
            (cls.work / source).replace(cls.work / 'tls' / target)
        der = ssl.PEM_cert_to_DER_cert((cls.work / 'tls/fullchain.pem').read_text())
        return hashlib.sha256(der).hexdigest()

    @classmethod
    def connection(cls, host):
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.check_hostname, context.verify_mode = False, ssl.CERT_NONE
        return context.wrap_socket(socket.create_connection(('127.0.0.1', cls.port), timeout=5), server_hostname=host)

    @classmethod
    def served(cls):
        try:
            with cls.connection('nas.example.test') as connection:
                return hashlib.sha256(connection.getpeercert(binary_form=True)).hexdigest()
        except OSError:
            return None

    def status(self, host, plain=False):
        request = f'GET /x HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n'.encode()
        with (socket.create_connection(('127.0.0.1', self.port), timeout=5) if plain else self.connection(host)) as connection:
            connection.sendall(request)
            connection.settimeout(3)
            response = b''
            try:
                while chunk := connection.recv(65536):
                    response += chunk
            except TimeoutError:
                # The request was accepted and is waiting on an upstream that does not exist here.
                return None, None
        head = response.decode(errors='replace').split('\r\n\r\n')[0].split('\r\n')
        location = next((line.split(': ', 1)[1] for line in head if line.lower().startswith('location:')), None)
        return int(head[0].split()[1]), location

    def test_configured_name_is_served_in_any_case_and_other_names_are_refused(self):
        # No upstream exists here, so a routed request waits or fails as a gateway error; a refused one is 421.
        for host in ('nas.example.test', 'NAS.example.test', 'Nas.Example.Test'):
            with self.subTest(host=host):
                self.assertIn(self.status(host)[0], (None, 502, 504))
        for host in ('evil.test', 'nas.example.test.evil.test', '203.0.113.7'):
            with self.subTest(host=host):
                self.assertEqual(421, self.status(host)[0])

    def test_plain_http_is_redirected_to_https_on_the_published_port(self):
        self.assertEqual((308, 'https://NAS.example.test:7663/x'), self.status('nas.example.test', plain=True))

    def test_replaced_certificate_is_served_without_restarting_and_a_broken_one_is_not(self):
        started = subprocess.run(self.runtime.engine_command('inspect', '--format', '{{.State.StartedAt}}', self.container),
                                 env=self.environment, capture_output=True, text=True, timeout=30, check=True).stdout
        renewed = self.issue('renewed')
        self.assertNotEqual(self.first, renewed)
        deadline = time.monotonic() + 30
        while self.served() != renewed and time.monotonic() < deadline:
            time.sleep(0.2)
        self.assertEqual(renewed, self.served(), 'the renewed certificate was not served')
        # A key that does not match its certificate must not take the listeners down.
        (self.work / 'tls/privkey.pem').write_text('not a key\n')
        (self.work / 'tls/fullchain.pem').write_text((self.work / 'tls/fullchain.pem').read_text() + '\n')
        time.sleep(3)
        self.assertEqual(renewed, self.served())
        self.assertEqual(started, subprocess.run(
            self.runtime.engine_command('inspect', '--format', '{{.State.StartedAt}}', self.container),
            env=self.environment, capture_output=True, text=True, timeout=30, check=True).stdout)


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
