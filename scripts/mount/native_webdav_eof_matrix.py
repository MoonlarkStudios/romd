#!/usr/bin/env python3
"""Opt-in macOS matrix isolating native WebDAV EOF-crossing read behavior.

Serves disposable fixtures with rclone behind a stdlib logging pass-through
proxy on loopback. The proxy records every HTTP exchange and can throttle only
the agent's background whole-file download, so the downloaded region at each
read is deterministic. Every case remounts, because unmounting discards the
agent's file cache. Predictions come from Apple's published webdavfs source
(webdav_rdwr/webdav_read_bytes); a mismatch means source and shipped binary
disagree, not that the explanation should be bent.

Exit 0 when every prediction matched, 1 on any mismatch, 2 if cleanup failed.
"""
import argparse
import hashlib
import http.client
import http.server
import json
import os
from pathlib import Path
import random
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse

KIB = 1024
MIB = 1024 * KIB
HOP_BY_HOP = {'connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization',
              'te', 'trailers', 'transfer-encoding', 'upgrade'}
DAV_METHODS = ['GET', 'HEAD', 'OPTIONS', 'PROPFIND', 'PROPPATCH', 'LOCK', 'UNLOCK',
               'PUT', 'DELETE', 'MKCOL', 'MOVE', 'COPY']


class Recorder:
    """Shared proxy state: exchange log, background-download throttle, and optional
    status substituted for WebDAVFS ranges that cross EOF (mitigation experiment)."""

    def __init__(self, eof_crossing_status=None):
        self.epoch = time.monotonic()
        self.exchanges = []
        self.lock = threading.Lock()
        self.background_rate = None
        self.eof_crossing_status = eof_crossing_status

    def now(self):
        return round(time.monotonic() - self.epoch, 4)

    def add(self, exchange):
        with self.lock:
            self.exchanges.append(exchange)

    def since(self, started):
        with self.lock:
            return [dict(e) for e in self.exchanges if e['started'] >= started]


def is_background_download(method, range_header):
    # The agent's cache fill is a plain GET, or `bytes=N-` when resuming;
    # out-of-band reads always use a closed `bytes=a-b` range.
    return method == 'GET' and (range_header is None or range_header.strip().endswith('-'))


def crosses_eof(range_header, content_range):
    """True for a closed request range whose last byte is at or past the representation length."""
    if not range_header or not content_range or range_header.strip().endswith('-'):
        return False
    total = content_range.rsplit('/', 1)[-1]
    return total.isdigit() and int(range_header.rsplit('-', 1)[-1]) >= int(total)


def read_is_exact(read):
    if 'sha256_matches' in read:
        return read['sha256_matches'] and read['bytes'] == read['expected_bytes']
    return read.get('exact', True)


def relay_handler(upstream_port, recorder):
    class Relay(http.server.BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'

        def log_message(self, *_):
            pass

        def relay(self):
            range_header = self.headers.get('Range')
            exchange = dict(started=recorder.now(), method=self.command,
                            path=urllib.parse.unquote(urllib.parse.urlsplit(self.path).path),
                            range=range_header, if_range=self.headers.get('If-Range'),
                            user_agent=self.headers.get('User-Agent'), bytes_sent=0,
                            aborted=False, finished=None)
            recorder.add(exchange)
            if self.headers.get('Transfer-Encoding'):
                raise ValueError('Chunked request bodies are outside this read-only fixture')
            body_length = int(self.headers.get('Content-Length') or 0)
            body = self.rfile.read(body_length) if body_length else None
            upstream = http.client.HTTPConnection('127.0.0.1', upstream_port, timeout=120)
            upstream.putrequest(self.command, self.path, skip_host=True, skip_accept_encoding=True)
            for name, value in self.headers.items():
                if name.lower() not in HOP_BY_HOP:
                    upstream.putheader(name, value)
            upstream.endheaders(body)
            response = upstream.getresponse()
            if self.defers_eof_crossing(range_header, response):
                self.defer(response, exchange)
                upstream.close()
                return
            headers = [(k, v) for k, v in response.getheaders() if k.lower() not in HOP_BY_HOP]
            declared = response.getheader('Content-Length')
            has_body = self.command != 'HEAD' and response.status not in (204, 304)
            buffered = None
            if has_body and declared is None:
                buffered = response.read()
                headers.append(('Content-Length', str(len(buffered))))
            rate = recorder.background_rate if is_background_download(self.command, range_header) else None
            exchange.update(status=response.status, content_length=declared,
                            content_range=response.getheader('Content-Range'), throttled=rate)
            try:
                self.send_response_only(response.status, response.reason)
                for name, value in headers:
                    self.send_header(name, value)
                self.end_headers()
                if buffered is not None:
                    self.wfile.write(buffered)
                    exchange['bytes_sent'] = len(buffered)
                elif has_body:
                    self.stream(response, rate, exchange)
            except (BrokenPipeError, ConnectionResetError):
                exchange['aborted'] = True
                self.close_connection = True
            finally:
                upstream.close()
                exchange['finished'] = recorder.now()

        def defers_eof_crossing(self, range_header, response):
            return (recorder.eof_crossing_status is not None and response.status == 206
                    and (self.headers.get('User-Agent') or '').startswith('WebDAVFS/')
                    and crosses_eof(range_header, response.getheader('Content-Range')))

        def defer(self, response, exchange):
            response.read()
            exchange.update(status=recorder.eof_crossing_status, rewritten_from=response.status,
                            content_length='0', content_range=response.getheader('Content-Range'),
                            throttled=None)
            self.send_response_only(recorder.eof_crossing_status)
            self.send_header('Retry-After', '1')
            self.send_header('Content-Length', '0')
            self.end_headers()
            exchange['finished'] = recorder.now()

        def stream(self, response, rate, exchange):
            slice_size = max(1024, rate // 20) if rate else 256 * 1024
            started = time.monotonic()
            while chunk := response.read(slice_size):
                self.wfile.write(chunk)
                exchange['bytes_sent'] += len(chunk)
                if rate:
                    delay = started + exchange['bytes_sent'] / rate - time.monotonic()
                    if delay > 0:
                        time.sleep(delay)

    for method in DAV_METHODS:
        setattr(Relay, 'do_' + method, Relay.relay)
    return Relay


def free_port():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]


def start_rclone(rclone, fixtures, work):
    port = free_port()
    config = work / 'rclone.conf'
    config.write_text('')
    process = subprocess.Popen(
        [rclone, 'serve', 'webdav', str(fixtures), '--addr', f'127.0.0.1:{port}', '--read-only',
         '--config', str(config), '--log-file', str(work / 'rclone.log'), '-v'],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    end = time.monotonic() + 20
    while time.monotonic() < end:
        try:
            connection = http.client.HTTPConnection('127.0.0.1', port, timeout=2)
            connection.request('OPTIONS', '/')
            connection.getresponse().read()
            connection.close()
            return process, port
        except OSError:
            time.sleep(.1)
    process.kill()
    raise TimeoutError('rclone did not become ready')


def mounted_points():
    return subprocess.check_output(['/sbin/mount'], text=True, timeout=10)


def is_mounted(point):
    return (' on ' + str(point.resolve()) + ' (') in mounted_points()


def mount(url, point):
    result = subprocess.run(['/sbin/mount_webdav', '-S', '-o', 'rdonly', url, str(point)],
                            stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=45)
    if result.returncode != 0 or not is_mounted(point):
        raise RuntimeError(f'mount_webdav exited {result.returncode}: {result.stderr.strip()}')


def unmount(point):
    if not is_mounted(point):
        return
    if subprocess.run(['/sbin/umount', str(point)], capture_output=True, timeout=30).returncode:
        subprocess.run(['/sbin/umount', '-f', str(point)], capture_output=True, timeout=30)
    if is_mounted(point):
        raise RuntimeError('Mount remains')


def describe_read(data, expected, seconds):
    extra = data[len(expected):]
    return dict(returned=len(data), expected=len(expected), exact=data == expected,
                prefix_exact=data[:len(expected)] == expected,
                extra_bytes=len(extra), extra_all_zero=bool(extra) and not any(extra),
                seconds=round(seconds, 4))


def timed_pread(fd, source, offset, length):
    started = time.monotonic()
    data = os.pread(fd, length, offset)
    return dict(call='pread', offset=offset, requested=length,
                **describe_read(data, source[offset:offset + length], time.monotonic() - started))


def sequential_read(path, source, chunk):
    digest = hashlib.sha256()
    total = 0
    started = time.monotonic()
    last = b''
    with open(path, 'rb', buffering=0) as stream:
        while data := stream.read(chunk):
            digest.update(data)
            last, total = data, total + len(data)
    tail_start = total - len(last)
    return dict(call=f'sequential read({chunk})', bytes=total, expected_bytes=len(source),
                sha256_matches=digest.hexdigest() == hashlib.sha256(source).hexdigest(),
                last_chunk=describe_read(last, source[tail_start:tail_start + chunk], 0),
                seconds=round(time.monotonic() - started, 4))


def wait_for_download(recorder, path, since, timeout):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if any(e['path'] == path and is_background_download(e['method'], e['range'])
               and e['finished'] is not None and not e['aborted'] and e.get('status') in (200, 206)
               and e['content_length'] is not None and e['bytes_sent'] == int(e['content_length'])
               for e in recorder.since(since)):
            return True
        time.sleep(.1)
    return False


def out_of_band_ranges(recorder, name, since):
    """Closed-range GETs the agent issued for this fixture: the out-of-band read path."""
    return [e['range'] for e in recorder.since(since)
            if e['path'] == '/' + name and e['method'] == 'GET'
            and not is_background_download(e['method'], e['range'])]


def timed_open(path):
    started = time.monotonic()
    stream = open(path, 'rb', buffering=0)
    return stream, dict(call='open', seconds=round(time.monotonic() - started, 4))


def padded_to_request(read):
    return read['returned'] == read['requested'] and read['prefix_exact'] and read['extra_all_zero']


def case_eof_cold(point, name, source, recorder, since):
    size = len(source)
    stream, opened = timed_open(point / name)
    with stream:
        fd = stream.fileno()
        reads = [timed_pread(fd, source, size - 1000, 5000),
                 timed_pread(fd, source, size - 1000, 100000),
                 timed_pread(fd, source, size - 4096, 4096),
                 timed_pread(fd, source, 1_000_000, 4096),
                 timed_pread(fd, source, size, 4096)]
    padded, large, ends_at_eof, in_bounds, at_eof = reads
    ranges = out_of_band_ranges(recorder, name, since)
    matched = (padded_to_request(padded) and padded_to_request(large)
               and ends_at_eof['exact'] and in_bounds['exact'] and at_eof['returned'] == 0
               and f'bytes={size - 1000}-{size + 3999}' in ranges
               and f'bytes={size - 1000}-{size + 98999}' in ranges)
    return [opened] + reads, matched


def case_lseek_read(point, name, source, recorder, since):
    size = len(source)
    stream, opened = timed_open(point / name)
    with stream:
        fd = stream.fileno()
        os.lseek(fd, size - 1000, os.SEEK_SET)
        started = time.monotonic()
        data = os.read(fd, 5000)
        read = dict(call='lseek+read', offset=size - 1000, requested=5000,
                    **describe_read(data, source[size - 1000:], time.monotonic() - started),
                    position_after=os.lseek(fd, 0, os.SEEK_CUR))
    matched = (padded_to_request(read) and read['position_after'] == size + 4000
               and f'bytes={size - 1000}-{size + 3999}' in out_of_band_ranges(recorder, name, since))
    return [opened, read], matched


def case_eof_warm(point, name, source, recorder, since):
    size = len(source)
    stream, opened = timed_open(point / name)
    with stream:
        downloaded = wait_for_download(recorder, '/' + name, since, 60)
        time.sleep(1)
        read = timed_pread(stream.fileno(), source, size - 1000, 5000)
    matched = (downloaded and read['exact'] and read['returned'] == 1000
               and not out_of_band_ranges(recorder, name, since))
    return [opened, dict(call='wait for background download', completed=downloaded), read], matched


def case_small_within_window(point, name, source, recorder, since):
    size = len(source)
    stream, opened = timed_open(point / name)
    with stream:
        read = timed_pread(stream.fileno(), source, size - 1000, 4096)
    return [opened, read], (read['exact'] and read['returned'] == 1000
                            and not out_of_band_ranges(recorder, name, since))


def case_beyond_window(point, name, source, recorder, since):
    size = len(source)
    stream, opened = timed_open(point / name)
    with stream:
        read = timed_pread(stream.fileno(), source, size - 1000, 4096)
    return [opened, read], (padded_to_request(read) and out_of_band_ranges(recorder, name, since)
                            == [f'bytes={size - 1000}-{size + 3095}'])


def case_offset_zero(point, name, source, recorder, since):
    size = len(source)
    stream, opened = timed_open(point / name)
    with stream:
        read = timed_pread(stream.fileno(), source, 0, size + 4000)
    return [opened, read], (read['exact'] and read['returned'] == size
                            and not out_of_band_ranges(recorder, name, since))


def case_offset_one(point, name, source, recorder, since):
    size = len(source)
    stream, opened = timed_open(point / name)
    with stream:
        read = timed_pread(stream.fileno(), source, 1, size + 4000)
    return [opened, read], (padded_to_request(read) and read['extra_bytes'] == 4001
                            and out_of_band_ranges(recorder, name, since) == [f'bytes=1-{size + 4000}'])


def case_sequential(point, name, source, recorder, since):
    read = sequential_read(point / name, source, MIB)
    padded_total = -(-len(source) // MIB) * MIB
    return [read], (read['bytes'] == padded_total and not read['sha256_matches']
                    and read['last_chunk']['extra_all_zero'])


def case_sequential_observed(point, name, source, recorder, since):
    return [sequential_read(point / name, source, MIB)], None


def case_copy(point, name, source, recorder, since):
    destination = point.parent / ('copied-' + name)
    started = time.monotonic()
    result = subprocess.run(['/bin/cp', str(point / name), str(destination)],
                            capture_output=True, text=True, timeout=180)
    copied = destination.read_bytes() if destination.exists() else b''
    destination.unlink(missing_ok=True)
    tail = copied[len(source):]
    return [dict(call='/bin/cp', exit=result.returncode, bytes=len(copied),
                 expected_bytes=len(source), exact=copied == source,
                 prefix_exact=copied[:len(source)] == source,
                 extra_all_zero=bool(tail) and not any(tail),
                 seconds=round(time.monotonic() - started, 4))], None


CASES = [
    dict(name='eof_cold_pread', size=4 * MIB + 1000, rate=256 * KIB, run=case_eof_cold,
         prediction='Closed-range GETs for the EOF-crossing reads; each returns the requested '
                    'count (5000, 100000) with a zero tail; reads ending at/inside EOF exact; '
                    'read at EOF returns 0'),
    dict(name='eof_cold_lseek_read', size=4 * MIB + 1000, rate=256 * KIB, run=case_lseek_read,
         prediction='Same as pread: 5000 bytes with zero tail; file position advances to size+4000'),
    dict(name='eof_warm_pread', size=4 * MIB + 1000, rate=4 * MIB, run=case_eof_warm,
         prediction='After the background download completes, no out-of-band GET; cached path '
                    'pins to EOF: 1000 bytes'),
    dict(name='small_within_wait_window', size=20 * KIB, rate=KIB, run=case_small_within_window,
         prediction='Request end stays within 32 KiB of the frontier: no out-of-band GET; waits '
                    'for the throttled file, then returns exactly 1000 bytes'),
    dict(name='beyond_wait_window', size=64 * KIB + 1000, rate=KIB, run=case_beyond_window,
         prediction='Request end exceeds frontier + 32 KiB: one closed-range GET, 4096 bytes, zero tail'),
    dict(name='offset_zero_guard', size=4 * MIB + 1000, rate=MIB, run=case_offset_zero,
         prediction='Offset 0 never reads out of band: no closed-range GET; returns exactly size'),
    dict(name='offset_one', size=4 * MIB + 1000, rate=MIB, run=case_offset_one,
         prediction='Offset 1 reads out of band: GET bytes=1-(size+4000); returns size+4000 '
                    'with a 4001-byte zero tail'),
    dict(name='sequential_throttled', size=16 * MIB + 1000, rate=MIB, run=case_sequential,
         prediction='1 MiB reads outrun the download: total rounds up to 17 MiB, hash mismatch'),
    dict(name='copy_throttled', size=16 * MIB + 1000, rate=MIB, run=case_copy,
         prediction='Observation only: whether /bin/cp output is padded'),
    dict(name='sequential_unthrottled', size=256 * MIB + 1000, rate=None, run=case_sequential_observed,
         prediction='Observation only: loopback race without artificial throttling'),
    dict(name='copy_unthrottled', size=256 * MIB + 1000, rate=None, run=case_copy,
         prediction='Observation only: /bin/cp over loopback without artificial throttling'),
]


def environment(rclone):
    def output(command):
        try:
            return subprocess.check_output(command, text=True, timeout=10, stderr=subprocess.DEVNULL).strip()
        except (OSError, subprocess.SubprocessError):
            return None
    kext = output(['/usr/sbin/kextstat', '-l', '-b', 'com.apple.filesystems.webdav'])
    return dict(macos=output(['/usr/bin/sw_vers', '-productVersion']),
                build=output(['/usr/bin/sw_vers', '-buildVersion']),
                kernel=output(['/usr/bin/uname', '-v']),
                webdav_kext=' '.join(kext.split()[5:7]) if kext else None,
                rclone=(output([rclone, 'version']) or '').splitlines()[:1],
                predictions_source='apple-oss-distributions/webdavfs tag webdavfs-403.0.0.0.1')


MITIGATION_PREDICTION = ('WebDAVFS ranges crossing EOF answered with the configured status: '
                         'every read exact and the mount stays up')


def fixture_name(index, case):
    return f'{index:02d}-{case["name"]}.bin'


def fixture_bytes(case):
    # Chunked because Random.randbytes overflows at 256 MiB.
    randomizer = random.Random(f'romd-{case["name"]}')
    return b''.join(randomizer.randbytes(min(16 * MIB, case['size'] - start))
                    for start in range(0, case['size'], 16 * MIB))


def run_case(case, name, point, url, recorder):
    source = fixture_bytes(case)
    recorder.background_rate = case['rate']
    since = recorder.now()
    result = dict(case=case['name'], fixture=name, size=case['size'],
                  background_rate=case['rate'], prediction=case['prediction'])
    try:
        mount(url, point)
        result['reads'], matched = case['run'](point, name, source, recorder, since)
        result['mounted_after'] = is_mounted(point)
        if recorder.eof_crossing_status is None:
            result['matched_prediction'] = matched
        else:
            result['prediction'] = MITIGATION_PREDICTION
            result['matched_prediction'] = result['mounted_after'] and all(map(read_is_exact, result['reads']))
    except Exception as error:
        result['error'] = f'{type(error).__name__}: {error}'
        result['matched_prediction'] = False
    finally:
        unmount(point)
    exchanges = recorder.since(since)
    result['http'] = [e for e in exchanges if e['path'] == '/' + name and e['method'] in ('GET', 'HEAD')]
    result['other_requests'] = sorted({f'{e["method"]} {e.get("status")}' for e in exchanges
                                       if e not in result['http']})
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('--rclone', required=True, help='absolute path to a verified rclone binary')
    parser.add_argument('--report', required=True, help='path for the JSON report')
    parser.add_argument('--only', nargs='*', help='case names to run (default: all)')
    parser.add_argument('--eof-crossing-status', type=int,
                        help='mitigation experiment: answer WebDAVFS closed ranges ending at or '
                             'past EOF with this status (for example 503) instead of 206')
    arguments = parser.parse_args()
    if sys.platform != 'darwin':
        raise SystemExit('This probe requires macOS mount_webdav')

    work = Path(tempfile.mkdtemp(prefix='romd-webdav-eof-'))
    fixtures, point = work / 'fixtures', work / 'mounted'
    fixtures.mkdir(mode=0o700)
    point.mkdir(mode=0o700)
    recorder = Recorder(arguments.eof_crossing_status)
    report = dict(scope='native macOS mount_webdav -> stdlib logging proxy -> rclone serve webdav '
                        '(loopback, anonymous, read-only, disposable fixtures)',
                  environment=environment(arguments.rclone),
                  eof_crossing_status=arguments.eof_crossing_status, cases=[], cleanup_verified=False)
    rclone = proxy = None
    try:
        # Create every fixture before rclone lists the directory; its VFS caches listings.
        selected = [c for c in CASES if not arguments.only or c['name'] in arguments.only]
        names = [fixture_name(index, case) for index, case in enumerate(selected, 1)]
        for case, name in zip(selected, names):
            (fixtures / name).write_bytes(fixture_bytes(case))
        rclone, upstream_port = start_rclone(arguments.rclone, fixtures, work)
        proxy = http.server.ThreadingHTTPServer(('127.0.0.1', 0), relay_handler(upstream_port, recorder))
        proxy.daemon_threads = True
        threading.Thread(target=proxy.serve_forever, daemon=True).start()
        url = f'http://127.0.0.1:{proxy.server_address[1]}/'
        for case, name in zip(selected, names):
            outcome = run_case(case, name, point, url, recorder)
            report['cases'].append(outcome)
            print(json.dumps({'case': case['name'], 'matched_prediction': outcome['matched_prediction'],
                              'error': outcome.get('error')}), flush=True)
        report['user_agents'] = sorted({e['user_agent'] for e in recorder.since(0) if e['user_agent']})
    finally:
        try:
            unmount(point)
            if proxy:
                proxy.shutdown()
                proxy.server_close()
            if rclone:
                rclone.terminate()
                rclone.wait(timeout=10)
            shutil.rmtree(work)
            report['cleanup_verified'] = not work.exists()
        except Exception as error:
            report['cleanup_error'] = f'{type(error).__name__}: {error}; preserved {work}'
    Path(arguments.report).write_text(json.dumps(report, indent=2) + '\n')
    if not report['cleanup_verified']:
        return 2
    return 0 if all(c['matched_prediction'] is not False for c in report['cases']) else 1


if __name__ == '__main__':
    sys.exit(main())
