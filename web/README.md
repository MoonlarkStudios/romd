# ROMD web

The pnpm workspace for ROMD's browser apps: the admin portal, the consumer
portal, the isolated browser player, and their shared packages. Built with
React 19, Mantine 9, Vite and Vitest.

## Packages

| Package | Purpose |
| --- | --- |
| `romd-admin-app` | Admin portal: catalogs, imports, enrichment, libraries and settings |
| `romd-consumer-app` | Consumer portal: browse, account, downloads and browser play |
| `romd-player-app` | [Browser player](../docs/architecture/browser-player.md) served from its own origin |
| `romd-player-protocol` | Message contract between the portals and the player |
| `romd-foundation` | Shared design tokens, typography, fonts and Mantine theme |
| [`romd-consumer-ui`](packages/romd-consumer-ui/README.md) | Consumer presentation shared with admin previews |
| `romd-portal-auth` | Session/profile lifecycle shared by the portals through app-owned adapters |
| `romd-delivery` | Browser download handoff and playback fetch transport |
| `romd-admin-api-client`, `romd-consumer-api-client` | TypeScript clients generated from the backend OpenAPI documents |

## Quick start

The easiest path is the integrated stack. From the repository root, run
`mise run dev`; it starts the backend, PostgreSQL and every Vite server. To work
on the frontends alone, from `web/`:

```sh
pnpm install --frozen-lockfile
pnpm dev             # admin portal
pnpm dev:consumer    # consumer portal
pnpm dev:player      # browser player
```

See [web development](docs/development.md) for ports, API origins and manual
startup.

## Checks

```sh
pnpm lint
pnpm test
pnpm build
```

After a backend API change, run `pnpm api:update` to regenerate the API clients.
Never hand-edit generated client code.

## More

- [Web development](docs/development.md): dev servers, proxies and origins.
- [Admin workspace styles](docs/admin-workspace-style-guide.md): shared layout
  rules for the admin workspaces.
- [Theming](docs/theming.md): shared palette, authentication first paint and
  the boundary for future instance themes.
- [Web branding](docs/branding.md): copying the ROMD kit, recording its source
  commit, and placing assets through the existing foundation.
- [Application brand snapshots](../brand/README.md): web asset export commands
  and the route to canonical Moonlark identity guides.
- [Web instructions](AGENTS.md): package boundaries and design rules for
  contributors and coding agents.
