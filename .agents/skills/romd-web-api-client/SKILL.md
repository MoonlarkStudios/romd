---
name: romd-web-api-client
description: Use when changing ROMD backend endpoints, OpenAPI contracts, generated TypeScript web API clients, API client packages, or admin/consumer web app call sites that depend on regenerated ROMD API clients.
---

# ROMD Web API Client

Use this skill for ROMD changes that cross the backend OpenAPI boundary and the
generated TypeScript clients under `web/`.

## First Reads

- `AGENTS.md`
- `web/AGENTS.md`
- `src/AGENTS.md` when backend endpoints, contracts, or OpenAPI output change

## Required Decision

Record the client scope in the worker summary exactly as:

- `api clients: admin/consumer/both/none - <reason>`

Use `none` only when the change is limited to handwritten app code and consumes
an existing generated client contract.

## Workflow

1. Change the backend endpoint, contract, or OpenAPI source first.
2. Do not hand-edit generated files under
   `web/packages/romd-*-api-client/src/generated/`.
3. From `web/`, run `pnpm api:update` after backend OpenAPI output changes.
4. Verify generated client changes reflect the source contract change.
5. Update admin or consumer app call sites after the generated client exposes
   the needed contract.
6. Keep generated client diffs and handwritten app diffs distinct when
   summarizing the work.

## Generated Boundaries

- Admin generated client:
  `web/packages/romd-admin-api-client/src/generated/`
- Consumer generated client:
  `web/packages/romd-consumer-api-client/src/generated/`

Generated files are derived artifacts. If generated output is wrong, fix the
backend endpoint, contract, or generator configuration, then regenerate.

## Negative Checks

Before finalizing, inspect:

```bash
git diff -- web/packages/romd-admin-api-client/src/generated web/packages/romd-consumer-api-client/src/generated
```

If generated files changed without `pnpm api:update`, rerun the generator or
revert the hand edit. If an endpoint or contract changed but generated files did
not, explain why no client update is required.

## Validation

Run from `web/`:

```bash
pnpm lint
pnpm build
```

Run `pnpm test` when handwritten app behavior changes. Run the relevant backend
validation from the repo root when backend endpoints, contracts, host behavior,
or integration fixtures changed.

If validation fails unexpectedly, read `../docs/known-issues.md` before retrying
or changing behavior.
