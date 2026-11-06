#!/usr/bin/env python3
"""Exercise recipe-report source preservation through the compiled probe CLI."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile


def run_case(probe: Path, root: Path, source: str, alias: str) -> None:
    case = root / f"{source}-{alias}"
    case.mkdir()
    paths = {name: case / f"{name}.bin" for name in ("Artifact", "Iso", "Dat")}
    for name, path in paths.items():
        path.write_bytes(f"preserve {name}\n".encode())
    spec = case / "spec.json"
    spec.write_text(json.dumps({"Name": "report-safety", "DatId": "1",
                                **{name: str(path) for name, path in paths.items()}}))
    sources = [spec, *paths.values()]
    before = {path: path.read_bytes() for path in sources}
    target = spec if source == "Spec" else paths[source]
    report = case / "report.json"
    if alias == "symlink":
        report.symlink_to(target)
    elif alias == "hardlink":
        os.link(target, report)
    elif alias == "case":
        report = target.with_name(target.name.upper())
    elif alias == "existing":
        report.write_bytes(b"preserve existing report\n")
        before[report] = report.read_bytes()
    else:
        raise AssertionError(alias)
    completed = subprocess.run(["dotnet", str(probe), "--recipe-probe", str(spec), str(report)],
                               cwd=case, capture_output=True, text=True, timeout=30)
    (case / "stdout.log").write_text(completed.stdout)
    (case / "stderr.log").write_text(completed.stderr)
    assert completed.returncode != 0, "unsafe destination must be rejected"
    assert "already exists" in completed.stderr.lower(), "probe must reject the report destination, not fail to start"
    for path, content in before.items():
        assert path.read_bytes() == content, f"source/report changed: {source}/{alias}: {path}"
    print(f"PASS: {source}/{alias} rejected without changing inputs or existing output")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("probe", type=Path, help="absolute path to compiled Romd.Media.Probe.dll")
    parser.add_argument("evidence", type=Path, help="new test-owned evidence directory")
    args = parser.parse_args()
    probe = args.probe.resolve(strict=True)
    root = args.evidence.resolve()
    root.mkdir()
    for source in ("Spec", "Artifact", "Iso", "Dat"):
        for alias in ("symlink", "hardlink"):
            run_case(probe, root, source, alias)
    run_case(probe, root, "Artifact", "existing")
    with tempfile.TemporaryDirectory(dir=root) as scratch:
        marker = Path(scratch) / "case-probe"
        marker.write_bytes(b"filesystem case test")
        case_insensitive = marker.with_name(marker.name.upper()).exists()
    if case_insensitive:
        for source in ("Spec", "Artifact", "Iso", "Dat"):
            run_case(probe, root, source, "case")
    else:
        print("NOT APPLICABLE: case aliases on this case-sensitive filesystem")


if __name__ == "__main__":
    main()
