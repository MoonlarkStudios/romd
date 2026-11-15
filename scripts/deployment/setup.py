#!/usr/bin/env python3
"""Create a new installation's .env: ask for the few choices a person must make
and generate every secret.

Run from an extracted release with `scripts/deployment/setup.sh`. It never
overwrites an existing env file and never prints a secret. Answers can also be
given as options for a scripted install; secrets are only read from hidden
prompts or files, never from the command line.
"""
import argparse
import base64
import getpass
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
INGRESS = {
    'cloudflare': ('Cloudflare Tunnel (recommended: no certificates or open ports)',
                   'deploy/examples/cloudflare/compose.yaml'),
    'https': ('HTTPS with your own certificate (nginx terminates TLS on port 443)',
              'deploy/examples/https/compose.yaml'),
    'proxy': ('An existing reverse proxy you run yourself', None),
}
LIBRARY_OVERLAY = 'deploy/examples/library/compose.yaml'
HOSTNAME = re.compile(r'(?=.{1,253}$)(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))+')
EMAIL = re.compile(r'[^@\s]+@[^@\s]+\.[^@\s]+')
DATABASE_ROLES = ('BOOTSTRAP', 'PROVISIONER', 'WORKER', 'ADMIN', 'CONSUMER')
# Tried in order; the first /24 that no existing Docker network overlaps is used.
EDGE_SUBNETS = tuple(f'172.{second}.0.0/24' for second in range(28, 32)) + tuple(
    f'10.254.{third}.0/24' for third in range(0, 8))


class SetupError(Exception):
    pass


def secret():
    # Hex keeps every secret safe inside connection strings and env files.
    return secrets.token_hex(32)


def password_problem(value):
    if len(value) < 12:
        return 'Use at least 12 characters.'
    if not (re.search('[a-z]', value) and re.search('[A-Z]', value) and re.search('[0-9]', value)):
        return 'Include an upper-case letter, a lower-case letter and a digit.'
    if any(character.isspace() for character in value):
        return 'Leave out spaces.'
    return None


def tunnel_token(text):
    """Accept the token, or the whole install command Cloudflare shows around it."""
    fields = text.split()
    return fields[-1] if fields else ''


def looks_like_tunnel_token(token):
    try:
        decoded = json.loads(base64.b64decode(token + '=' * (-len(token) % 4), validate=True))
    except (ValueError, TypeError):
        return False
    return isinstance(decoded, dict) and {'a', 't', 's'} <= set(decoded)


def docker_subnets(run=subprocess.run):
    """Existing Docker network subnets, or None when Docker cannot be asked."""
    if not shutil.which('docker'):
        return None
    try:
        names = run(['docker', 'network', 'ls', '--quiet'], capture_output=True, text=True, timeout=20, check=True).stdout.split()
        if not names:
            return []
        output = run(['docker', 'network', 'inspect', '--format', '{{range .IPAM.Config}}{{.Subnet}} {{end}}', *names],
                     capture_output=True, text=True, timeout=20, check=True).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    subnets = []
    for value in output.split():
        try:
            subnets.append(ipaddress.ip_network(value, strict=False))
        except ValueError:
            continue
    return subnets


def edge_network(existing):
    """Static ingress addresses inside one free /24, with dynamic allocation in its upper half."""
    for candidate in EDGE_SUBNETS:
        subnet = ipaddress.ip_network(candidate)
        if not any(subnet.overlaps(other) for other in existing or () if other.version == 4):
            break
    else:
        raise SetupError('Every candidate edge subnet overlaps an existing Docker network; '
                         'set ROMD_EDGE_SUBNET and the related addresses in .env by hand.')
    host = list(subnet.hosts())
    return {'ROMD_EDGE_SUBNET': str(subnet), 'ROMD_EDGE_DYNAMIC_RANGE': str(list(subnet.subnets(new_prefix=25))[1]),
            'ROMD_TRUSTED_PROXY': str(host[1]), 'ROMD_ADMIN_EDGE_IP': str(host[2]),
            'ROMD_CONSUMER_EDGE_IP': str(host[3]), 'ROMD_PLAYER_EDGE_IP': str(host[4]),
            'ROMD_CLOUDFLARED_EDGE_IP': str(host[5])}


class Prompter:
    def __init__(self, interactive, ask=input, hidden=getpass.getpass, say=print):
        self.interactive, self.ask, self.hidden, self.say = interactive, ask, hidden, say

    def value(self, label, given=None, default=None, check=None):
        if given is not None:
            problem = check(given) if check else None
            if problem:
                raise SetupError(f'{label}: {problem}')
            return given
        if not self.interactive:
            if default is not None:
                return default
            raise SetupError(f'{label} is required')
        while True:
            answer = self.ask(f'{label}{f" [{default}]" if default else ""}: ').strip() or (default or '')
            problem = check(answer) if check else (None if answer else 'A value is required.')
            if not problem:
                return answer
            self.say(f'  {problem}')

    def choice(self, label, options, given=None):
        if given is not None:
            return given
        if not self.interactive:
            raise SetupError(f'{label} is required')
        keys = list(options)
        self.say(label)
        for index, key in enumerate(keys, 1):
            self.say(f'  {index}. {options[key]}')
        while True:
            answer = self.ask(f'Choose 1-{len(keys)} [1]: ').strip() or '1'
            if answer.isdigit() and 1 <= int(answer) <= len(keys):
                return keys[int(answer) - 1]
            self.say(f'  Enter a number from 1 to {len(keys)}.')

    def confirm(self, label, default=True):
        if not self.interactive:
            return default
        answer = self.ask(f'{label} [{"Y/n" if default else "y/N"}]: ').strip().lower()
        return default if not answer else answer in ('y', 'yes')

    def secret(self, label, path=None, check=None, confirm=False):
        if path is not None:
            value = Path(path).read_text().strip()
            problem = check(value) if check else (None if value else 'The file is empty.')
            if problem:
                raise SetupError(f'{label}: {problem}')
            return value
        if not self.interactive:
            raise SetupError(f'{label} is required (give it in a file)')
        while True:
            value = self.hidden(f'{label} (input hidden): ').strip()
            problem = check(value) if check else (None if value else 'A value is required.')
            if problem:
                self.say(f'  {problem}')
                continue
            if confirm and self.hidden('Repeat it: ').strip() != value:
                self.say('  The two entries differ.')
                continue
            return value


def hostname_problem(value):
    return None if HOSTNAME.fullmatch(value.lower()) else 'Enter a DNS name such as example.com, without https:// or a path.'


def collect(args, prompt, existing_subnets):
    values = {}
    prompt.say('ROMD serves three sites: the admin portal, the console portal and a separate')
    prompt.say('player. Each needs its own HTTPS hostname under one domain you control.\n')
    domain = prompt.value('Domain', args.domain, check=hostname_problem).lower()
    hosts = {key: (given or f'{prefix}.{domain}').lower() for key, given, prefix in (
        ('ADMIN', args.admin_host, 'admin'), ('CONSUMER', args.consumer_host, 'console'), ('PLAYER', args.player_host, 'play'))}
    if not (args.admin_host or args.consumer_host or args.player_host):
        prompt.say(f'\n  Admin portal:   https://{hosts["ADMIN"]}\n  Console portal: https://{hosts["CONSUMER"]}'
                   f'\n  Player:         https://{hosts["PLAYER"]}')
        if not prompt.confirm('Use these hostnames?'):
            for key, label in (('ADMIN', 'Admin portal hostname'), ('CONSUMER', 'Console portal hostname'),
                               ('PLAYER', 'Player hostname')):
                hosts[key] = prompt.value(label, default=hosts[key], check=hostname_problem).lower()
    for key, host in hosts.items():
        if problem := hostname_problem(host):
            raise SetupError(f'{key.lower()} hostname: {problem}')
    if len(set(hosts.values())) != 3:
        raise SetupError('The admin, console and player hostnames must all differ.')
    for key, host in hosts.items():
        values[f'ROMD_{key}_PUBLIC_URL'] = f'https://{host}'
        values[f'ROMD_{key}_HOST'] = host

    prompt.say('\nThe first administrator signs in with this email and password.')
    values['ROMD_DEFAULT_ADMIN_EMAIL'] = prompt.value(
        'Administrator email', args.admin_email,
        check=lambda value: None if EMAIL.fullmatch(value) else 'Enter an email address.')
    values['ROMD_DEFAULT_ADMIN_PASSWORD'] = prompt.secret(
        'Administrator password', args.admin_password_file, check=password_problem, confirm=True)

    prompt.say('')
    ingress = prompt.choice('How will visitors reach ROMD?', {key: label for key, (label, _) in INGRESS.items()},
                            args.ingress)
    overlays = ['compose.yaml'] + ([INGRESS[ingress][1]] if INGRESS[ingress][1] else [])
    if ingress in ('cloudflare', 'https'):
        if existing_subnets is None:
            prompt.say('  Docker networks could not be listed; using the default edge subnet.')
        values.update(edge_network(existing_subnets))
    if ingress == 'cloudflare':
        prompt.say('\nCreate a tunnel in the Cloudflare dashboard (Zero Trust > Networks > Tunnels) and')
        prompt.say('copy its token. Pasting the whole install command it shows also works.')
        token = tunnel_token(prompt.secret('Tunnel token', args.tunnel_token_file))
        if not looks_like_tunnel_token(token):
            prompt.say('  Warning: that does not look like a Cloudflare tunnel token. Check it if the tunnel stays down.')
        values['CLOUDFLARE_TUNNEL_TOKEN'] = token
    elif ingress == 'https':
        def tls_problem(value):
            path = Path(value)
            if not path.is_absolute():
                return 'Enter an absolute path.'
            missing = [name for name in ('fullchain.pem', 'privkey.pem') if not (path / name).is_file()]
            return f'Missing {" and ".join(missing)} in that directory.' if missing else None
        prompt.say('\nThe certificate must cover all three hostnames.')
        values['ROMD_TLS_DIRECTORY'] = prompt.value('Directory holding fullchain.pem and privkey.pem',
                                                    args.tls_directory, check=tls_problem)
        values['ROMD_HTTPS_BIND'] = prompt.value(
            'Address to listen on (0.0.0.0 for every interface)', args.https_bind, default='0.0.0.0',
            check=lambda value: None if _ip(value) else 'Enter an IP address.')
    else:
        prompt.say('\nROMD trusts forwarded client addresses from exactly one proxy. Enter that')
        prompt.say("proxy's IP address as the ROMD containers see it.")
        values['ROMD_TRUSTED_PROXY'] = prompt.value(
            'Proxy IP address', args.trusted_proxy, check=lambda value: None if _ip(value) else 'Enter an IP address.')

    prompt.say('\nOptional: an existing ROM library on this server, for importing in place.')
    prompt.say('ROMD mounts it read-only and copies what you import. Leave empty to skip.')
    def library_problem(value):
        if not value:
            return None
        path = Path(value)
        if not path.is_absolute():
            return 'Enter an absolute path.'
        return None if path.is_dir() else 'That directory does not exist.'
    library = prompt.value('Library directory', args.library, default='', check=library_problem)
    if library:
        values['ROMD_LIBRARY_PATH'] = library
        overlays.append(LIBRARY_OVERLAY)

    values['COMPOSE_PROJECT_NAME'] = args.project
    values['COMPOSE_FILE'] = ':'.join(overlays)
    values['ROMD_JWT_SECRET'] = secret()
    values['ROMD_CONSUMER_DELIVERY_SIGNING_SECRET'] = secret()
    for role in DATABASE_ROLES:
        values[f'ROMD_POSTGRES_{role}_PASSWORD'] = secret()
    return ingress, values


def _ip(value):
    try:
        return ipaddress.ip_address(value)
    except ValueError:
        return None


def render(template, values):
    """Fill the template's assignments in place, keeping its comments and order."""
    remaining, lines = dict(values), []
    for line in template.splitlines():
        key = line.split('=', 1)[0] if re.match(r'[A-Z][A-Z0-9_]*=', line) else None
        if key in remaining:
            line = f'{key}={remaining.pop(key)}'
        lines.append(line)
    if remaining:
        lines += ['', '# Added by setup.sh'] + [f'{key}={value}' for key, value in remaining.items()]
    for key, value in values.items():
        if '\n' in value or (value != value.strip()):
            raise SetupError(f'{key} cannot contain line breaks or surrounding spaces')
    return '\n'.join(lines) + '\n'


def write_private(path, text):
    # O_EXCL refuses an existing file even if one appears while setup runs.
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, 'w') as stream:
        stream.write(text)
    os.chmod(path, 0o600)


def next_steps(ingress, values):
    hosts = [values[f'ROMD_{key}_HOST'] for key in ('ADMIN', 'CONSUMER', 'PLAYER')]
    steps = ['Next steps:']
    if ingress == 'cloudflare':
        steps += ['  1. In your tunnel, add a public hostname for each of these, with service',
                  '     http://ingress:80:'] + [f'       {host}' for host in hosts]
    elif ingress == 'https':
        steps += ['  1. Point DNS for these hostnames at this server and allow port 443:'] + [
            f'       {host}' for host in hosts]
    else:
        steps += ['  1. Connect your proxy to the "edge" network or the published ports, forward',
                  '     each hostname to its service, and follow "Existing reverse proxy" in',
                  '     docs/production-deployment.md.']
    steps += ['  2. Check the configuration:   scripts/deployment/validate.sh',
              '  3. Start ROMD:                scripts/deployment/compose.sh pull',
              '                                scripts/deployment/compose.sh up -d --wait --wait-timeout 240',
              '  4. Confirm it is healthy:     scripts/deployment/verify.sh',
              f'  5. Sign in at https://{hosts[0]} as {values["ROMD_DEFAULT_ADMIN_EMAIL"]}.',
              '',
              'The administrator password in .env is only used to create the first account.',
              'Changing it there later has no effect; change it in the admin portal instead.']
    return '\n'.join(steps)


def parser():
    options = argparse.ArgumentParser(description=__doc__.split('\n\n')[0],
                                      formatter_class=argparse.RawDescriptionHelpFormatter)
    options.add_argument('--non-interactive', action='store_true', help='fail instead of prompting for missing answers')
    options.add_argument('--domain')
    options.add_argument('--admin-host')
    options.add_argument('--consumer-host')
    options.add_argument('--player-host')
    options.add_argument('--admin-email')
    options.add_argument('--admin-password-file', help='read the first administrator password from this file')
    options.add_argument('--ingress', choices=sorted(INGRESS))
    options.add_argument('--tunnel-token-file', help='read the Cloudflare tunnel token from this file')
    options.add_argument('--tls-directory')
    options.add_argument('--https-bind')
    options.add_argument('--trusted-proxy')
    options.add_argument('--library', help='existing ROM library directory to mount read-only')
    options.add_argument('--project', default='romd', help='Compose project name (default: romd)')
    return options


def main(argv=None, ask=input, hidden=getpass.getpass, say=print, subnets=docker_subnets, terminal=sys.stdin.isatty):
    args = parser().parse_args(argv)
    # A release keeps the production template at its root. A source checkout keeps it
    # under deploy/, and its root .env belongs to development, so a checkout needs an
    # explicit ROMD_ENV_FILE target.
    checkout = (ROOT / 'deploy/.env.example').is_file()
    template = ROOT / ('deploy/.env.example' if checkout else '.env.example')
    target = Path(os.environ.get('ROMD_ENV_FILE') or ROOT / '.env')
    try:
        if checkout and not os.environ.get('ROMD_ENV_FILE'):
            raise SetupError('Run setup from an extracted ROMD release, or set ROMD_ENV_FILE to the file to create.')
        if not template.is_file():
            raise SetupError('Run setup from an extracted ROMD release.')
        if target.exists():
            raise SetupError(f'{target} already exists. Setup never replaces it; move it aside to start over.')
        if not re.fullmatch(r'[a-z0-9][a-z0-9_-]*', args.project):
            raise SetupError('The project name may contain lower-case letters, digits, "-" and "_".')
        if not args.non_interactive and not terminal():
            raise SetupError('Run setup in a terminal, or pass --non-interactive with every answer.')
        prompt = Prompter(not args.non_interactive, ask, hidden, say)
        ingress, values = collect(args, prompt, subnets())
        write_private(target, render(template.read_text(), values))
    except SetupError as error:
        say(f'setup: {error}')
        return 1
    except (KeyboardInterrupt, EOFError):
        say('\nsetup: cancelled; nothing was written.')
        return 1
    say(f'\nWrote {target} (readable only by you). Keep it out of version control.\n')
    say(next_steps(ingress, values))
    return 0


if __name__ == '__main__':
    sys.exit(main())
