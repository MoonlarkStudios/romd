#!/usr/bin/env python3
"""Prepare the isolated, hash-locked workflow-policy parser."""
from pathlib import Path
import subprocess
import venv

ROOT = Path(__file__).resolve().parents[2]
environment = ROOT / '.artifacts/ci-python'
python = environment / 'bin/python'
if not python.exists():
    venv.EnvBuilder(with_pip=True).create(environment)
subprocess.run([str(python), '-m', 'pip', 'install', '--disable-pip-version-check',
                '--require-hashes', '--only-binary=:all:', '-r', str(ROOT / 'scripts/ci/requirements.txt')], check=True)
