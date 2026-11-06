#!/usr/bin/env python3
"""Install and reverify the release-pinned Trivy for source advisory checks."""
import json
from pathlib import Path
import platform
import urllib.request

from tools import PINS, binary_from_archive


def ensure_trivy(directory):
    if platform.system() != 'Linux' or platform.machine().lower() not in ('x86_64', 'amd64'):
        raise ValueError('Source advisory scanner requires Linux x64; use the Linux x64 CI job')
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    pin = json.loads(PINS.read_text())['trivy']
    archive = directory / 'trivy.tar.gz'
    executable = directory / 'trivy'
    if not archive.exists():
        with urllib.request.urlopen(pin['url'], timeout=120) as source:
            data = source.read(300 * 1024 * 1024 + 1)
        if len(data) > 300 * 1024 * 1024:
            raise ValueError('Scanner archive exceeds download limit')
        # Validate before retaining a download. Interrupted downloads never become cache hits.
        temporary = directory / 'trivy.download'
        try:
            temporary.write_bytes(data)
            binary_from_archive(temporary, 'trivy', pin['sha256'])
            temporary.replace(archive)
        finally:
            temporary.unlink(missing_ok=True)
    binary = binary_from_archive(archive, 'trivy', pin['sha256'])
    if executable.exists() and executable.read_bytes() != binary:
        raise ValueError('Installed source scanner differs from pinned archive')
    if not executable.exists():
        executable.write_bytes(binary)
        executable.chmod(0o755)
    return executable
