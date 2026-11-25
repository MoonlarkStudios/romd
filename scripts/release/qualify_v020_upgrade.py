#!/usr/bin/env python3
"""One-time native upgrade rehearsal with attested public ROMD bundles."""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile


REPO = "MoonlarkStudios/romd"
SIGNER = REPO + "/.github/workflows/release.yml"


def command(*args, cwd=None):
    return subprocess.check_output([str(arg) for arg in args], cwd=cwd, text=True, timeout=180).strip()


def qualifier(path, module_name):
    spec = importlib.util.spec_from_file_location(module_name, path / "scripts/release/qualify.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def download(qa, work, source, tag):
    revision = command("git", "rev-parse", "HEAD", cwd=source)
    metadata = json.loads(command("gh", "release", "view", tag, "--repo", REPO,
                                  "--json", "isDraft,isImmutable"))
    if metadata["isDraft"] or not metadata["isImmutable"]:
        raise RuntimeError(f"{tag} must be an immutable public release")
    directory = work / f"download-{tag}"
    directory.mkdir(mode=0o700)
    base = f"romd-{tag}-deployment.tar.gz"
    attestation = f"romd-{tag}-attestations.jsonl"
    command("gh", "release", "download", tag, "--repo", REPO, "--dir", directory,
            "--pattern", base, "--pattern", base + ".sha256", "--pattern", attestation)
    bundle = directory / base
    command("gh", "attestation", "verify", bundle, "--repo", REPO, "--signer-workflow", SIGNER,
            "--source-ref", f"refs/tags/{tag}", "--bundle", directory / attestation)
    image_list = directory / "attested-images.env"
    with tarfile.open(bundle, "r:gz") as archive:
        member = archive.getmember(f"{qa.bundle_root(tag)}/images.env")
        if not member.isfile() or member.size > 16_384:
            raise RuntimeError(f"{tag} has an invalid image map")
        image_list.write_bytes(archive.extractfile(member).read())
    install = work / f"install-{tag}"
    manifest = qa.verify_bundle(bundle, directory / (base + ".sha256"), image_list, install, revision, tag)
    pins = json.loads((source / "scripts/deployment/base-images.json").read_text())
    return install, manifest, pins["ingress"]


def qualify(work, evidence, baseline_source, candidate_source, candidate_tag):
    baseline_tag = "v0.1.0"
    baseline = qualifier(baseline_source, "romd_baseline")
    candidate = qualifier(candidate_source, "romd_candidate")
    server = json.loads(command("docker", "version", "--format", "{{json .Server}}"))
    if (server.get("Os"), server.get("Arch")) != ("linux", "amd64"):
        raise RuntimeError("Upgrade qualification requires a native Linux amd64 Docker daemon")
    old_root, old_manifest, old_ingress = download(baseline, work, baseline_source, baseline_tag)
    new_root, new_manifest, new_ingress = download(candidate, work, candidate_source, candidate_tag)
    os.environ.pop("GH_TOKEN", None)
    os.environ.pop("GITHUB_TOKEN", None)

    env = baseline.deployment_environment(work, old_root, old_ingress)
    upgrade_env = dict(env, COMPOSE_FILE=f"{new_root}/compose.yaml", ROMD_NGINX_IMAGE=new_ingress)
    rollback_env = dict(env, COMPOSE_PROJECT_NAME=env["COMPOSE_PROJECT_NAME"] + "-rollback")
    old = baseline.Runner(work, evidence, old_root, env)
    upgraded = candidate.Runner(work, evidence, new_root, upgrade_env)
    rolled_back = baseline.Runner(work, evidence, old_root, rollback_env)
    gate = candidate.ContainerLifecycle(env, (env["COMPOSE_PROJECT_NAME"], rollback_env["COMPOSE_PROJECT_NAME"]),
                                       old_ingress, evidence / "lifecycle.json")
    cleanups = []
    for runner in (rolled_back, upgraded):
        cleanups.append((runner.env["COMPOSE_PROJECT_NAME"],
                         lambda current=runner: current.compose("stop", stage="cleanup-stop"),
                         lambda current=runner: current.compose("down", "--volumes", "--remove-orphans",
                                                                stage="cleanup-project")))
    for runner in (old, upgraded, rolled_back):
        runner.control_authority()
    with gate.monitor(cleanups):
        source = baseline.RecoverySequence(old.run, old_root / "scripts/deployment/compose.sh", gate, env,
                                           first_start=lambda: baseline.quick_start(old, env))
        old.compose("pull", stage="pull-baseline-images")
        upgraded.compose("pull", stage="pull-candidate-images")
        source.start()
        old.inspect_images(old_manifest["images"])
        authority = old.ingress_file("ca.crt", "old-certificate-authority")
        old.trust("baseline", authority)
        old.browser("browser-initial", "none", baseline_tag)

        backup = source.backup_and_stop(old_root / "scripts/backup/backup.sh", work / "backups")
        target = candidate.RecoverySequence(upgraded.run, new_root / "scripts/deployment/compose.sh", gate, upgrade_env)
        target.start(phase="restart")
        upgraded.inspect_images(new_manifest["images"])
        upgraded.run([new_root / "scripts/deployment/verify.sh"], "upgraded-http-verification")
        upgraded.trust("upgraded", authority)
        upgraded.persisted_state = old.persisted_state
        upgraded.browser("browser-after-upgrade", "none", candidate_tag)

        with gate.stopping(upgrade_env["COMPOSE_PROJECT_NAME"]):
            upgraded.compose("down", stage="remove-upgraded-stack-before-rollback")
        rollback = baseline.RecoverySequence(rolled_back.run, old_root / "scripts/deployment/compose.sh", gate, rollback_env)
        rollback.restore(old_root / "scripts/backup/restore.sh", backup, rollback_env)
        rolled_back.inspect_images(old_manifest["images"])
        rolled_back.run([old_root / "scripts/deployment/verify.sh"], "rollback-http-verification")
        rolled_back.trust("rollback", authority)
        rolled_back.persisted_state = old.persisted_state
        rolled_back.browser("browser-after-rollback", "none", baseline_tag)
    return {
        "result": "passed",
        "baseline": {key: old_manifest[key] for key in ("sha256", "sourceCommit", "releaseTag", "images")},
        "candidate": {key: new_manifest[key] for key in ("sha256", "sourceCommit", "releaseTag", "images")},
        "persisted": old.persisted_state,
        "certificateAuthoritySha256": hashlib.sha256(authority.encode()).hexdigest(),
        "checks": ["worker-first-upgrade", "authenticated-content-preservation",
                   "pre-upgrade-backup-restore-to-baseline"],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-source", type=Path, required=True)
    parser.add_argument("--candidate-source", type=Path, required=True)
    parser.add_argument("--candidate-tag", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    args.evidence.mkdir(mode=0o700)
    with tempfile.TemporaryDirectory(prefix="romd-release-upgrade-") as name:
        result = qualify(Path(name), args.evidence, args.baseline_source.resolve(),
                         args.candidate_source.resolve(), args.candidate_tag)
    (args.evidence / "summary.json").write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
