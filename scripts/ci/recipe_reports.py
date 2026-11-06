#!/usr/bin/env python3
"""Give each report-preservation check its own retained evidence directory."""
import argparse
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('probe', type=Path)
    parser.add_argument('--evidence-parent', type=Path, default=ROOT / 'coverage/deterministic')
    args = parser.parse_args()
    probe = args.probe.resolve(strict=True)
    args.evidence_parent.mkdir(parents=True, exist_ok=True)
    parent = Path(tempfile.mkdtemp(prefix='recipe-reports-', dir=args.evidence_parent)).resolve()
    evidence = parent / 'source-preservation'
    print(f'Recipe-report evidence: {parent}', flush=True)
    # The existing safety test requires a destination that does not already exist.
    with (parent / 'runner.log').open('w') as log:
        result = subprocess.run([sys.executable, str(ROOT / 'tools/Romd.Media.Probe/test-recipe-reports.py'),
                                 str(probe), str(evidence)], cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
    print((parent / 'runner.log').read_text(), end='')
    return result.returncode


if __name__ == '__main__':
    sys.exit(main())
