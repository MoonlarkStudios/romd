# Source development

This is the canonical guide for a fresh development machine and daily source
work. Production operators use [release bundles](../self-hosting/production-deployment.md).
Host ownership and manual process startup belong to the
[split-host runbook](../architecture/split-host-topology.md); frontend-only work belongs to
[web development](../../web/docs/development.md).

For the shortest path, install the prerequisites below, clone the repository,
select your container engine, then run `mise run bootstrap`,
`mise run doctor --tests`, `mise run containers:smoke` and `mise run dev`.
Once ready, open the admin portal at `http://localhost:5137` (ordinary-checkout
default) and use the [development sign-in](#development-sign-in).
The remaining sections explain setup, alternate modes, ports and recovery.

## 1. Install host prerequisites

Install Git, Python 3.11 or newer, and [mise](https://mise.jdx.dev/getting-started.html).
Use the same user account for the checkout, mise, and container engine. Managed
commands use POSIX process groups and require Linux or macOS. On Windows, [install WSL2](https://learn.microsoft.com/en-us/windows/wsl/install)
and run server development there with a Linux checkout and Linux tools; native
Windows managed startup is unsupported. The native Flutter client has separate
[platform prerequisites](../../clients/romd_console/docs/development.md#native-target-prerequisites).

Choose one container engine:

| Environment | Setup |
| --- | --- |
| Fedora / native Linux with Podman | Rootless Podman and its user Unix socket; Fedora 44 with Podman 5.8.7 is the tested baseline |
| Linux with Docker | Docker Engine with Buildx, available to the development user |
| macOS | [Docker Desktop](https://docs.docker.com/desktop/setup/install/mac-install/) or Rancher Desktop configured for Docker/Moby, with its CLI and active context available |
| WSL2 | A native rootless Linux Podman setup with systemd, or Docker Desktop's WSL integration; run the smoke check in that distribution |

Podman Machine, rootful Podman, and remote Podman connections are not supported
by the development helper yet. WSL2 is a Linux execution route, not a claim of
completed Windows qualification. Production packaging, release qualification,
the CHD end-to-end harness, and the optional fuzz harness still require Docker.
Using Podman for development does not change those release gates.

For Fedora's Podman route:

```sh
sudo dnf install git python3 podman clang
systemctl --user enable --now podman.socket
podman info
```

The user socket is socket-activated after login; an always-running API service
or system-wide daemon is unnecessary. Keep SELinux enforcing. If rootless ID
mapping is not configured for the account, follow the
[Podman rootless setup](https://github.com/containers/podman/blob/main/docs/tutorials/rootless_tutorial.md).
The [Podman API guide](https://docs.podman.io/en/latest/markdown/podman-system-service.1.html)
describes socket activation. Do not expose that socket over unauthenticated TCP.

For Docker, follow its [Linux installation](https://docs.docker.com/engine/install/)
or the desktop application's setup. Start the engine and verify `docker info`
works as the development user. Preserve the intended Docker context and TLS
settings. Linux media tests additionally compile synthetic fixtures using a
host `clang`; GCC alone does not satisfy this prerequisite.

## 2. Restore the pinned tools and select the engine

From the repository root:

```sh
mise trust
mise install
```

Use `mise run` for repository tasks and `mise exec --` for individual tool
commands. Shell activation is optional for these forms; plain `dotnet`, `pnpm`,
or `flutter` commands require the pinned mise toolchain on the shell's PATH.

The root toolchain pins .NET, Node, pnpm, and the Docker Compose client. The
Compose client works with either engine; installing it does not install Docker
Engine. ROMD uses Docker Compose's JSON model and health/wait options.
`podman-compose` 1.6.0 lacks the JSON option and is not a compatible substitute.
Do not install a `docker=podman` alias for ROMD.

Create the ignored, build-excluded `.mise.local.toml` with your selection:

```toml
[env]
ROMD_CONTAINER_ENGINE = "podman"
```

Use `"docker"` for Docker. Preserve other local settings when editing an existing
file. This selection is per checkout; copy it deliberately to a new worktree.
When exactly one engine CLI is installed it can be selected automatically. If
both or neither are found, commands request an explicit selection. A failed
selected engine never causes a fallback to another engine or data store.

Then restore dependencies and check the machine:

```sh
mise run bootstrap
mise run doctor --tests
mise run containers:smoke
```

`bootstrap` restores repository-pinned tools, .NET dependencies, and the frozen
web lockfile. It preserves databases, application data, and local settings.
Add `--console` to bootstrap and doctor only for Flutter work. Root bootstrap
does not generate native platform runners or qualify native console binaries.

`doctor` defaults to source mode and reports selected tool versions, restored
dependencies, engine/API access, Compose JSON and required options, free loopback
ports, and source data path consistency. The dependency check fails with a
`mise run bootstrap` hint when `web/node_modules` is missing or does not match
`web/pnpm-lock.yaml` (compared with pnpm's installed lockfile copy or the digest
bootstrap records), or when `.config/dotnet-tools.json` tools are absent from the
NuGet package folder. It reads files only; it never installs. `dev`, `dev:source`
and `dev:containers` run the web part of this preflight before starting
PostgreSQL or building, so a fresh worktree fails in under a second instead of
after a full build. `doctor --mode containers` also checks pinned image-build URLs
and notes legacy images that `dev:clean-images` can reclaim.
Add `--tests` to also check host prerequisites such as Linux clang. Host clang
is needed by the full backend checks, not by container-backed application startup.
`containers:doctor` checks just the container prerequisites. Neither command
builds ROMD images or establishes that the application works.

`containers:smoke` builds a disposable context, verifies that `.env` and local
mise settings stay out of it, starts PostgreSQL and a non-root client, checks
health ordering, DNS and a loopback port, and verifies data survives container
recreation. It removes its own containers, network, and test volume; image
caches may remain. It does not reset or stop a development stack.

## 3. Start the application

Local .NET hosts with containerized PostgreSQL are the default for backend,
full-stack and agent work:

```sh
mise run dev
# Equivalent explicit name:
mise run dev:source
```

Use containerized .NET hosts for frontend work against a stable backend, image
changes, or reproducing container behavior:

```sh
mise run doctor --mode containers
mise run dev:containers
```

| Surface | Browser origin | Container API origin | Source API origin |
| --- | --- | --- | --- |
| Admin | `http://localhost:5137` | `http://localhost:11337` | `http://localhost:5000` |
| Consumer | `http://localhost:5174` | `http://localhost:11338` | `http://localhost:5002` |
| Player | `http://localhost:5175` | Static container | Vite server |

`dev:docker` remains a compatibility alias for `dev:containers` using the
selected engine. The pinned Compose provider builds the images. PostgreSQL and
the worker start first; API hosts wait for provisioning/seeders. The player is
a static container, while both portal Vite servers run locally. Backend and
player edits require stopping and rerunning container mode to rebuild images.

The table shows ordinary-checkout defaults; linked worktrees use their own port
range as described below.

Source mode uses the shared development PostgreSQL service and the database
identified by this checkout's canonical path. It builds and supervises the
worker and APIs. Backend input changes rebuild/restart all three hosts in
worker-first order; this is restart-based reload, not in-process hot reload.
It avoids the pinned SDK's [watch graph regression](https://github.com/dotnet/sdk/issues/54552).
Watched inputs are C#/project/JSON files plus the PNG/SVG presentation assets that
`Romd.Persistence` embeds from `reference-data/assets`. The console prints each
build/start/readiness phase. A failed reload (`reload-failed`) echoes the first
`error CS…`/`error MSB…` lines, retains build logs, and waits for the next edit.
If an owned backend host exits after readiness, the runner stops the remaining
backend hosts, keeps the Vite servers running, prints the host's log path and
last lines, and enters `backend-exited`. The next backend edit, or
`mise run dev:restart` (no edit needed), rebuilds and restarts worker-first.
A first-start build/host failure, dependency-watcher exit, or Vite exit ends the
run as `failed`, with the reason in `dev:status`. Managed startup compiles the
union of the selected apps' shared workspace dependencies once before starting
Vite, then owns one `web-dependencies` watch group per checkout, with one
TypeScript compiler per selected shared package. It also owns `web-admin`,
`web-consumer`, and (in source mode) `web-player`. Shared packages compile once
each even when several apps depend on them; backend reloads keep
these frontend processes running. Initial compilation is cancellable and bounded
to five minutes, with `build-web-dependencies.log` in the run directory.
Standalone `pnpm dev`, `pnpm dev:consumer`, and `pnpm dev:player` retain their
independent dependency preparation and watching.

Source mode preserves an explicit absolute `Romd__DataDirectory`, otherwise a
consistent absolute path from local host settings, otherwise `.data/romd`.
Ambiguous, relative, or unreadable settings fail before startup. It never
rewrites those settings or moves CAS content, signing keys, Data Protection
keys, or server identity. Container mode uses checkout-owned persistent volumes.
The two modes have separate databases and content stores; switching modes does
not transfer accounts or libraries. Changing engines likewise selects that
engine's storage. Use deliberate backup/restore for any data transfer.

Container build contexts exclude private host-local `appsettings` overrides.
Supply container settings through environment variables or explicit read-only
mounts; source mode reads existing local settings. Base settings and the tracked
consumer Development example remain build inputs. Use the
[development sign-in](#development-sign-in) below; never reuse these credentials
for a public deployment.

For repeatable UI and agent scenarios, use [development demo data](demo-data.md):
prepare the ROM/BIOS copies listed by your private `.demo/profile.json` and
authenticated DATs in ignored `.demo/` with
`mise run dev:demo:prepare --from /absolute/path/to/collection`, or copy
another checkout's prepared workspace into a worktree with
`--from-workspace /absolute/path/to/other/.demo`. Run `dev:demo:serve`, then
seed with `dev:demo`; from one terminal or agent shell, background the serve
command and use `dev:demo --wait` (see [start, seed and subscribe](demo-data.md#start-seed-and-subscribe)).
Ordinary demo runs read only the prepared manifest. Keep demo inputs separate
from durable application data.

The worker seeds the first administrator. For manual consumer access, create a library
in **Libraries**, use **Refresh library** to publish it, and assign the account
in **People**. Sign in again after changing its library assignment. An assigned
empty library shows an empty-games view; an unassigned account has no consumer
library. Browser play also needs a playable entry and configured runtime.

### Development sign-in

For a new development database, sign in to the **admin portal** with:

| Managed mode | Email | Password |
| --- | --- | --- |
| `mise run dev` / `dev:source` | `admin@localhost` | `ChangeMe123!` |
| `mise run dev:containers` | `admin@localhost` | `LocalDev123` |

Source mode accepts `Romd__DefaultAdminEmail` and `Romd__DefaultAdminPassword`
or local host settings. Container mode accepts `ROMD_DEFAULT_ADMIN_EMAIL` and
`ROMD_DEFAULT_ADMIN_PASSWORD`. Existing accounts keep their current password;
changing bootstrap settings does not reset it. See
[administrator recovery](../self-hosting/production-deployment.md#administrator-recovery)
if needed. Source and container modes have separate databases and accounts.
These defaults are for local development only.

## Ports, state, and shutdown

Linked Git worktrees automatically use five consecutive ports between 20000 and
59999, derived from the canonical checkout path. An ordinary checkout keeps the
table's defaults. Set `ROMD_DEV_PORT_BASE` to select a different five-port range:
admin API, consumer API, admin web, consumer web, player. Individual overrides
take precedence. Hash collisions and ports held by unrelated programs fail
actionably; select another base rather than stopping their owners.

Set `ROMD_DEV_ADMIN_PORT` and `ROMD_DEV_CONSUMER_PORT` for API ports, and
`ROMD_DEV_ADMIN_WEB_PORT`, `ROMD_DEV_CONSUMER_WEB_PORT`, and
`ROMD_DEV_PLAYER_PORT` for browser origins. Use the same values for doctor and
startup. Commands validate distinct unprivileged ports and configure matching
Vite proxies, CORS, OIDC callbacks/logout, and player parent/origin mappings.
They never stop an unrelated port owner.

For concurrent tasks, use separate worktrees, restore their dependencies
(`mise run bootstrap`), and select their engine as in initial setup. Prefer
sibling worktrees outside the checkout. A worktree nested inside it, such as
Claude Code's `.claude/worktrees/`, also inherits the parent checkout's
`.mise.toml` and `.mise.local.toml`, including its engine selection and sourced
environment; `mise config ls` there lists every file it loads. Unless ignored,
such a nested directory also shows as untracked in the parent and blocks its
`dev:smoke` clean-tree check. Initialize the shared PostgreSQL server
with `mise run db:up` before starting multiple source sessions. Each worktree
derives its own database and defaults to its own `.data/romd` content store;
avoid copying absolute data paths or private host settings from another checkout.
Explicit data path overrides still select that path and require deliberate
ownership. Shared PostgreSQL is a local development convenience, not a security
boundary between agents.

Discover the current checkout's endpoints and diagnostics without scraping logs:

```sh
mise run dev:status
```

This prints JSON with `schemaVersion` (currently 2), `active`, `state`, mode,
URLs, process IDs, run directory and current backend log directory. States
include `starting`, `ready`, `reloading`, `reload-failed`, `backend-exited`,
`stopping`, `stopped` and `failed`. In `reload-failed`, `backend-exited` and
`failed`, `error` holds a short reason (including the first compiler errors for
a build), `failedComponent` names the build or process (for example
`build-worker`, `admin`, `web-admin`) and `log` its log file. Before the first
run, `not-started` still reports the deterministic source-mode `urls`.
`ready` records successful startup/readiness; use API `/health/ready` for a fresh
health check. `active` probes the checkout lock rather than trusting a saved PID;
an unclean exit is reported as `interrupted` once that lock is released.
The private manifest is `.data/dev/session.json`; it contains no connection
strings or application credentials.

Ctrl-C stops only the managed run. A checkout lock prevents overlapping managed
runs. Container projects and networks are run-specific; labelled persistent
volumes belong to the checkout and survive shutdown. Images use one stable tag
per checkout, `localhost/romd-dev-<checkout id>-<service>:local`, labelled
`com.romd.dev.checkout`, so a rebuild replaces the tag and reuses the build cache;
the replaced image is removed only once it has no tag left and no container uses
it. Images from older runners were tagged per run and accumulate (~1 GB each run).
List and remove this checkout's legacy and untagged labelled images with:

```sh
mise run dev:clean-images --dry-run
mise run dev:clean-images        # add --all to also drop the current cached images
```

It matches only this checkout's ID, never forces removal, refuses while a managed
run is active, and never prunes globally. Source shutdown
terminates owned process groups and leaves the shared PostgreSQL service running.
No managed shutdown invokes `db:down`, `db:reset`, or global volume pruning.
After a crash, inspect containers labelled `com.romd.dev.checkout` with the
selected engine before restarting. Engine switches do not clean up the old
engine's resources.

Logs and rendered configuration live under private `.data/dev/runs/` directories.
Rendered configuration may contain credentials; do not publish or commit it.

## Tests and manual container commands

Use the normal validation commands in [contributing](../../CONTRIBUTING.md).
Local PostgreSQL-backed checks launched by `mise run test` and
`mise run test:integration` receive the selected runtime's Testcontainers
endpoint. Ryuk cleanup stays enabled. CI retains its Docker selection.

Mise test tasks call `scripts/validation/run.py` with a group from
`scripts/validation/checks.json`. To see or rerun part of a group, prefer a
registered check over a hand-written command; it uses the same command, working
directory and container environment as the full group:

```sh
python3 scripts/validation/run.py --list --group backend
mise exec -- python3 scripts/validation/run.py --id backend.storage
mise exec -- python3 scripts/validation/run.py --group backend --keep-going
```

`--id` may be repeated. Runs stop at the first failure by default;
`--keep-going` runs the remaining checks and still exits non-zero.

Each run prints its evidence directory, named
`.artifacts/validation/<UTC time>-<selector>-<random>/`, which holds one log
per check and `results.json`. The record is rewritten after every check and
names the selector, the HEAD `revision`, each check's status
(`passed`, `failed`, `setup-failed`, `interrupted` or `not-run`) and duration,
the overall `status`, and the `notRun` check IDs. A run stopped by Ctrl-C or
SIGTERM keeps a partial record with status `interrupted`.

`dirty: true` means the checks ran against uncommitted changes on top of
`revision`. In that case `tree` stores hashes of `git diff HEAD` and the
untracked, non-ignored files, plus their count. It does not store diffs, paths
or file contents. `treeChangedDuringRun` reports edits made while checks were
running. For PR evidence, commit first and run again, or say that the result
covers uncommitted changes.

For ad hoc runs without a registered check, such as a `dotnet test --filter`,
or for an IDE terminal, use the scoped container wrapper:

```sh
mise exec -- python3 scripts/containers/runtime.py test dotnet test tests/Romd.Hosting.IntegrationTests
```

The helper supplies Podman's `DOCKER_HOST` and
`TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` only to children, removes a conflicting
Docker context there, and rejects a socket belonging to another engine or a
mismatched reaper socket. An inherited Podman `DOCKER_HOST` must resolve to the
same Unix socket reported by native `podman info`; equivalent symlink paths
are accepted and canonicalized. Another Podman socket is rejected even if it
reports the same engine name, so native builds/volumes and API clients share
one instance. Do not put these overrides or
`TESTCONTAINERS_RYUK_DISABLED` in global shell configuration. Docker preserves
its selected context/TLS configuration.

Native Linux build tasks use the same engine. The shared entry points are:

```sh
mise exec -- python3 scripts/containers/runtime.py engine ps
mise exec -- python3 scripts/containers/runtime.py compose -f compose.dev.yaml config --quiet
```

A custom `ROMD_COMPOSE_PROVIDER` may identify one executable path; it is not a
shell command. Doctor still requires the same Compose capabilities. Prefer the
pinned client installed by mise.

The managed runner uses the shared Python runtime API directly, resolving the
engine, native socket and provider once for builds, volume ownership checks,
Compose operations and cleanup. Bash and standalone commands use its thin CLI.

For Podman children the adapter selects `DOCKER_BUILDKIT=0`: the pinned Compose
provider's classic API builder is qualified against ROMD's actual image targets.
This avoids automatic fallback when Buildx is absent and removes a separate
Compose-to-native-build translation layer. Docker retains its existing builder
configuration. Changes introducing BuildKit-only features require qualifying
the Podman path again. The classic builder may evaluate unused Dockerfile
stages, so cold container builds can do more work than native target builds.

`podman compose` with the pinned provider was also exercised; it delegates to
that same executable. ROMD invokes the provider directly with the resolved
environment to avoid another provider-selection layer. See
[Podman's provider wrapper](https://docs.podman.io/en/latest/markdown/podman-compose.1.html).
Use `dev:containers` for the managed application lifecycle.

Development infrastructure checks run through `mise run dev:check` and join
`mise run agents:check`. After changing the helper, also run
`mise run containers:smoke` against each available supported engine. A Podman
pass does not establish Docker, macOS, WSL2, complete backend-suite, or release
qualification. Search [known issues](known-issues.md) before retrying unexpected
failures; use the retained local diagnostics without weakening a gate.

## Source workflow qualification

After source-runner or checkout-isolation changes, run the opt-in real-host check:

```sh
mise run dev:smoke
mise run dev:smoke --parallel
```

The task exercises **committed HEAD** and refuses tracked changes or non-ignored
untracked files; commit the intended implementation before running it. Ignored
local files do not block it and are not copied. It creates disposable linked
worktrees outside the checkout, restores pinned .NET/web dependencies, and starts
its own PostgreSQL Compose project on a free loopback port. It strips inherited
application credentials, data paths, browser/DB port overrides and Compose project
settings. The ordinary checkout, its content and its shared development PostgreSQL
server are not used by the test.

Each worktree uses its automatic port range and default data path. The check
verifies worker-first readiness, all five HTTP endpoints, authorization-code/PKCE
login and token exchange for both surfaces, and matching CORS origins. A temporary
shared-library TypeScript module must compile to `dist`, and all three Vite
servers must serve its changed value while owned process IDs stay stable. The
parallel variant also verifies the other checkout keeps its original compiled
value. It adds a temporary backend source file, exercises rebuild/restart, deliberately causes a
compiler error, verifies discoverable diagnostics and the status `error`, and
corrects it to recover. It then kills the admin host, expects `backend-exited`
with the siblings stopped, and recovers through `dev:restart`. Vite processes
and the shared dependency watch group must survive these backend edits and the crash. It then proves owned shutdown,
closed ports, and preserved database/content sentinels before removing fixtures.

`--parallel` starts two complete stacks together. It also checks distinct
databases and content paths, rejects tokens/origins/callbacks from the other
checkout, and verifies that editing or stopping one stack leaves the other
running. Login checks use independent cookie jars. This qualifies process,
database and origin isolation; it is not browser UI or media playback testing.

Allow several minutes, dependency downloads, and enough RAM/disk for two builds
when using `--parallel`. `--timeout SECONDS` bounds each setup/lifecycle phase
(default 900). Git worktree metadata and normal tool caches must be writable;
an agent sandbox may require scoped execution with local sockets and cache access.

Every run retains a private directory under `.artifacts/dev-smoke/` with
`summary.json`, the tested revision/environment, completed stages, setup logs and
per-worktree host/build logs. Use `--evidence-parent PATH` to choose another parent.
Treat raw diagnostics as private. Successful cleanup removes only that run's
worktrees, PostgreSQL containers/network/volume and data. If shutdown cannot be
proved, the task fails and preserves the owned fixtures for inspection instead
of deleting a live checkout. It never invokes global process kills or pruning.

Fast harness regression tests run with `dev:check` and `agents:check`; the real
smoke command is opt-in and does not replace backend, integration, web, console
or release gates. A pass qualifies only its recorded platform and engine.
