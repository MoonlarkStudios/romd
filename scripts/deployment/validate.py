#!/usr/bin/env python3
"""Validate rendered Compose from stdin without echoing secret values."""
import ipaddress
import json
import re
import sys
from urllib.parse import urlsplit


def import_mount(service, root):
    """The (source, target) of the innermost mount containing an import root, or None."""
    held = [(volume.get('source'), volume.get('target')) for volume in service.get('volumes', [])
            if isinstance(volume, dict) and isinstance(volume.get('target'), str)
            and (root == volume['target'] or root.startswith(volume['target'].rstrip('/') + '/'))]
    return max(held, key=lambda mount: len(mount[1]), default=None)


def validate(config):
    errors = []
    services = config['services']
    required = ('postgres', 'romd-worker', 'romd-admin', 'romd-consumer', 'romd-player')
    for name in required:
        if name not in services:
            errors.append(f'Missing service: {name}')
    if errors:
        return errors
    if services['romd-worker'].get('init') is not False or services['romd-worker'].get('pid'):
        errors.append('romd-worker: run as namespace PID 1 with init: false and a private PID namespace for media recovery')
    postgres_networks = set(services['postgres'].get('networks', {}))
    for name in required:
        service = services[name]
        if 'container_name' in service:
            errors.append(f'{name}: use project-scoped names, not container_name')
        if not service.get('healthcheck'):
            errors.append(f'{name}: healthcheck required')
        if name in ('postgres', 'romd-worker') and service.get('ports'):
            errors.append(f'{name}: must not publish ports')
        if name in ('romd-worker', 'romd-admin', 'romd-consumer'):
            if not postgres_networks.intersection(service.get('networks', {})):
                errors.append(f'{name}: no shared network with PostgreSQL')
            env = service.get('environment', {})
            # romd-init generates every secret; none may pass through the environment.
            if (env.get('ROMD_SECRETS_DIRECTORY') != '/var/lib/romd/secrets'
                    or any(key in env for key in ('Romd__JwtSecret', 'Romd__ConsumerDelivery__SigningSecret'))):
                errors.append(f'{name}: signing secrets must be read from /var/lib/romd/secrets')
            if any('Password=' in value or 'Passfile=/etc/romd/database/pgpass' not in value
                   for key, value in env.items() if key.startswith('ConnectionStrings__')):
                errors.append(f'{name}: connection strings must use the generated password file')
            secret_volumes = {volume.get('source'): volume for volume in service.get('volumes', [])
                              if isinstance(volume, dict) and str(volume.get('source', '')).startswith('romd-secrets-')}
            own = secret_volumes.get('romd-secrets-' + name.removeprefix('romd-'), {})
            if len(secret_volumes) != 1 or own.get('target') != '/etc/romd/database' or own.get('read_only') is not True:
                errors.append(f'{name}: mount only its own password volume, read-only')
            if not env.get('Romd__ConsumerDelivery__SigningKeyId', '').strip():
                errors.append(f'{name}: consumer delivery signing key ID is required')
            ttl = env.get('Romd__ConsumerDelivery__SignedUrlTtlMinutes', '')
            if not re.fullmatch(r'[0-9]+', ttl) or not 1 <= int(ttl) <= 2147483647:
                errors.append(f'{name}: consumer delivery signed URL lifetime must be a positive integer')
        if name in ('romd-admin', 'romd-consumer'):
            env = service['environment']
            if any('Provisioning' in key for key in env):
                errors.append(f'{name}: provisioning credentials are worker-only')
            try:
                ipaddress.ip_address(env.get('Romd__ForwardedHeaders__KnownProxies__0', ''))
            except ValueError:
                errors.append(f'{name}: configure an explicit trusted proxy IP')
    if not services['romd-admin']['environment'].get('ASPNETCORE_TEMP', '').startswith('/var/lib/romd/temp/'):
        errors.append('romd-admin: ASPNETCORE_TEMP must buffer uploads under /var/lib/romd/temp/')
    pg_env = services['postgres']['environment']
    vfs = services['romd-consumer']['environment']
    if vfs.get('Romd__Vfs__Enabled', 'false').lower() not in ('true', 'false'):
        errors.append('romd-consumer: VFS enabled must be true or false')
    for key, default, maximum in [('MaximumConcurrentRequests', 8, 128),
                                   ('MaximumConcurrentRequestsPerUser', 4, 128),
                                   ('MaximumDirectoryEntries', 20000, 100000)]:
        value = vfs.get('Romd__Vfs__' + key, str(default))
        if not re.fullmatch(r'[0-9]+', value) or not 1 <= int(value) <= maximum:
            errors.append(f'romd-consumer: VFS {key} must be an integer from 1 to {maximum}')
    if (pg_env.get('POSTGRES_PASSWORD_FILE') != '/run/romd-postgres/bootstrap'
            or pg_env.get('ROMD_POSTGRES_SECRETS_DIRECTORY') != '/run/romd-postgres'
            or any(key.endswith('PASSWORD') for key in pg_env)):
        errors.append('PostgreSQL must read its role passwords from the generated password files')
    init = services.get('romd-init', {})
    init_targets = {volume.get('target'): volume.get('source') for volume in init.get('volumes', []) if isinstance(volume, dict)}
    # The signing secrets live in the data directory, so init must write the one the hosts mount.
    data = init_targets.pop('/init/data', None)
    certificate = init_targets.pop('/init/tls', None)
    if ((init.get('command') or [])[:2] != ['--init-deployment', '/init'] or init.get('network_mode') != 'none'
            or init_targets != {'/init/postgres': 'romd-secrets-postgres', '/init/worker': 'romd-secrets-worker',
                                '/init/admin': 'romd-secrets-admin', '/init/consumer': 'romd-secrets-consumer',
                                '/init/ca': 'romd-ca'}
            or not data or not certificate
            or any(next((volume.get('source') for volume in services[name].get('volumes', [])
                         if isinstance(volume, dict) and volume.get('target') == '/var/lib/romd'), None) != data
                   for name in ('romd-worker', 'romd-admin', 'romd-consumer'))
            or services['postgres'].get('depends_on', {}).get('romd-init', {}).get('condition') != 'service_completed_successfully'):
        errors.append('romd-init: must generate every secret, offline, before PostgreSQL starts')
    worker_env = services['romd-worker']['environment']
    origins = []
    for surface in ('Admin', 'Consumer'):
        origins.append(worker_env.get(f'Romd__Auth__{surface}Spa__PostLogoutRedirectUris__0', ''))
    origins.append(services['romd-consumer']['environment'].get(
        'Romd__BrowserPlayback__PlayerOrigins__0__Player', ''))
    for origin in origins:
        try:
            parsed = urlsplit(origin)
            # Browsers send origins in lower case and the applications compare them exactly.
            if (parsed.scheme != 'https' or not parsed.hostname or parsed.path or parsed.query
                    or parsed.fragment or parsed.username or parsed.port == 443 or origin != origin.lower()):
                errors.append('Public URLs must be canonical lower-case HTTPS origins without paths, '
                              'trailing slashes or explicit default ports')
        except ValueError:
            errors.append('Public URLs must be valid HTTPS origins')
    if len(set(origins)) != 3:
        errors.append('Admin, consumer and player must use distinct origins')
    ingress = services.get('ingress')
    if ingress:
        ingress_env = ingress.get('environment', {})
        if ingress.get('labels', {}).get('io.romd.ingress-example') == 'true':
            for surface, origin in zip(('ADMIN', 'CONSUMER', 'PLAYER'), origins):
                host = ingress_env.get(f'ROMD_{surface}_HOST', '')
                if host != urlsplit(origin).hostname or not re.fullmatch(r'[A-Za-z0-9.-]+', host):
                    errors.append(f'Ingress {surface} hostname must match its public origin')
        else:
            # Built-in ingress: one host name, and each surface on the port its listener is published at.
            published = {port.get('target'): str(port.get('published')) for port in ingress.get('ports', [])
                         if isinstance(port, dict)}
            for surface, target, origin in zip(('ADMIN', 'CONSUMER', 'PLAYER'), (7664, 7663, 7665), origins):
                try:
                    parsed = urlsplit(origin)
                    port = str(parsed.port or 443)
                    if (parsed.hostname != ingress_env.get('ROMD_HOST') or port != ingress_env.get(f'ROMD_{surface}_PORT')
                            or published.get(target) != port):
                        errors.append(f'Ingress {surface} origin must be ROMD_HOST on its published port')
                except ValueError:
                    errors.append(f'Ingress {surface} origin must be ROMD_HOST on its published port')
            if set(published) != {7663, 7664, 7665}:
                errors.append('Built-in ingress publishes exactly its three surface listeners')
        try:
            proxy = ipaddress.ip_address(services['romd-admin']['environment']['Romd__ForwardedHeaders__KnownProxies__0'])
            assigned = (ingress.get('networks', {}).get('edge') or {}).get('ipv4_address')
            if str(proxy) != assigned or any(
                    services[name]['environment']['Romd__ForwardedHeaders__KnownProxies__0'] != assigned
                    for name in ('romd-admin', 'romd-consumer')):
                errors.append('Ingress static IP must match the API trusted proxy')
            for network in config.get('networks', {}).get('edge', {}).get('ipam', {}).get('config', []):
                if proxy not in ipaddress.ip_network(network['subnet']):
                    errors.append('Ingress IP is outside the edge subnet')
                if 'ip_range' in network and proxy in ipaddress.ip_network(network['ip_range']):
                    errors.append('Ingress IP must be outside the dynamic allocation range')
            addresses = [str(proxy)]
            for name in ('romd-admin', 'romd-consumer', 'romd-player'):
                address = ipaddress.ip_address(services[name]['networks']['edge']['ipv4_address'])
                addresses.append(str(address))
                for network in config['networks']['edge']['ipam']['config']:
                    if address not in ipaddress.ip_network(network['subnet']) or (
                            'ip_range' in network and address in ipaddress.ip_network(network['ip_range'])):
                        errors.append(f'{name}: reserve a static edge address outside the dynamic range')
            if len(set(addresses)) != len(addresses):
                errors.append('Ingress and API/player static addresses must be distinct')
            if 'cloudflared' in services:
                connector = services['cloudflared']
                address = ipaddress.IPv4Address(ingress_env.get('ROMD_CLOUDFLARED_EDGE_IP', ''))
                if (connector.get('networks', {}).get('edge') or {}).get('ipv4_address') != str(address):
                    errors.append('Cloudflared static IP must match the ingress trusted connector')
                if str(address) in addresses:
                    errors.append('Cloudflared static IP must differ from ingress and API/player addresses')
                networks = config['networks']['edge']['ipam']['config']
                if len(networks) != 1:
                    errors.append('Cloudflared needs one explicit IPv4 edge subnet')
                for network in networks:
                    subnet = ipaddress.IPv4Network(network['subnet'])
                    gateway = ipaddress.IPv4Address(network.get('gateway', str(subnet.network_address + 1)))
                    if address not in subnet or address in (subnet.network_address, subnet.broadcast_address, gateway):
                        errors.append('Cloudflared needs a usable static IP inside the edge subnet')
                    if 'ip_range' in network and address in ipaddress.ip_network(network['ip_range']):
                        errors.append('Cloudflared static IP must be outside the dynamic allocation range')
                if set(connector.get('networks', {})) != {'edge'}:
                    errors.append('Cloudflared must join only the edge network')
                connector_ports = connector.get('ports', [])
                if not isinstance(connector_ports, list) or connector_ports:
                    errors.append('Cloudflared must not publish ports')
                ingress_ports = ingress.get('ports', [])
                if not isinstance(ingress_ports, list) or any(
                        not isinstance(port, dict) or type(port.get('target')) is not int
                        or not 1 <= port['target'] <= 65535 for port in ingress_ports):
                    errors.append('Ingress published ports need normalized valid target mappings')
                elif any(port['target'] == 80 for port in ingress_ports):
                    errors.append('Cloudflare tunnel listener must not publish port 80')
        except (ValueError, KeyError, TypeError):
            errors.append('Ingress and connector need valid static IPs and network configuration')
    roots = {}
    for name in ('romd-admin', 'romd-worker'):
        environment = services[name].get('environment', {})
        roots[name] = sorted(value for key, value in environment.items() if key.startswith('Romd__AllowedImportPaths__'))
    if roots['romd-admin'] != roots['romd-worker']:
        errors.append('Path import roots must be identical on romd-admin and romd-worker')
    else:
        for root in roots['romd-admin']:
            # The worker deletes move-mode originals by the path admin staged, so the
            # mount holding each root must be the same source at the same target on both.
            holders = [import_mount(services[name], root) for name in ('romd-admin', 'romd-worker')]
            if holders[0] is None or holders[0] != holders[1]:
                errors.append(f'Path import root {root} must be inside the same mount on romd-admin and romd-worker')
    for name in ('ingress', 'cloudflared'):
        # The examples default to reviewed pins; an override must stay immutable too.
        if name in services and not re.fullmatch(r'[^\s@]+@sha256:[0-9a-f]{64}', str(services[name].get('image', ''))):
            errors.append(f'{name}: image must be pinned by digest')
    player = services['romd-player']
    if player.get('volumes') or postgres_networks.intersection(player.get('networks', {})):
        errors.append('Player must be isolated from database and persistent volumes')
    if any(key.startswith(('ConnectionStrings__', 'Romd__')) for key in player.get('environment', {})):
        errors.append('Player must not receive ROMD credentials')
    return errors


if __name__ == '__main__':
    failures = validate(json.load(sys.stdin))
    for failure in failures:
        print(f'error: {failure}', file=sys.stderr)
    if failures:
        sys.exit(1)
    print('Production Compose configuration validated.')
