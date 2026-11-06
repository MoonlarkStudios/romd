# Web theme ownership

ROMD's web default is Moonlark charcoal and teal: a neutral `#191B1C` canvas,
cream `#F4EFE4` text and the studio's ROMD teal `#88C9B9` action accent. Elevated
surfaces stay neutral so game artwork supplies most of the color. The admin's
light counterpart uses warm paper and a deeper teal for readable actions.
New sessions default to dark; an explicit admin light/dark preference is retained.
The consumer portal and browser player remain dark. Logos and game artwork
retain their own colors. See the [screenshot guide](../../docs/assets/screenshots/README.md)
for reviewed product captures.

## Current layer

`@romd/foundation` owns semantic colors, type and motion. Components consume
`--romd-*` roles or the Mantine adapter, rather than choosing palette hex values.
`romdVars` provides CSS variable references for inline styles; `romdColors` is
the dark palette's imperative mirror, for contexts that cannot use CSS.

- `portal-theme.css`: portal/authentication canvas, surfaces, text, borders,
  actions and errors. Both schemes use readable secondary text and a separate
  warm red for errors. Muted text remains readable on elevated surfaces.
- `variables.css` and `tokens.ts`: dark presentation roles used by the consumer,
  isolated player and embedded previews. Keep the mirror aligned.
- `theme.ts`: Mantine color ramps and semantic CSS variable adapter. The primary
  `teal` and legacy `mint`/`pine` color names share the action ramp. Both filled
  and light variants use the semantic accent; the scoped dark adapter keeps
  embedded consumer actions independent of the admin scheme. Colored segmented
  controls and filled actions resolve their label contrast through CSS, so it
  follows the active scheme.
- `auth.css`: shared sign-in layout, fields, focus states and logo variants.

The Vite `portalTheme` plugin inlines the small palette stylesheet and bootstrap
script before application mount. The .NET host embeds these exact files in
login and device verification HTML through `AuthenticationPageAssets`.
Do not create a second server-side palette or rely on the SPA mounting to paint
the authentication background. The bootstrap reads Mantine's preference key
without writing storage; consumer redirects always select dark.

The scoped consumer presentation owns its dark colors even inside a light
admin page. Its preview must not change the host's scheme. Ottercade's Flutter
skins are independently owned; this web change does not re-theme native apps.

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
