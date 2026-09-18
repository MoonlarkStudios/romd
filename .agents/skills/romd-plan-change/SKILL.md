---
name: romd-plan-change
description: Use when planning or implementing ROMD cross-surface changes, validation routing changes, host/API topology changes, generated-client contract changes, migration-sensitive console changes, or updates to project instructions and runbooks.
---

# ROMD Plan Change

Use this skill to keep planning work aligned with ROMD's current validation and
migration boundaries.

## Workflow

1. Read the closest `AGENTS.md` files for every touched surface.
2. Convert the request into observable acceptance criteria before naming files
   or workers.
3. Read `docs/known-issues.md` before interpreting unexpected validation
   failures.
4. Classify touched surfaces: backend, integration/host topology, web, console,
   emulator runtime, docs/instructions, or CI/release validation.
5. Build a risk map: persisted contracts, generated artifacts, host/API
   boundaries, schema migrations, auth/storage boundaries, and likely test gaps.
6. Name the minimum validation set for those surfaces before editing.
7. If the change adds a backend test project, include the `.mise.toml`
   `tasks.test` update in the same plan.
8. Treat generated clients as derived artifacts. Plan backend/OpenAPI changes
   before client regeneration; do not hand-edit generated files.
9. For migration-sensitive areas, identify persisted contract keys and schema
   migrations before implementation.
10. Mark parallelization opportunities only when file scopes are disjoint enough
    to avoid workers editing the same files or dependent generated artifacts.

## Output

Return a compact plan with:

- acceptance criteria,
- touched surfaces,
- risk map,
- verification matrix with commands, scope, and pass/fail evidence expected,
- parallelization opportunities with explicitly disjoint file scopes, or `none`,
- known-issues entries that may affect interpretation,
- explicit follow-ups that should not be mixed into the current task.
