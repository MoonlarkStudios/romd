# AGENTS.md - ROMD

ROM management platform: .NET 10, React 19 + Mantine 9, Flutter console,
PostgreSQL 18, Hangfire, and IGDB enrichment. Admin, consumer, and worker hosts
have separate contracts.

## Working Contract

- Inspect `git status --short`; preserve existing work and keep changes scoped.
- For substantive changes, identify observable acceptance criteria, affected
  contracts, and meaningful validation before editing. Scale planning to risk.
- Read the surface guidance below even when starting from the repository root.
  Paths here are repository-relative; scoped documents may use local paths.
- Verify the surface you touch. Report exactly what passed, failed, or was not
  run. A narrow check does not establish a full-suite pass.
- Fix warnings that indicate real risk; flag pre-existing or out-of-scope
  warnings. Do not weaken checks or silence warnings to get green output.
- On unexpected failure, search `docs/development/known-issues.md`, then
  `docs/development/known-issues-archive.md` (resolved issues), by command/error and read
  the matching section before retrying or changing behavior.
- Use [the documentation index](docs/README.md) for current runbooks and decisions.
  Verify plans and dated reports against current source; load only relevant skills.

## Surface Routing

- Backend, contracts, persistence, storage, tests: `src/AGENTS.md` and
  relevant sections of [the .NET standards](docs/development/dotnet-development.md);
  test work also reads `tests/AGENTS.md`.
- Web apps and generated TypeScript clients: `web/AGENTS.md`.
- Console: `clients/romd_console/AGENTS.md` plus relevant nested instructions.
- Emulator/runtime: also read
  `clients/romd_console/lib/src/play/emulator/AGENTS.md`.
- Documentation, instructions, runbooks: `docs/AGENTS.md`.

Conditional workflows:

- Backend/OpenAPI/generated-client changes:
  `.agents/skills/romd-web-api-client/SKILL.md`.
- Console emulator/runtime changes:
  `.agents/skills/romd-console-runtime/SKILL.md`.
- Cross-surface, migration-sensitive, validation-routing, or instruction changes:
  `.agents/skills/romd-plan-change/SKILL.md`.
- Newly confirmed, recurring environment/tooling issues:
  `.agents/skills/romd-retro/SKILL.md`.

## Validation

Backend commands run from the root, web from `web/`, console from
`clients/romd_console/`.

| Surface | Default checks |
| --- | --- |
| Documentation links/anchors/assets, agent configuration and instructions | `mise run agents:check` |
| Backend | `mise run dotnet:style -- --base origin/main`, then `mise run test` (configured container engine required for PostgreSQL Testcontainers) |
| Host behavior/topology, worker registration, integration fixtures, deployment composition | Backend checks plus `mise run test:integration` |
| Web | `pnpm lint`, `pnpm test`, `pnpm build` |
| Backend/OpenAPI contracts, deployment Compose, CHD harness | `mise run contracts:check` (commit generated clients first; the Docker-only build-context export skips with a reason on other engines) |
| Console | `mise run analyze`, `mise run test` |

`mise run build` packages backend and frontends. `mise run test:all` combines
backend deterministic and integration suites; web/console checks are separate.
`mise run test:mount-dev` checks the parked bearer-based mount helper, outside
supported-client qualification.

Use `pnpm install --frozen-lockfile` when web dependencies are needed and
`pnpm api:update` for backend/OpenAPI changes that affect clients.
Route new backend test projects through a `scripts/validation/checks.json` check
in group `backend` (run by `mise run test`) in the same change; keep specialized
opt-in projects in their explicit group.

## Contract Boundaries

- Compile-time dependencies point inward: Application depends on Domain;
  Persistence/Infrastructure implement application-owned ports; host composition
  wires adapters. Domain has no external dependencies.
- Public IDs use `Sqid`; internal IDs are `int`.
- Generated web clients are derived artifacts. Change backend/OpenAPI sources
  first and regenerate; never hand-edit generated contracts.
- Consumer contract changes include the console's handwritten Dart client and
  its analysis/tests; generated TypeScript drift checks do not cover it.
- Console SQLite schema, local auth/profile storage, runtime profile IDs, and
  controller preferences require migration-aware changes. Pre-1.0 exception:
  the owner approved the fresh console SQLite v17 baseline with no v16 upgrade
  path on 2026-10-31; unsupported databases are refused. See the console
  [persistence rules](clients/romd_console/docs/architecture.md#persistence-acquisition-and-cleanup).
- Read `docs/architecture/split-host-topology.md` before changing host ownership/composition.
  Only the worker migrates and provisions schemas; API readiness fails closed.

## Operations, Delegation, And Git

[Development setup](docs/development/getting-started.md) owns local hosts (`mise run dev`),
worktree isolation, `dev:status`, shutdown and `dev:smoke` qualification;
[demo setup](docs/development/demo-data.md) owns the ignored `.demo/` workspace.
Agent constraints:

- Use one worktree per concurrent task. Never use global process kills,
  `db:down`, resets or volume pruning as task cleanup.
- Commit development-runner or demo-tooling changes, then run
  `mise run dev:smoke` (it tests committed HEAD); keep its evidence private.
- Never commit private ROMs, BIOS, inventory, credentials or raw evidence.
- Hosted CI gates every pull request. Cite its run, add local evidence for
  checks it does not cover, and diagnose failures; do not rerun or weaken them.

Host ownership and local PostgreSQL: [split-host runbook](docs/architecture/split-host-topology.md).
Images, production Compose, backup/restore: [deployment runbook](docs/self-hosting/production-deployment.md).

Use one agent by default. Delegate only when the user explicitly requests
subagents, parallel work, or a named ROMD agent. Optional roles in
`.codex/agents/`: `romd-scout` (exploration), `romd-implementer` (owned changes),
`romd-reviewer` (correctness/evidence), `romd-librarian` (documentation).
Assign bounded questions or owned file scopes; the primary agent integrates and
verifies. Models/reasoning belong in personal config or explicit user choices.
See [the agent workflow](docs/development/agentic-development.md) for setup and maintenance.

Single-line conventional commits: `type(scope): description` using `feat`,
`fix`, `refactor`, `test`, `docs`, or `chore`.
