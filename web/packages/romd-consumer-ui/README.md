# Consumer presentation

Shared consumer UI used by the consumer app and admin artwork previews. Import
components from `@romd/consumer-ui` and load `@romd/consumer-ui/styles.css` once.

## Organization

- `theme/`: the consumer Mantine theme, built on `@romd/foundation`.
- `ConsumerPresentation`: an isolated consumer theme and named sizing container.
- `components/title-detail/`: game-detail composition and its owned styles.
- Future page compositions belong in `pages/`, assembled from these components.
  Add details, release, and media sections under `components/title-detail/` as
  they become shared, then compose a full title-detail presentation for both apps.

## Boundaries

Accept display data and React slots/callbacks, not generated API DTOs. Keep data
fetching, authentication, routers, playback decisions, downloads, and artwork
acquisition in each app. Admin supplies draft image URLs and inert action slots;
consumer supplies safe image delivery and real actions. Do not copy presentation
markup into previews.

Use foundation tokens and container queries rather than viewport media queries
for component layouts. This lets the same component respond to a full page or
an embedded desktop/mobile preview. `ConsumerPresentation` scopes theme variables
without changing the parent app's color scheme.

Validate shared changes through both apps: from `web/`, run `pnpm lint`,
`pnpm test`, and `pnpm build`, then inspect the live consumer and admin preview.
