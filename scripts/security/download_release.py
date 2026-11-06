#!/usr/bin/env python3
"""Download public release assets anonymously and match trusted current-run bytes."""
import argparse
import hashlib
import http.client
import json
from pathlib import Path
import re
import ssl
import urllib.error
import urllib.parse
import urllib.request


ALLOWED_HOSTS = {'github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com'}
HEADERS = {'Accept': 'application/octet-stream', 'User-Agent': 'ROMD-public-release-verifier'}
REPORT_NAME = 'download-result.json'


class DownloadFailure(ValueError):
    """A fixed diagnostic code; never include HTTP bodies or signed URLs."""


def safe_name(name):
    if not isinstance(name, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,199}', name):
        raise DownloadFailure('unsafe-asset-name')
    return name


def official_url(url):
    try:
        parsed = urllib.parse.urlsplit(url)
        valid = (parsed.scheme == 'https' and parsed.hostname in ALLOWED_HOSTS
                 and parsed.username is None and parsed.password is None
                 and parsed.port in (None, 443) and not parsed.fragment
                 and not any(character in url for character in ('\r', '\n', '\t', '\\')))
    except (ValueError, TypeError):
        valid = False
    if not valid:
        raise DownloadFailure('unsafe-release-url')
    return url


class OfficialRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        try:
            official_url(new_url)
            if request.get_method() != 'GET' or code not in (301, 302, 303, 307, 308):
                raise DownloadFailure('unsafe-redirect-method')
        except DownloadFailure:
            # urllib normally closes this response after redirect_request
            # returns. Refusing the redirect must close it ourselves.
            if response is not None:
                response.close()
            raise
        # Rebuild rather than forwarding any caller-supplied authorization,
        # cookies or proxy headers to another host (or a signed asset URL).
        return urllib.request.Request(new_url, headers=HEADERS, method='GET')


def anonymous_opener():
    # No global opener, proxy environment, auth handlers, cookies or netrc.
    # urllib does not consult credential environment variables itself.
    return urllib.request.build_opener(urllib.request.ProxyHandler({}), OfficialRedirects(),
                                      urllib.request.HTTPSHandler(context=ssl.create_default_context()))


def local_asset(path):
    if path.is_symlink() or not path.is_file():
        raise DownloadFailure('trusted-asset-not-regular-file')
    size = path.stat().st_size
    if not size:
        raise DownloadFailure('trusted-asset-empty')
    with path.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    return {'name': safe_name(path.name), 'expectedSize': size, 'expectedSha256': digest}


def expected_assets(metadata_dir, bundle, attestations):
    index_path = metadata_dir / 'release-metadata.json'
    index = json.loads(index_path.read_text())
    if not isinstance(index, dict) or index.get('schemaVersion') != 1 or index.get('result') != 'passed':
        raise DownloadFailure('trusted-metadata-not-passed')
    files = index.get('files')
    if not isinstance(files, dict) or not files or len(files) > 512:
        raise DownloadFailure('invalid-trusted-file-inventory')
    names = [safe_name(name).casefold() for name in
             ['release-metadata.json', *files, bundle.name, bundle.name + '.sha256', attestations.name, REPORT_NAME]]
    if len(set(names)) != len(names):
        raise DownloadFailure('duplicate-or-reserved-asset-name')
    assets = [local_asset(index_path)]
    for name, digest in sorted(files.items()):
        safe_name(name)
        if not isinstance(digest, str) or not re.fullmatch('[0-9a-f]{64}', digest):
            raise DownloadFailure('invalid-trusted-file-digest')
        asset = local_asset(metadata_dir / name)
        if asset['expectedSha256'] != digest:
            raise DownloadFailure('trusted-metadata-checksum-mismatch')
        assets.append(asset)
    bundle_asset = local_asset(bundle)
    checksum = Path(str(bundle) + '.sha256')
    checksum_asset = local_asset(checksum)
    fields = checksum.read_text().strip().split()
    if (len(fields) != 2 or fields[0] != bundle_asset['expectedSha256']
            or fields[1].removeprefix('*') != bundle.name):
        raise DownloadFailure('trusted-bundle-checksum-mismatch')
    assets.extend((bundle_asset, checksum_asset, local_asset(attestations)))
    return assets


def download_asset(opener, url, destination, record):
    partial = destination.with_name('.' + destination.name + '.partial')
    digest = hashlib.sha256()
    size = 0
    try:
        request = urllib.request.Request(official_url(url), headers=HEADERS, method='GET')
        with opener.open(request, timeout=60) as response:
            official_url(response.geturl())
            if response.status != 200:
                raise DownloadFailure('unexpected-http-status')
            length = response.headers.get('Content-Length')
            if length is not None:
                if not re.fullmatch('[0-9]+', length) or int(length) != record['expectedSize']:
                    raise DownloadFailure('http-content-length-mismatch')
            with partial.open('xb') as output:
                while True:
                    chunk = response.read(min(65536, record['expectedSize'] - size + 1))
                    if not chunk:
                        break
                    size += len(chunk)
                    digest.update(chunk)
                    if size > record['expectedSize']:
                        raise DownloadFailure('download-size-mismatch')
                    output.write(chunk)
        if size != record['expectedSize']:
            raise DownloadFailure('download-size-mismatch')
        if digest.hexdigest() != record['expectedSha256']:
            raise DownloadFailure('download-checksum-mismatch')
        partial.rename(destination)
        record['result'] = 'passed'
    finally:
        record.update(actualSize=size, actualSha256=digest.hexdigest())
        # Unexpected content might contain a login form or sensitive error page.
        # Only sanitized counts/hashes survive a failed download.
        partial.unlink(missing_ok=True)


def download_release(repository, tag, metadata_dir, bundle, attestations, output):
    # Never reuse or clean an existing directory, including after a failed run.
    output.mkdir(parents=True, exist_ok=False, mode=0o700)
    report = {'schemaVersion': 1, 'result': 'failed', 'transport': 'anonymous-https', 'files': []}
    try:
        if (not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}', repository)
                or len(tag) > 128 or not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?', tag)):
            raise DownloadFailure('invalid-release-identity')
        report.update(repository=repository, tag=tag)
        assets = expected_assets(metadata_dir, bundle, attestations)
        opener = anonymous_opener()
        for expected in assets:
            record = dict(expected, result='failed')
            report['files'].append(record)
            url = 'https://github.com/' + repository + '/releases/download/' + tag + '/' + expected['name']
            download_asset(opener, url, output / expected['name'], record)
        report['result'] = 'passed'
    except DownloadFailure as error:
        report['failure'] = str(error)
    except urllib.error.HTTPError as error:
        report.update(failure='http-error', httpStatus=error.code)
        error.close()
    except (urllib.error.URLError, TimeoutError, ssl.SSLError, http.client.HTTPException):
        report['failure'] = 'network-error'
    except (ValueError, TypeError, KeyError, OSError):
        report['failure'] = 'invalid-trusted-input-or-io-error'
    (output / REPORT_NAME).write_text(json.dumps(report, indent=2, sort_keys=True) + '\n')
    return report['result'] == 'passed'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--tag', required=True)
    for name in ('metadata-dir', 'bundle', 'attestations', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        passed = download_release(args.repository, args.tag, args.metadata_dir, args.bundle,
                                  args.attestations, args.output)
    except OSError:
        print('Public release asset download: failed (output unavailable; choose a new directory)')
        return 1
    print('Public release asset download: ' + ('passed' if passed else 'failed; see download-result.json'))
    return 0 if passed else 1


if __name__ == '__main__':
    raise SystemExit(main())
