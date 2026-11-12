# Packaged release qualification

This gate qualifies the downloaded deployment bundle and its published Linux
amd64 image digests. A source Compose smoke, an HTTP login, a passing harness
unit test, or `--verify-only` does not qualify an installation. Final publication
also requires the plan's maintainer checkpoint and release metadata verification.

Use a disposable native Linux amd64 runner with Docker Compose v2.24+, Python
3.11+, OpenSSL, Node 24+, and outbound access to the published registry and the
configured EmulatorJS runtime. The runner must permit Docker to bind an unused
address in Linux's `127.0.0.0/8` loopback range on port 443. Each run chooses a
random address and unique Compose project. This preserves the downloaded HTTPS
template's canonical port without occupying another deployment's loopback
socket. A bind, pull, startup, or cleanup failure blocks qualification.

Install browser tools on the qualification runner, outside the extracted bundle:

```bash
cd scripts/release/browser
npm ci --ignore-scripts
npm run install:browser
npm test
```

`package-lock.json` pins Playwright and its browser revision. The installation
bundle needs no Node, browser tools, source checkout, or development SDK.

Download the prerelease bundle and its checksum from the intended repository
and tag. Obtain the expected image map from the publisher's independently
recorded digest output; copying `images.env` out of the same archive is not an
independent comparison. Use the full allowed source commit and exact release
tag recorded by release preflight. From the qualification checkout root:

```bash
python3 scripts/release/qualify.py \
  --bundle /absolute/download/romd-v1.2.3-rc.1-deployment.tar.gz \
  --checksum /absolute/download/romd-v1.2.3-rc.1-deployment.tar.gz.sha256 \
  --expected-images /absolute/publisher-evidence/images.env \
  --publisher-state /absolute/publisher-evidence/candidate.json \
  --expected-revision FULL_40_CHARACTER_COMMIT_SHA \
  --expected-tag v1.2.3-rc.1 \
  --nginx-image REGISTRY/NGINX@sha256:REVIEWED_64_CHARACTER_DIGEST \
  --evidence-dir .artifacts/release/UNIQUE_RUN/positive
```

The evidence directory must be new. Archive verification rejects checksum or
manifest mismatches, missing files, unexpected inventory, unsafe paths, links,
source files, installation secrets, mutable image references, and a mismatch
with the publisher's image map. `manifest.json` schema 1 binds `sourceCommit`,
`releaseTag`, `platform: linux/amd64`, `images`, and every other regular file's
relative path and SHA-256 in `files`. The manifest excludes itself from `files`.
Operational documentation includes installation, configuration and media processing.
Included references stay local; omitted source references are pinned to the
manifest's `sourceCommit`. Bundle generation validates local paths and source
anchors before hashing the rewritten documents. This link check is separate
from the downloaded-installation runtime gate below.
The checksum verifies download integrity; provenance establishes origin through
the separate release metadata gate.

The qualifier uses only the extracted deployment files. It clears inherited
ROMD/Compose settings, generates private disposable credentials, pulls the
digest references, provisions PostgreSQL and worker first, and starts the APIs,
player and HTTPS ingress. It inspects pulled image architecture and digest and
each running container's image ID and configured reference. It refuses an ARM
Docker daemon, including amd64 emulation on that daemon.

The browser trusts only the generated fixture certificate's SPKI and maps the
three fixture hostnames to this run's loopback address. Chromium receives explicit
screen wake-lock permission for the consumer/player origin pair within that
disposable browser context, since headless mode denies permission prompts.
It loads the real portal
JavaScript, signs in through each portal's button and the server's login form,
observes each authorization callback, and requires the authenticated profile
response and rendered authenticated view. It imports an original synthetic
NES NROM cartridge and matching DAT through the admin API, creates and publishes
an assigned library with an explicit policy allowing its unrated content, and
selects **Play Now** in the consumer portal.

The actual isolated player must send its protocol hello, ready, and game-started
events. The runner clicks the runtime's real **Start game** control and requires
both the player's `body[data-playing]` and consumer's playing state. The passive
protocol observer calls native `MessagePort.postMessage` with the original
arguments; it never supplies a handshake, ROM, runtime, or startup event. The
fixture generator contains only ROMD's small original NROM program, zero CHR,
and reset vectors. No commercial ROM, BIOS, artwork, or downloaded game is used.

The qualifier repeats browser sign-in and startup after a worker-first restart.
It then runs the bundle's own backup tool, stops the source project before
exposing its restored identity, restores into a second empty project with the
bundle's restore tool, and repeats the browser gate. Both projects' volumes and
networks are removed on exit. Backup contents and credentials remain private in
temporary storage and are never copied into public evidence.

The same continuous lifecycle gate used by the source smoke covers initial
startup, manual restarts, the backup tool's nested stop/resume operations, restore
and final cleanup. It requires complete create/start/termination/destruction
coverage and exit code zero for every started container. An OOM, forced kill,
unexpected exit or restart, lost or malformed event stream, missing observer
acknowledgment or cleanup failure blocks qualification even when functional
checks and Compose succeed. The sanitized `lifecycle.json` records that result.
Bounded container logs and state stay private under
`.artifacts/container-lifecycle-private/` in the qualification checkout and are
captured before container removal.

Only the initial browser pass may create the synthetic fixture. Restart and
restore passes require the existing library, account, title and release; they
never recreate missing content. They compare the original server instance and
persisted IDs by SHA-256, verify the manifest's content hash, and hash the actual
downloaded cartridge bytes. A reset database or lost content cannot pass through
bootstrap/reimport. Restored running container identities are checked again.

After a positive run, perform **two separate new disposable runs** with the same
inputs and new evidence directories, adding `--inject-fault portal-asset` or
`--inject-fault player-startup`. Each command must exit nonzero. Then run:

```bash
python3 scripts/release/assert_failure.py \
  --evidence-dir .artifacts/release/UNIQUE_RUN/broken-portal --fault portal-asset
python3 scripts/release/assert_failure.py \
  --evidence-dir .artifacts/release/UNIQUE_RUN/broken-player --fault player-startup
```

The portal fault returns 503 for actual admin assets. The player fault returns
503 for the actual EmulatorJS loader, after the real handshake. The assertion
requires a delivered fault and failing required asset evidence; the player case
also requires both completed callbacks and authenticated views plus the real
player hello. An unavailable browser, failed deployment, or unrelated early
failure cannot satisfy a negative drill. Both injected-failure assertions also
require complete, passing lifecycle evidence, so an unrelated container crash
cannot count as successful fault detection. These faults affect browser routing
only in that disposable run and never modify released images or files.

`qualification.json` records the bundle/revision/tag/digests, actual container
image identities, command argv with temporary paths replaced, working directory,
environment type, exit codes, elapsed time and output hashes. Each browser pass
retains `browser-diagnostics.json` on success or failure. Required asset/config
HTTP failures, invalid script MIME types, CSP violations, console errors and
unhandled browser errors fail the gate. Browser records contain only fixed event
codes, surface names, status codes, extension classes and one-way fingerprints.
They exclude URL query strings, literal paths, OAuth codes, cookies, bearer
tokens, passwords, page contents, screenshots, HAR files and raw console logs.
Do not upload temporary credential/configuration or backup directories.

Run the deterministic guards from the checkout root:

```bash
python3 scripts/release/test_qualify.py
node --test scripts/release/browser/*.test.mjs
```

These checks cover unsafe/tampered bundles, revision/platform/digest binding,
operator-environment isolation, new evidence directories, required-asset
classification, observed startup prerequisites, fault delivery assertions and
diagnostic redaction. They do not replace a positive packaged installation and
the two injected-failure runs on the final downloaded prerelease. Keep local,
hosted, disposable-release and manual evidence separate in the release plan.
