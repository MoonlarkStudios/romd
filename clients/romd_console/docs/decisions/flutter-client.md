# Console Client In Flutter

Related decisions: [consumer runtime boundary](../../../../docs/decisions/consumer-runtime-boundary.md),
[API contract and compatibility policy](../../../../docs/decisions/api-contract-policy.md).

## Context

ROMD needs a console-style frontend for couch-distance browsing and launching.
That client has different constraints than the React apps: controller-first
navigation, large-screen layout, local caching, and launching native emulator
processes. It must also keep working offline from what it installed.

## Decisions

### A Flutter Client Under `clients/romd_console/`

- The console frontend is a Flutter application in `clients/romd_console/`.
- It stays out of `web/` and `src/`: it is neither a pnpm package nor a .NET
  host, and it keeps its own toolchain.
- It talks to `Romd.Consumer.Host` only. Admin curation, ingestion, enrichment
  and storage authorization stay outside the app.
- It owns local caching, installs and emulator launch integration.

### Handwritten Service Boundaries

- The consumer API client is a handwritten service boundary.
- A generated API client, if one is introduced, sits behind handwritten
  services, and the rest of the app never depends on generated code directly.

## Trade-offs

- **Separate toolchains.** Backend, web and console each build and test with
  their own tools.
- **The console client moves by hand.** The handwritten consumer client is
  outside the generated-client drift gate, so every consumer contract change
  includes console call sites.

## Rejected Alternatives

- **A console UI inside the React web apps.** Rejected: controller-first
  navigation, large-screen layout, local caching and native process launch are
  constraints the browser apps do not share.

## Guardrails

- **Console checks:** `mise run analyze` and `mise run test` from
  `clients/romd_console/`.
- **`consumer_api_client_test.dart`:** the handwritten consumer boundary.
- **Review:** consumer contract changes include their console call sites, as
  required by the
  [API contract policy](../../../../docs/decisions/api-contract-policy.md#first-party-audience-and-repo-atomic-compatibility).
