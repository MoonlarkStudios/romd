# Contributing to ROMD

Thanks for your interest in ROMD! Bug reports, ideas, documentation fixes and
code are all welcome. This guide covers how to propose a change, keep it
reviewable, and show that it works.

Please follow the [code of conduct](CODE_OF_CONDUCT.md). Its private reporting
address is for conduct concerns; [Security](SECURITY.md) covers vulnerabilities.

## Before you start

- **Set up a checkout** with the [development guide](docs/development/getting-started.md).
  `mise run dev` runs local .NET hosts with containerized PostgreSQL;
  `mise run dev:containers` runs a stable containerized backend instead.
- **Find your way around** with the [documentation index](docs/README.md). Each
  surface has its own instructions: [backend](src/AGENTS.md),
  [web](web/AGENTS.md), [console](clients/romd_console/AGENTS.md) and
  [tests](tests/AGENTS.md). [`AGENTS.md`](AGENTS.md) is the repository-wide
  summary, used by people and coding agents alike.
- **Design backend changes** with the [.NET development standards](docs/development/dotnet-development.md):
  preserve invariants, keep policies pure and effects explicit, and use the
  linked decisions for contracts and mutation ownership.
- **Working on several things at once?** Use one Git worktree per task. Each
  gets its own ports; find them with `mise run dev:status`.

## Issues

Use the [issue forms](https://github.com/MoonlarkStudios/romd/issues/new/choose):
a bug report for a reproducible problem, a feature request for an outcome or
use case. Include the release tag or commit, your environment, expected
behavior and a minimal reproduction.

Keep vulnerabilities, credentials, private service addresses and personal data
out of public issues. See [SECURITY.md](SECURITY.md) for private reporting.

## Making a change

Keep each pull request focused on one behavior. Before substantial work, write
down the observable acceptance criteria, the contracts it affects and how you
will validate it. Follow the existing architecture.

Some boundaries need extra care:

- **Hosts.** The admin and consumer APIs have separate ownership and
  authentication contracts. Only the worker migrates and provisions database
  schemas; the APIs fail readiness until provisioning is complete.
- **Identifiers.** Public IDs use `Sqid`. Changing storage IDs or persisted
  console/profile identities needs deliberate compatibility or migration work.
- **Generated clients.** Web API clients are generated from the backend's
  OpenAPI documents. Regenerate them with `pnpm api:update` in `web/`; never
  hand-edit them.
- **Content and notices.** Keep supplied licenses and notices. Use synthetic
  media for reproductions. Never attach ROMs, BIOS files, private screenshots or
  logs containing secrets.

Commits use a single-line conventional message: `type(scope): description`,
where type is `feat`, `fix`, `refactor`, `test`, `docs` or `chore`. For
example: `fix(web): prepare client exports before development startup`.

## Validating

Run the checks for every surface you touch, from the listed directory:

| Change | Directory | Checks |
| --- | --- | --- |
| Documentation links/anchors/assets, instructions, validation config | Repository root | `mise run agents:check` |
| Backend | Repository root | `mise run dotnet:style -- --base origin/main`, then `mise run test` |
| Host composition, integration fixtures, worker registration, deployment topology | Repository root | Backend checks plus `mise run test:integration` |
| Web | `web/` | `pnpm lint`, `pnpm test`, `pnpm build` |
| Backend/OpenAPI changes affecting web clients | `web/` | `pnpm api:update`, the web checks, and no unexplained generated drift |
| Backend/OpenAPI contracts, deployment Compose, CHD harness | Repository root | `mise run contracts:check` (commit regenerated clients first; the Docker-only build-context export skips with a reason on other engines) |
| Console | `clients/romd_console/` | `mise run analyze`, `mise run test` |

PostgreSQL-backed checks need a configured container engine (see the
[development guide](docs/development/getting-started.md)). Production packaging
and release checks require Docker. Linux media tests compile synthetic fixtures
with a host `clang` (GCC alone is not enough); native inputs are described in
the [libchdr build guide](native/libchdr/BUILD.md). Console platform
prerequisites are in the [console development guide](clients/romd_console/docs/development.md).

Add or update tests for changed behavior. For documentation and other
low-risk edits, use meaningful link, render or command checks. New backend test
projects must be routed through `scripts/validation/checks.json` in the same
change.

**When something fails unexpectedly,** search
[known issues](docs/development/known-issues.md) and then the
[resolved-issue archive](docs/development/known-issues-archive.md) for the
command or error before retrying. Fix real regressions. Don't weaken a check,
silence a warning or skip a test just to get green output.

## Opening a pull request

Describe the problem and the resulting behavior. For validation, record the
commit, command, directory, environment, result and where the artifacts are.
Registered checks write evidence under `.artifacts/validation/`. Say which
evidence is local, hosted (GitHub Actions) or manual, and list checks you did
not run. A filtered test run is not a full-suite pass.

Call out any affected API/auth, database, generated-client, storage or console
identity contracts, and any migration impact. Security-sensitive changes,
validation routing and release publication get independent review. Attach only
sanitized summaries of diagnostics.

Maintainers: the GitHub settings this repository relies on are recorded in
[repository settings](docs/maintainers/repository-settings.md).
