# AGENTS.md - ROMD Web

React 19 + Mantine 9 admin and consumer web apps, the isolated-origin browser
player, and generated TypeScript API clients.

## Package Map

- `packages/romd-foundation`: `@romd/foundation`, the shared design foundation.
  Owns the identity tokens (`src/tokens.css`), shared portal/authentication palette
  (`src/portal-theme.css`), typography roles
  (`src/typography.css`), bundled Archivo/IBM Plex Mono fonts (OFL, shared
  with the console client), the TypeScript token mirror (`src/tokens.ts`), and
  the Mantine theme (`src/theme.ts`, `romdTheme`). Values align with the
  Moonlark charcoal-and-teal palette; see `docs/theming.md` for scheme and first-paint ownership.
  Approved artwork and fixed artwork colors originate in the copied `../brand/romd/`
  kit. Follow `docs/branding.md` for source commits, placement, and updates; do not
  edit derived `src/brand/` files independently. Title-font adoption is separate.
  Web apps must consume identity values from here instead of hardcoding
  colors, motion, or type scales.
- `packages/romd-consumer-ui`: `@romd/consumer-ui`, shared consumer presentation
  used by the consumer app and admin previews. Owns the scoped consumer theme
  and title-detail composition. Keep API clients, routing, authentication, and
  playback logic in host apps. Add future shared sections under `components/`
  and full page compositions under `pages/`; use container queries for previews.
- `packages/romd-admin-app`: admin SPA for curation, ingestion, jobs, libraries,
  taxonomy, and management workflows.
- `packages/romd-consumer-app`: consumer SPA for browse, account, collection,
  and delivery workflows.
- `packages/romd-player-app`: isolated-origin browser player that the consumer
  app hands verified ROM bytes to for in-browser play.
- `packages/romd-delivery`: shared browser-managed download handoff and credential-free
  playback fetch transport. Authorization stays in each host; playback integrity
  checks stay in the consumer app. See `../docs/architecture/downloads-and-exports.md`.
- `packages/romd-player-protocol`: `@romd/player-protocol`, the portal/player
  message contract. `pnpm build` and `pnpm dev:consumer` build it first.
- `packages/romd-admin-api-client`: generated TypeScript client for admin and
  management API contracts.
- `packages/romd-consumer-api-client`: generated TypeScript client for consumer
  API contracts.

- `packages/romd-portal-auth`: `@romd/portal-auth`, transport-independent session/profile
  lifecycle ownership shared by both portals. Each app supplies its own OIDC events
  and profile adapter; API clients stay in the app.

## Dev Servers

Run from `web/`:

```bash
pnpm dev
VITE_ROMD_CONSUMER_API_ORIGIN=http://localhost:5002 pnpm dev:consumer
```

## Validation

Run from `web/`:

```bash
pnpm install --frozen-lockfile
pnpm lint
pnpm test
pnpm build
pnpm api:update
```

Use `pnpm lint`, `pnpm test`, and `pnpm build` for meaningful web changes.
Run `pnpm api:update` after backend endpoint or OpenAPI contract changes that
affect generated clients.

## TypeScript Conventions

- Functional components with hooks. `const` over `let`; never `var`.
- Use `import type` for type-only imports.
- Single quotes, semicolons, trailing commas.
- Biome owns lint and format. Do not add ESLint or Prettier.
- React Query v5 for server state, Mantine 9 for UI.
- Strongly type data flows. Do not cast to `any`; use `unknown` only when the
  type is genuinely unknown.
- Styling: use `@romd/foundation` tokens (`var(--romd-*)` in CSS/inline styles,
  `romdVars`/`romdColors` from `@romd/foundation` in TS) and the
  `romd-hero`/`romd-page-heading`/`romd-section-heading`/`romd-card-title`/
  `romd-body`/`romd-metadata`/`romd-eyebrow` type roles. Do not introduce
  hardcoded hex/rgba brand colors, ad-hoc font sizes/weights, or one-off
  transition durations for the same roles.

## API Client Lifecycle

- Never edit files in `web/packages/romd-*-api-client/src/generated/`.
- Change backend endpoints, contracts, or OpenAPI configuration first, then run
  `pnpm api:update` from `web/`.
- Generated client packages must build before app call sites can resolve new
  exports. `pnpm build` handles the package ordering.
- Update admin and consumer app call sites only after generated clients reflect
  the backend contract.
- Use `.agents/skills/romd-web-api-client/SKILL.md` for backend/OpenAPI changes
  that require generated client updates.

## Critical Gotchas

- `useRomsList` uses `useInfiniteQuery`; its `TData` generic must be
  `InfiniteData<RomsPage>`, not `RomsPage`.
- Background enrichment actions return `202` with a `JobReference` body (`jobId`)
  and `Location: /api/jobs/{jobId}`. Poll that job resource to observe completion;
  polling must be sufficient for correctness. SignalR can accelerate updates.
- Default test render helpers provide stable auth context. Mount the real
  `AuthProvider` explicitly for auth lifecycle tests, and await observable async
  updates. The full suite should run without React `act(...)` warnings.
- Each package owns its Vitest config: the `test` block in an app's
  `vite.config.ts`, or a library's `vitest.config.ts`. The root config only runs
  them as projects, so tests in a package with neither file never run.
- `pnpm lint` checks formatting and lint rules and fails on warnings. Generated
  API clients are excluded; regenerate them instead of editing their output.

## Admin Workspace Styling

For audience and curation workspace changes, read
`docs/admin-workspace-style-guide.md`. Reuse
`components/Workspace/Workspace.module.css` and `workspaceActionProps` in the
admin app for shared typography, surfaces, tabs, and actions. Libraries is the
visual reference; keep content-specific layout in page styles.

## Consumer App Styling

The consumer app draws identity from `@romd/foundation` plus a small set of
shared pieces; use them instead of inventing per-page variants:

- `components/status/StatusState`: the single vocabulary for loading, error
  (with retry), and empty states. Do not add bare Loaders or ad-hoc error
  Alerts at page level.
- `components/library/ArtworkFallback`: the one no-artwork treatment (hash
  gradient + monogram) for every title/shelf image fallback.
- `components/library/PosterCard` (grid) and `TitleListRow` (list) are the
  title-card family; the old `TitleCard` was removed.
- `hooks/useHistoryBack`: back navigation that preserves browsing context,
  with a deep-link fallback route.
- `styles/app.css` holds consumer-only composition (grain, rails, poster
  hover, version rows, header search); interactive states must include
  `:focus-visible`/`:focus-within`, not hover-only reveals.
