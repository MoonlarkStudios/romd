---
name: romd-web-api-client
description: Use when changing ROMD backend endpoints, OpenAPI contracts, generated TypeScript API clients or their web packages/call sites, including consumer contract changes that require handwritten console-client review.
---

# ROMD Web API Client

Use this skill for ROMD changes that cross the backend OpenAPI boundary and the
generated TypeScript clients under `web/`. Consumer contract changes also cross
the console's handwritten Dart client boundary.

## First Reads

- `AGENTS.md`
- [API contract and compatibility policy](../../../docs/decisions/api-contract-policy.md)
- `web/AGENTS.md`
- `src/AGENTS.md` when backend endpoints, contracts, or OpenAPI output change
- `clients/romd_console/AGENTS.md` for consumer contract changes

## Required Decision

Record the client scope in the worker summary exactly as:

- `api clients: admin/consumer/both/none - <reason>`

Use `none` only when the change is limited to handwritten app code and consumes
an existing generated client contract.

Also record:

- `console client: changed/unchanged/not-applicable - <reason>`

For every consumer contract change, inspect
`clients/romd_console/lib/src/data/consumer_api_client.dart` and affected console
call sites. Use `unchanged` only with a reason that the existing parser and call
sites already accept the contract. The console checks below still apply.
`not-applicable` is for changes that leave the consumer contract unchanged.

## Workflow

1. Change the backend endpoint, contract, or OpenAPI source first.
2. Do not hand-edit generated files under
   `web/packages/romd-*-api-client/src/generated/`.
3. From `web/`, run `pnpm api:update` after backend OpenAPI output changes.
4. Verify generated client changes reflect the source contract change.
5. Update admin or consumer app call sites after the generated client exposes
   the needed contract.
6. For a consumer contract change, update the handwritten console client,
   parsers and call sites as needed in the same change. Generated-client drift
   checks do not cover Dart. Preserve the policy's same-commit/release pairing
   and migration rules for persisted console identities.
7. Keep generated client diffs and handwritten app diffs distinct when
   summarizing the work.

## Generated Boundaries

- Admin generated client:
  `web/packages/romd-admin-api-client/src/generated/`
- Consumer generated client:
  `web/packages/romd-consumer-api-client/src/generated/`

Generated files are derived artifacts. If generated output is wrong, fix the
backend endpoint, contract, or generator configuration, then regenerate.

## Negative Checks

After reviewing and committing the regenerated schemas and clients, run from
the repository root:

```bash
mise exec -- python3 scripts/validation/run.py --id openapi.drift
```

It reruns `pnpm api:update` and fails if the committed schemas or generated
clients differ from what the backend produces, so a hand edit or a stale
regeneration cannot pass. It refuses to start while those generated paths have
uncommitted changes rather than overwrite them. If it reports drift, fix the
backend source or generator configuration and regenerate; never patch the
generated output. If an endpoint or contract changed but generated files did
not, explain why no client update is required.

## Validation

Run from `web/`:

```bash
pnpm lint
pnpm test
pnpm build
```

Run the relevant backend validation from the repo root when backend endpoints,
contracts, host behavior, or integration fixtures changed; `mise run contracts:check`
includes the drift check above.

For every consumer contract change, also run from `clients/romd_console/`, even
when existing console source already accepts the changed contract:

```bash
mise run analyze
mise run test
```

Add or update contract behavior tests where the console's parsing or request
semantics change. Report any unavailable console tooling or unrun check as a
validation gap; a passing TypeScript drift check cannot establish console
compatibility.

If validation fails unexpectedly, search `docs/development/known-issues.md`, then
`docs/development/known-issues-archive.md` (repository-relative paths), before
retrying or changing behavior.
