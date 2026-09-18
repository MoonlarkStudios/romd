# Console Client Flutter Direction

Status: accepted

## Context

ROMD needs a console-style frontend for couch-distance browsing and launching.
That client has different constraints than the React apps: controller-first
navigation, large-screen layout, local caching, and native process launch.

## Decision

Build the console frontend with Flutter under `clients/romd_console/`.

Keep it out of `web/` and `src/`; it is neither a pnpm package nor a .NET host.
It consumes `Romd.Consumer.Host`.

## Consequences

- Backend, web, and native-client toolchains stay separate.
- The console app can own local cache and emulator launch integration.
- Generated Flutter API clients should be isolated behind hand-written services
  if introduced.
