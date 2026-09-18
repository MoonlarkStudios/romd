# AGENTS.md - ROMD

ROM management platform. .NET 10 backend (Clean Architecture, CQRS, ErrorOr),
React 19 + Mantine 8 frontend, and a Flutter console client. PostgreSQL 18
persistence, Hangfire jobs, IGDB enrichment. Monorepo: `src/`, `web/`,
`clients/romd_console/`, and `tests/`.

## Prime Directives

- Run meaningful verification for the surface you touch.
- Do not overclaim partial signals. Report exactly what passed, failed, or was
  not run.
- Fix warnings when they indicate real risk. If a warning is pre-existing or out
  of scope, flag it; do not silence warnings just to get green output.
- When validation fails unexpectedly, read `docs/known-issues.md` before retrying
  or changing behavior.

## Project Status

ROMD is actively evolving. Backend host/API boundaries are migration-sensitive:
admin, consumer, and worker hosts have different contracts.
Generated web API clients are derived artifacts, not hand-edited contracts.
Console SQLite schema, local auth/profile storage, runtime profile ids, and
controller preference storage are migration-sensitive.

## Routing Map

- Backend domain/application/infrastructure/hosting/contracts/storage/tests:
  `src/`, `tests/`; also read `src/AGENTS.md`.
- Admin and consumer web apps plus generated TypeScript API clients: `web/`;
  also read `web/AGENTS.md`.
- Backend endpoint, OpenAPI, or generated web API client changes: use
  `.agents/skills/romd-web-api-client/SKILL.md`.
- Console client: `clients/romd_console/` plus its nested `AGENTS.md` files.
- Emulator/runtime work: also read
  `clients/romd_console/lib/src/play/emulator/AGENTS.md` and consider
  `.agents/skills/romd-console-runtime/SKILL.md`.
- Planning or cross-surface changes: consider
  `.agents/skills/romd-plan-change/SKILL.md`.
- Validation retros or durable environment facts: use
  `.agents/skills/romd-retro/SKILL.md`; append to `docs/known-issues.md` only
  for a newly confirmed issue.

Custom Codex agents live in `.codex/agents/`:

- `romd-scout`: read-heavy exploration and risk mapping.
- `romd-implementer`: scoped implementation with touched-surface validation.
- `romd-reviewer`: correctness review, validation-output checking, and warning
  scrutiny.
- `romd-librarian`: docs, instructions, skills, and runbook updates.

Use these agents only when the user explicitly asks for subagents, parallel
agent work, or a named ROMD agent.
Project agent TOMLs do not pin models; choose cost tiers at spawn time or in
personal Codex config.

## Build And Test

Run from the repo root unless noted.

```bash
mise run build
mise run test
mise run test:integration
mise run test:all
```

Use `mise run test` as the default backend safety check; it needs Docker
running because the infrastructure suite exercises real PostgreSQL through
Testcontainers and fails fast without it. Run
`mise run test:integration` when a change touches host topology, deployment
composition, Hangfire worker registration, integration fixtures, or
admin/consumer host behavior. Use `mise run test:all` when full release-style
backend confidence is needed.

Run frontend checks from `web/`:

```bash
pnpm install
pnpm lint
pnpm test
pnpm build
pnpm api:update
```

Run console checks from `clients/romd_console/`:

```bash
mise run analyze
mise run test
```

If you add a backend test project, add it to `.mise.toml` `tasks.test` in the
same change so deterministic backend validation does not drift.

## Run

Start the local PostgreSQL dev server first. The worker and both API hosts
require it: the application database and Hangfire storage live in this
checkout's database, and the API hosts fail `/health/ready` closed until the
worker has migrated and provisioned both schemas.

```bash
mise run db:up
```

`db:down` stops the shared server, `db:reset` returns this checkout to
first-run state, `db:psql` opens psql, `db:snapshot`/`db:restore` manage
template-based snapshots. Each checkout/worktree gets its own database on the
shared server. Details in `docs/split-host-topology.md`.

`.mise.toml` derives `ConnectionStrings__Romd`, `ConnectionStrings__Hangfire`,
and their `*Provisioning` counterparts for that database, so a mise-activated
shell already has them. Without shell activation, run
`eval "$(mise run db:env)"` in every terminal that runs a host.

Set the shared data directory in every terminal that runs a host:

```bash
export Romd__DataDirectory="$PWD/.data/romd"
```

Run the split hosts separately, worker first; it migrates the application
schema and provisions the Hangfire schema. Production deployment uses the
image-only `compose.yaml`; see `docs/production-deployment.md`.

```bash
dotnet run --project src/Romd.Worker.Host
dotnet run --project src/Romd.Admin.Host --urls http://localhost:5000
dotnet run --project src/Romd.Consumer.Host --urls http://localhost:5002
```

Run frontend dev servers from `web/`:

```bash
pnpm dev
VITE_ROMD_CONSUMER_API_ORIGIN=http://localhost:5002 pnpm dev:consumer
```

See `docs/split-host-topology.md` for the full topology runbook. Deployment
backup and restore live in `scripts/backup/` with the runbook in
`docs/production-deployment.md`.

## Architecture

- Clean Architecture layers: Domain -> Application -> Persistence and
  Infrastructure -> Host. Domain has zero external dependencies. Application
  defines interfaces; Persistence (EF Core) and Infrastructure implement them.
- Backend host/API boundaries are split between admin, consumer, and worker
  hosts. See `src/AGENTS.md` and `docs/split-host-topology.md`
  before changing host composition or endpoint ownership.
- Public IDs use `Sqid`; internal IDs are `int`.
- Generated web API clients are derived from backend OpenAPI output. Do not
  hand-edit generated client files.

## Git

Semantic conventional commits, single line.

```text
type(scope): description
```

Types: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`.
