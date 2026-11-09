#!/usr/bin/env python3
"""Prepare pinned network-probe tools and require executed, passing TRX results."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

RCLONE_VERSION = '1.75.1'
RCLONE_ARCHIVE = f'rclone-v{RCLONE_VERSION}-linux-amd64.zip'
RCLONE_SHA256 = '982b5aa772841168f8e380f139e9e787b2a105403e32b94da8676a0e1c0a13ab'
# The versioned GitHub asset matches the publisher checksum. The direct download
# host returned HTTP 403 to hosted runners during qualification.
RCLONE_URL = f'https://github.com/rclone/rclone/releases/download/v{RCLONE_VERSION}/{RCLONE_ARCHIVE}'
MAX_BYTES = 128 * 1024 * 1024
ENDPOINT = 'Romd.Hosting.IntegrationTests.Endpoints.ConsumerIdentityEndpointsTests.'
STREAMING = 'Romd.Hosting.IntegrationTests.Vfs.NginxIngressStreamingTests.'
PROBES = {
    'hosting': [STREAMING + name for name in (
        'HttpsTemplate_SlowVfsRead_AppliesBackpressureWithoutTemporaryFiles',
        'TunnelTemplate_SlowVfsRead_AppliesBackpressureWithoutTemporaryFiles')],
    'endpoints': [ENDPOINT + name for name in (
        'Vfs_NginxHttps_DeploymentTemplateServesRevocableAuthenticatedRanges',
        'Vfs_NginxHttps_RcloneReadsThroughDeploymentTemplate',
        'Vfs_Https_RcloneValidatesCertificatesAndReadsAuthenticatedContent',
        'Vfs_Https_RcloneRegisteredConnections_UseExportAndEnforceLifecycle',
        'Vfs_Rclone_RealNetworkDeviceLoginListingRangesAndRevocation',
        'Vfs_RcloneTokenCommand_RefreshesAfterUnauthorizedAndFailsAfterRefreshRevocation')],
}
TRX = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'


def install_rclone(archive, directory):
    if archive.stat().st_size > MAX_BYTES:
        raise ValueError('Rclone archive exceeds the size bound')
    with archive.open('rb') as source:
        if hashlib.file_digest(source, 'sha256').hexdigest() != RCLONE_SHA256:
            raise ValueError('Rclone archive checksum mismatch')
    member = RCLONE_ARCHIVE.removesuffix('.zip') + '/rclone'
    with zipfile.ZipFile(archive) as source:
        entries = [info for info in source.infolist() if info.filename == member]
        if len(entries) != 1 or entries[0].file_size > MAX_BYTES or stat.S_ISLNK(entries[0].external_attr >> 16):
            raise ValueError('Rclone executable inventory is invalid')
        # Copy only the exact authenticated executable, never archive-selected paths.
        binary = directory / 'rclone'
        with source.open(entries[0]) as executable, binary.open('xb') as target:
            shutil.copyfileobj(executable, target)
    binary.chmod(0o755)
    return binary


def prepare(directory):
    nginx = shutil.which('nginx')
    if nginx is None:
        raise ValueError('Install the Ubuntu nginx package before preparing network probes')
    directory = directory.resolve()
    directory.mkdir(mode=0o700, parents=True, exist_ok=False)
    archive = directory / RCLONE_ARCHIVE
    with urllib.request.urlopen(RCLONE_URL, timeout=60) as response, archive.open('xb') as target:
        count = 0
        while block := response.read(1024 * 1024):
            count += len(block)
            if count > MAX_BYTES:
                raise ValueError('Rclone download exceeds the size bound')
            target.write(block)
    binary = install_rclone(archive, directory)
    version = subprocess.check_output([str(binary), 'version'], text=True, timeout=15)
    if version.splitlines()[0] != f'rclone v{RCLONE_VERSION}':
        raise ValueError('Rclone executable version differs from the reviewed pin')
    subprocess.run([nginx, '-v'], check=True, timeout=15)
    variables = {'ROMD_VFS_RCLONE': str(binary), 'ROMD_VFS_NGINX': nginx,
                 'ROMD_VFS_PYTHON': sys.executable}
    if any('\n' in value or '\r' in value for value in variables.values()):
        raise ValueError('Tool paths cannot contain line breaks')
    with Path(os.environ['GITHUB_ENV']).open('a') as environment:
        environment.writelines(f'{key}={value}\n' for key, value in variables.items())
    print(f'Prepared nginx and checksum-verified rclone {RCLONE_VERSION} for loopback protocol tests')


def verify(part, directory):
    reports = list(directory.rglob('integration.trx'))
    if len(reports) != 1:
        raise ValueError('Expected exactly one integration TRX report')
    results = ET.parse(reports[0]).findall('.//' + TRX + 'UnitTestResult')
    receipt = {'schemaVersion': 1, 'part': part, 'result': 'passed', 'probes': []}
    errors = []
    for name in PROBES[part]:
        matches = [result for result in results if result.get('testName') == name]
        if len(matches) != 1 or matches[0].get('outcome') != 'Passed':
            errors.append(name)
        receipt['probes'].append({'name': name, 'results': len(matches),
                                  'outcome': matches[0].get('outcome') if len(matches) == 1 else None,
                                  'duration': matches[0].get('duration') if len(matches) == 1 else None})
    if errors:
        receipt['result'] = 'failed'
    # Retain only bounded probe metadata, never the TRX's stdout or failure payloads.
    (directory / 'network-probes.json').write_text(json.dumps(receipt, indent=2) + '\n')
    if errors:
        raise ValueError('Required network probe missing, skipped, duplicated or failed: ' + ', '.join(errors))
    for result in receipt['probes']:
        print(f'Passed required network probe: {result["name"]} ({result["duration"] or "unknown duration"})')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('prepare').add_argument('directory', type=Path)
    check = commands.add_parser('verify')
    check.add_argument('part', choices=PROBES)
    check.add_argument('directory', type=Path)
    args = parser.parse_args()
    if args.command == 'prepare':
        prepare(args.directory)
    else:
        verify(args.part, args.directory)


if __name__ == '__main__':
    main()
