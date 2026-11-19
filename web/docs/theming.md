# Web theme ownership

ROMD uses the Moonlark charcoal-and-teal palette. Admin pairs a charcoal
canvas with warm off-white text and mint for actions, selection and focus.
Light mode pairs warm paper with deeper teal. Panel and field fills establish
the editing hierarchy, while green, amber and red communicate success,
attention and danger. New Admin sessions default to dark and keep an explicit
light or dark preference. The consumer portal and browser player stay dark.
Logos and game artwork have their own colors. See the
[screenshot guide](../../docs/assets/screenshots/README.md) for reviewed
product captures.

## Current layer

`@romd/foundation` owns semantic colors, type and motion. Components consume
`--romd-*` roles or the Mantine adapter, rather than choosing palette hex values.
`romdVars` provides CSS variable references for inline styles; `romdColors` is
the dark palette's imperative mirror, for contexts that cannot use CSS.

- `portal-theme.css`: portal/authentication canvas, surfaces, text, borders,
  actions and errors. Both schemes use readable secondary text and a separate
  warm red for errors. Muted text remains readable on elevated surfaces.
- `admin-theme.css`: Admin SPA roles for fields, control borders, selection,
  focus and status in both schemes. The shared portal palette supplies the
  canvas, panel, text and action colors. Admin components select the default
  Mantine action, the relevant status variant, or a semantic CSS role so
  palette changes apply across workspaces.
- `variables.css` and `tokens.ts`: dark presentation roles used by the consumer,
  isolated player and embedded previews. Keep the mirror aligned.
- `theme.ts`: Mantine color ramps and semantic CSS variable adapters. The
  shared theme uses `teal` with the legacy `mint` and `pine` aliases. The Admin
  adapter maps its primary and status variants to the Admin palette, while the
  scoped dark adapter supplies the consumer preview's own colors. Filled
  actions resolve label contrast through the active palette.
- `auth.css`: shared sign-in layout, fields, focus states and logo variants.

The Vite `portalTheme` plugin inlines the shared stylesheet and bootstrap
script before application mount. The Admin Vite entry point also inlines
`admin-theme.css` after the shared stylesheet. The Admin Mantine resolver maps
those same roles after mount. The .NET host embeds the shared files in login
and device verification HTML through `AuthenticationPageAssets`.
The bootstrap reads Mantine's preference key and respects the stored Admin
scheme. Consumer redirects always select dark.

The scoped consumer presentation owns its dark colors even inside a light
admin page. Its preview must not change the host's scheme. Ottercade's Flutter
skins are independently owned; this web change does not re-theme native apps.

## Sign-in message

An administrator can set the optional **Sign-in message** under **Settings**.
Both hosted web sign-in forms show the same plain-text message before
credentials are entered. A blank message hides the subtitle.

## Shared header geometry

`romdLayout` in the foundation's `tokens.ts` owns header height, horizontal
padding, control size and control gaps. Admin and consumer headers use the same
64 px height, 16 px padding, 34 px controls and 8 px control gaps. `brand.css`
owns the ROMD signature: the wordmark at full width and its extracted
symbol below the 36 em compact breakpoint. Keep one signature visible at a
time; the symbol must not repeat the wordmark's “o” beside the full name.
Consumer page gutters remain separate: 32 px on desktop and 16 px on mobile.

[Web branding](branding.md) owns local artwork placement and updates. `brand.css`
uses fixed ink/cream values from the [canonical tokens](../../brand/romd/tokens.css).
The wordmark is supplied vector artwork.
Hosted sign-in uses the same canonical horizontal signature as the README.

## Future instance themes

Admin-selected instance themes are not implemented. Build that feature around
a versioned semantic theme definition: canvas, surface elevations, text,
muted text, border, action, on-action, focus, success, warning and danger.
Validate contrast and supported values before saving. Components should keep
their role names when the selected palette changes.

Resolve the same theme before first paint in portals, hosted authentication
and the isolated player. That needs a deliberate public configuration contract
across their separate origins, with a safe bundled fallback. Preserve the
player's credential-free boundary. Native adapters need their own migration
and offline-cache policy. Add settings, persistence and adapters together when
that feature is taken on; a CSS variable override alone does not cover them.

For theme changes, run the [web checks](../README.md#checks), review both admin
schemes and consumer previews, and walk login → redirect → callback → app.
Include host integration checks when changing the embedded HTML/resources.
