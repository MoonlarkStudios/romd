#!/usr/bin/env python3
"""Validate rendered Compose from stdin without echoing secret values."""
import ipaddress
import json
import re
import sys
from urllib.parse import urlsplit


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
            for key in ('Romd__JwtSecret', 'Romd__ConsumerDelivery__SigningSecret'):
                if len(env.get(key, '')) < 32:
                    errors.append(f'{name}: {key} needs at least 32 characters')
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
    passwords = [value for key, value in pg_env.items() if key.endswith('PASSWORD')]
    if len(passwords) != 5 or len(set(passwords)) != 5:
        errors.append('PostgreSQL requires five distinct role passwords')
    if any(not re.fullmatch(r'[0-9a-fA-F]{32,}', value or '') for value in passwords):
        errors.append('PostgreSQL role passwords must be at least 32 hex characters')
    worker_env = services['romd-worker']['environment']
    origins = []
    for surface in ('Admin', 'Consumer'):
        origins.append(worker_env.get(f'Romd__Auth__{surface}Spa__PostLogoutRedirectUris__0', ''))
    origins.append(services['romd-consumer']['environment'].get(
        'Romd__BrowserPlayback__PlayerOrigins__0__Player', ''))
    for origin in origins:
        parsed = urlsplit(origin)
        if parsed.scheme != 'https' or not parsed.hostname or parsed.path or parsed.query or parsed.fragment or parsed.username:
            errors.append('Public URLs must be HTTPS origins without paths or trailing slashes')
    if len(set(origins)) != 3:
        errors.append('Admin, consumer and player must use distinct origins')
    ingress = services.get('ingress')
    if ingress and ingress.get('labels', {}).get('io.romd.ingress-example') == 'true':
        ingress_env = ingress.get('environment', {})
        for surface, origin in zip(('ADMIN', 'CONSUMER', 'PLAYER'), origins):
            host = ingress_env.get(f'ROMD_{surface}_HOST', '')
            if host != urlsplit(origin).hostname or not re.fullmatch(r'[A-Za-z0-9.-]+', host):
                errors.append(f'Ingress {surface} hostname must match its public origin')
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
