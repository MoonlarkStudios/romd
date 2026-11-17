#!/usr/bin/env python3
"""Reviewed, expiring exceptions for source dependency advisories.

An exception never hides a finding and never widens: it names one advisory for one
package version in web/pnpm-lock.yaml and applies only while that version is
provably a development-only dependency reached through the listed tools. The
finding stays in the report with the exception's disposition. If the lockfile
cannot prove development-only use, a production path or an unlisted path appears,
or the exception expires, the finding blocks as any other would.
"""
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import posixpath
import re

from policy import MAX_EXCEPTION_DAYS

EXCEPTIONS_FILE = Path(__file__).with_name('source-advisory-exceptions.json')
FIELDS = ('advisory', 'ecosystem', 'scope', 'package', 'version', 'developmentRoots',
          'owner', 'rationale', 'tracking', 'expiresAt')
SCOPE = 'web/pnpm-lock.yaml'
IMPORTER_KINDS = {'dependencies', 'optionalDependencies', 'devDependencies'}
PRODUCTION_KINDS = ('dependencies', 'optionalDependencies')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def load(path=EXCEPTIONS_FILE):
    """The exception entries, each validated for shape. Expiry is judged when an entry is applied."""
    try:
        document = json.loads(Path(path).read_text())
    except json.JSONDecodeError as error:
        raise ValueError('Advisory exceptions must be JSON') from error
    require(isinstance(document, dict) and set(document) == {'exceptions'} and isinstance(document['exceptions'], list),
            'Advisory exceptions must be one "exceptions" list')
    seen = set()
    for entry in document['exceptions']:
        require(isinstance(entry, dict) and set(entry) == set(FIELDS), 'Each exception needs exactly: ' + ', '.join(FIELDS))
        require(isinstance(entry['advisory'], str)
                and re.fullmatch(r'https://github\.com/advisories/GHSA(-[0-9a-z]{4}){3}', entry['advisory']),
                'Exception requires one exact GitHub advisory URL')
        require(entry['ecosystem'] == 'npm' and entry['scope'] == SCOPE,
                f'Exceptions are supported only for npm packages in {SCOPE}')
        require(isinstance(entry['package'], str) and re.fullmatch(r'(@[a-z0-9._-]+/)?[a-z0-9._-]+', entry['package'])
                and isinstance(entry['version'], str) and re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?', entry['version']),
                'Exception requires an exact package name and version; ranges and wildcards are rejected')
        roots = entry['developmentRoots']
        require(isinstance(roots, list) and roots and len(set(roots)) == len(roots)
                and all(isinstance(root, str) and re.fullmatch(r'(@[a-z0-9._-]+/)?[a-z0-9._-]+', root) for root in roots),
                'Exception requires the development dependencies through which the package is reached')
        require(isinstance(entry['owner'], str) and entry['owner'].strip(), 'Exception requires an accountable owner')
        require(isinstance(entry['rationale'], str) and len(entry['rationale'].strip()) >= 40,
                'Exception requires a specific reachability rationale')
        require(isinstance(entry['tracking'], str) and re.fullmatch(r'https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/(pull|issues)/[0-9]+', entry['tracking']),
                'Exception requires an upstream tracking link')
        require(isinstance(entry['expiresAt'], str) and re.fullmatch(r'[0-9]{4}-[0-9]{2}-[0-9]{2}T00:00:00Z', entry['expiresAt']),
                'Exception expiry must be a UTC date written as YYYY-MM-DDT00:00:00Z')
        expires(entry)
        key = (entry['advisory'], entry['package'], entry['version'])
        require(key not in seen, 'Duplicate advisory exception')
        seen.add(key)
    return document['exceptions']


def expires(entry):
    return datetime.fromisoformat(entry['expiresAt'].replace('Z', '+00:00'))


def pnpm_reachability(lock, package, version):
    """Whether package@version is in any workspace package's production closure, and the
    development dependencies that reach it. Raises when the lockfile cannot establish either."""
    require(isinstance(lock, dict) and str(lock.get('lockfileVersion')) == '9.0', 'Unsupported pnpm lockfile version')
    importers, snapshots = lock.get('importers'), lock.get('snapshots')
    require(isinstance(importers, dict) and importers and isinstance(snapshots, dict), 'pnpm lockfile lacks importers or snapshots')
    base = f'{package}@{version}'
    targets = {key for key in snapshots if key == base or key.startswith(base + '(')}
    require(targets, 'Package version is absent from the pnpm lockfile')

    def closure(start):
        seen, pending = set(), [start]
        while pending:
            key = pending.pop()
            if key in seen:
                continue
            require(key in snapshots, 'pnpm lockfile dependency is unresolved')
            seen.add(key)
            snapshot = snapshots[key] or {}
            require(isinstance(snapshot, dict), 'Invalid pnpm snapshot')
            for kind in PRODUCTION_KINDS:
                edges = snapshot.get(kind) or {}
                require(isinstance(edges, dict), 'Invalid pnpm snapshot dependencies')
                pending.extend(f'{name}@{resolved}' for name, resolved in edges.items())
        return seen

    production, development = False, set()
    for importer, kinds in importers.items():
        require(isinstance(kinds, dict) and set(kinds) <= IMPORTER_KINDS, 'Unrecognized pnpm importer dependency kind')
        for kind, dependencies in kinds.items():
            require(isinstance(dependencies, dict), 'Invalid pnpm importer dependencies')
            for name, resolved in dependencies.items():
                require(isinstance(resolved, dict) and isinstance(resolved.get('version'), str), 'Invalid pnpm importer dependency')
                reference = resolved['version']
                if reference.startswith('link:'):
                    # A workspace package is an importer itself; its own production dependencies are judged there.
                    require(posixpath.normpath(posixpath.join(importer, reference[5:])) in importers,
                            'pnpm workspace link leaves the audited workspace')
                    continue
                if targets & closure(f'{name}@{reference}'):
                    if kind == 'devDependencies':
                        development.add(name)
                    else:
                        production = True
    return production, development


def disposition(entry, lock, now):
    """Why the exception does not apply, or None when it does."""
    if now >= expires(entry):
        return 'expired'
    if expires(entry) - now > timedelta(days=MAX_EXCEPTION_DAYS):
        return f'exceeds the {MAX_EXCEPTION_DAYS}-day limit'
    try:
        production, development = pnpm_reachability(lock, entry['package'], entry['version'])
    except ValueError as error:
        return f'development-only use could not be established ({error})'
    if production:
        return 'the package is a production dependency'
    if not development:
        return 'development-only use could not be established (no development dependency reaches the package)'
    unlisted = sorted(development - set(entry['developmentRoots']))
    if unlisted:
        return 'reached through unreviewed development dependencies: ' + ', '.join(unlisted)
    return None


def apply(findings, lock, exceptions, now=None):
    """Mark each finding as blocking or excepted. An exception that matches nothing must be removed."""
    now = now or datetime.now(timezone.utc)
    used = set()
    for finding in findings:
        finding['blocked'] = True
        for index, entry in enumerate(exceptions):
            if (finding['ecosystem'], finding['scope'], finding['package'], finding['version'], finding['advisory']) != (
                    entry['ecosystem'], entry['scope'], entry['package'], entry['version'], entry['advisory']):
                continue
            used.add(index)
            refusal = disposition(entry, lock, now)
            finding['blocked'] = refusal is not None
            finding['exception'] = dict(status='refused' if refusal else 'applied',
                                        **({'reason': refusal} if refusal else {}),
                                        **{key: entry[key] for key in ('owner', 'rationale', 'tracking', 'expiresAt', 'developmentRoots')})
    require(len(used) == len(exceptions), 'An advisory exception matches no finding; remove it')
    return findings
