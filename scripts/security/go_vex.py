#!/usr/bin/env python3
"""govulncheck VEX for the packaged catalog reader, in the form Trivy applies.

The catalog reader is ROMD's only Go binary. Trivy reports every advisory for
each module and standard library version linked into it, including advisories
whose vulnerable code the reader does not contain. govulncheck's binary mode
checks the reader's actual symbols and emits OpenVEX, marking those advisories
`not_affected` / `vulnerable_code_not_present`, and Trivy suppresses exactly
those findings with `--vex`. Any other status blocks, whatever its severity.

Trivy needs two normalizations to match govulncheck's statements: govulncheck
names an "Unknown Product" with each package as a subcomponent, and
percent-encodes module paths. Here each statement names its decoded package
URLs directly as products.
"""
import json
import os
from pathlib import Path
import platform
import re
from urllib.parse import unquote

DB = 'https://vuln.go.dev'
OPENVEX = 'https://openvex.dev/ns/v0.2.0'
READER = 'app/catalog-reader'
EXPORT = ('catalog-reader', 'catalog-reader.json', 'govulncheck')
NOT_BLOCKING = {('not_affected', 'vulnerable_code_not_present'), ('fixed', None)}
TRUST_MODULES = ('github.com/sigstore/', 'github.com/theupdateframework/')


def pin(root):
    """Return the govulncheck pin after checking that the Dockerfile builds exactly that version."""
    tool = json.loads((root / 'scripts/security/tools.json').read_text())['govulncheck']
    text = (root / 'Dockerfile').read_text()
    if (tool.get('module') != 'golang.org/x/vuln' or not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+', tool.get('version', ''))
            or not all(tool.get(key) and tool[key] in text for key in ('version', 'goSum', 'goModSum', 'sourceZipSha256'))):
        raise ValueError('Dockerfile govulncheck pins differ from scripts/security/tools.json')
    return tool


def export(root, work, run):
    """Build the catalog reader and the pinned govulncheck from the Dockerfile into a new directory."""
    if platform.system() != 'Linux' or platform.machine().lower() not in ('x86_64', 'amd64'):
        raise ValueError('Catalog reader export requires native Linux amd64')
    if os.environ.get('ROMD_CONTAINER_ENGINE', 'docker') != 'docker':
        raise ValueError('Docker BuildKit/buildx is required; Podman export is not qualified (no skip)')
    pin(root)
    output = work / 'catalog-reader-security'
    if output.exists():
        raise ValueError('Catalog reader export output already exists')
    builder = ['--builder', os.environ['ROMD_GO_READER_BUILDER']] if os.environ.get('ROMD_GO_READER_BUILDER') else []
    run(['docker', 'buildx', 'build', *builder, '--platform', 'linux/amd64', '--target', 'catalog-reader-security-artifacts',
         '--output', 'type=local,dest=' + str(output), '--file', str(root / 'Dockerfile'), root],
        'catalog-reader-security-export')
    files = {name: output / name for name in EXPORT}
    if any(not path.is_file() or path.is_symlink() for path in files.values()):
        raise ValueError('Catalog reader export is incomplete')
    return files


def normalize(text):
    """Return (Trivy-matchable OpenVEX document, sorted IDs whose status blocks)."""
    try:
        document = json.loads(text)
    except json.JSONDecodeError as error:
        raise ValueError('govulncheck did not produce an OpenVEX document') from error
    if not isinstance(document, dict) or document.get('@context') != OPENVEX or not isinstance(document.get('statements'), list):
        raise ValueError('govulncheck did not produce an OpenVEX v0.2.0 document')
    statements, blocking = [], set()
    for statement in document['statements']:
        name = (statement.get('vulnerability') or {}).get('name') if isinstance(statement, dict) else None
        packages = [unquote(item.get('@id', '')) for product in statement.get('products') or []
                    for item in product.get('subcomponents') or []] if isinstance(name, str) else []
        if (not isinstance(name, str) or not re.fullmatch(r'GO-[0-9]{4}-[0-9]+', name) or not packages
                or any(not re.fullmatch(r'pkg:golang/[^@\s]+@v[^@\s]+', package) for package in packages)):
            raise ValueError('Unrecognized govulncheck VEX statement')
        if (statement.get('status'), statement.get('justification')) not in NOT_BLOCKING:
            blocking.add(name)
        statements.append(dict(statement, products=[{'@id': package} for package in packages]))
    return dict(document, statements=statements), sorted(blocking)


def scan(govulncheck, binary, output, run, stage):
    """Write the reader's normalized VEX to output; return the IDs whose vulnerable code it contains."""
    document, blocking = normalize(run([govulncheck, '-db=' + DB, '-mode=binary', '-format=openvex', binary], stage))
    Path(output).write_text(json.dumps(document, indent=2) + '\n')
    return blocking


def assert_only_reader(report):
    """The reader's VEX may only describe the reader: it must be the scanned image's only Go binary."""
    targets = {group.get('Target') for group in report.get('Results') or [] if group.get('Type') in ('gobinary', 'gomod')}
    if targets != {READER}:
        raise ValueError('The catalog reader must be the only Go binary covered by its VEX; found: '
                         + ', '.join(sorted(map(str, targets))))


def assert_reader_inventory(report):
    """A reader scan must inventory its standard library and linked Sigstore/TUF trust modules."""
    assert_only_reader(report)
    packages = [package for group in report['Results'] if group.get('Target') == READER
                for package in group.get('Packages') or []]
    names = {package.get('Name', '') for package in packages}
    if 'stdlib' not in names or not all(any(name.startswith(prefix) for name in names) for prefix in TRUST_MODULES):
        raise ValueError('Catalog reader scan did not inventory its standard library and linked trust modules')
    return {'goModules': len(names - {'stdlib'}),
            'standardLibrary': next(package.get('Version') for package in packages if package.get('Name') == 'stdlib')}
