#!/usr/bin/env python3
"""Prepare the isolated, hash-locked workflow-policy parser."""
from pathlib import Path
import subprocess
import sys
import tempfile
import venv

ROOT = Path(__file__).resolve().parents[2]
environment = ROOT / '.artifacts/ci-python'
python = environment / 'bin/python'
config = environment / 'pyvenv.cfg'
if config.is_file():
    version = next((line.partition(' = ')[2] for line in config.read_text(encoding='utf-8').splitlines()
                    if line.startswith('version = ')), '')
    expected = [str(part) for part in sys.version_info[:2]]
    if version.split('.')[:2] != expected or not python.exists():
        previous = Path(tempfile.mkdtemp(prefix='ci-python-previous-', dir=environment.parent)) / environment.name
        environment.rename(previous)
        print(f'Preserved the previous parser environment at {previous}', flush=True)
if not python.exists():
    venv.EnvBuilder(with_pip=True).create(environment)
subprocess.run([str(python), '-m', 'pip', 'install', '--disable-pip-version-check',
                '--require-hashes', '--only-binary=:all:', '-r', str(ROOT / 'scripts/ci/requirements.txt')], check=True)
