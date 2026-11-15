#!/usr/bin/env python3
"""Install Ubuntu packages on hosted runners with bounded, retried apt-get calls.

An unresponsive archive mirror once held `apt-get update` until the 20-minute
job timeout cancelled the job. Each attempt is now stopped after a fixed time
and retried a bounded number of times; if every attempt fails, the step fails.
"""
import re
import subprocess
import sys
import time

ATTEMPTS = 3
SECONDS = 60  # A normal update plus install takes about 12 seconds on hosted runners.
OPTIONS = ('-o', 'Acquire::Retries=3', '-o', 'Acquire::http::Timeout=30', '-o', 'Acquire::https::Timeout=30')
PACKAGE = re.compile(r'[a-z0-9][a-z0-9.+-]+')


def bounded(arguments, run=subprocess.run, sleep=time.sleep):
    # timeout runs as root so it stops apt-get itself, which releases the apt locks
    # before the next attempt; stopping only sudo would leave apt-get holding them.
    command = ['sudo', 'timeout', '--kill-after=10', str(SECONDS), 'apt-get', *OPTIONS, *arguments]
    for attempt in range(1, ATTEMPTS + 1):
        if run(command, check=False).returncode == 0:
            return
        if attempt < ATTEMPTS:
            print(f'apt-get {arguments[0]} attempt {attempt} failed or timed out; retrying', file=sys.stderr, flush=True)
            sleep(5 * attempt)
    raise SystemExit(f'apt-get {arguments[0]} failed after {ATTEMPTS} bounded attempts')


def main(packages, run=subprocess.run, sleep=time.sleep):
    if not packages or not all(PACKAGE.fullmatch(package) for package in packages):
        raise SystemExit('usage: apt_install.py PACKAGE...')
    bounded(['update', '-qq'], run, sleep)
    bounded(['install', '--yes', '--no-install-recommends', *packages], run, sleep)


if __name__ == '__main__':
    main(sys.argv[1:])
