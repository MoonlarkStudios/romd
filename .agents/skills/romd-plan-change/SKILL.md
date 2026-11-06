---
name: romd-plan-change
description: Use when planning or implementing ROMD cross-surface changes, validation routing changes, host/API topology changes, generated-client contract changes, migration-sensitive console changes, or updates to project instructions and runbooks.
---

# ROMD Plan Change

Use this skill for changes that need coordination across ROMD surfaces or
migration/validation boundaries. Keep routine local edits lightweight.

## Workflow

1. Inspect existing work and read the closest `AGENTS.md` files for every
   touched surface. Backend tests also route through `tests/AGENTS.md`.
2. Convert the request into observable acceptance criteria before naming files
   or workers.
3. Use `docs/README.md` to select relevant runbooks and decisions. Search
   `docs/development/known-issues.md`, then `docs/development/known-issues-archive.md`, for the
   command/error before interpreting unexpected validation failures; read the
   matching section rather than the entire file.
4. Classify touched surfaces: backend, integration/host topology, web, console,
   emulator runtime, docs/instructions, or CI/release validation.
5. Build a risk map: persisted contracts, generated artifacts, host/API
   boundaries, schema migrations, auth/storage boundaries, and likely test gaps.
6. Name the minimum validation set for those surfaces before editing, using
   the root validation table. Backend/OpenAPI or deployment/Compose changes
   include `mise run contracts:check`. Development-runner and demo-tooling
   changes need real lifecycle qualification: commit, then run
   `mise run dev:smoke` with the variants that
   [source qualification](../../../docs/development/getting-started.md#source-workflow-qualification)
   names; unit/structural checks alone do not establish that behavior.
   Container adapter or image changes also need the affected engine smoke/build checks.
7. If the change adds a backend test project, include its
   `scripts/validation/checks.json` registration (group `backend` for
   `mise run test`) in the same plan.
8. Treat generated clients as derived artifacts. Plan backend/OpenAPI changes
   before client regeneration; do not hand-edit generated files.
9. For migration-sensitive areas, identify persisted contract keys and schema
   migrations before implementation.
10. For instruction/configuration routing changes, run `mise run agents:check`.
    Its structural checks do not prove runtime behavior or skill quality.
11. If the user requests delegation, assign bounded questions or disjoint owned
    file scopes. Keep shared generated artifacts under one owner. Otherwise,
    carry out the plan with one agent.

## Development Execution

[Development setup](../../../docs/development/getting-started.md) owns environment, stack,
worktree and qualification commands; [demo setup](../../../docs/development/demo-data.md)
owns `.demo/` preparation and seeding; the [agent workflow](../../../docs/development/agentic-development.md)
owns agent constraints and evidence. A focused build or registered check need
not start a stack. Plan one worktree per concurrent task, and never plan global
process kills, `db:down`, resets or pruning as cleanup.

## Output

Return a compact plan with:

- acceptance criteria,
- touched surfaces,
- risk map,
- verification matrix with commands, scope, and pass/fail evidence expected,
- delegation scopes only when requested,
- known-issues entries that may affect interpretation,
- explicit follow-ups that should not be mixed into the current task.

Keep the plan proportional to the change; omit empty boilerplate. Update a
persistent plan only when it helps continuation or review.
