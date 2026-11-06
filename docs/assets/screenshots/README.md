# Documentation screenshots

These are captures of the running local demo, using the same product
UI as the portals, with the Squish wordmark and charcoal-and-teal theme. They
contain no ROM files, provider credentials, account addresses or local
filesystem paths. All captures are 1920 × 1080 desktop images, taken at a device
pixel ratio of 1.

| Capture | View |
| --- | --- |
| [Library](library.jpg) | Consumer Browse page, search controls and game-cover grid |
| [Library workspace](library-workspace.jpg) | Admin Games tab, audience ratings and draft results |
| [Sign-in](sign-in.jpg) | Hosted authentication with the shared dark palette and ROMD lockup |

The checked-in [brand snapshots and placement guides](../../../brand/README.md)
provide logo artwork, usage guidance and reproducible exports.
Desktop headers show the full Squish wordmark; compact headers use its extracted
“o” symbol. Sign-in and the README use the horizontal signature with clear space.

The admin capture uses 18 original, fictional demonstration titles with
geometric cover artwork composed for ROMD from circles, a triangle, text and
flat colors. The payloads are generated qualification fixtures, not playable
games or commercial ROMs. Its original fixture artwork is project material under the root license;
bundled font notices remain applicable. Capture inputs and generation commands
are retained with the private publication evidence.

The consumer Browse capture shows real game covers acquired through configured
[IGDB](https://www.igdb.com/) and [SteamGridDB](https://www.steamgriddb.com/)
providers. The cover artwork and game identities belong to their respective
owners and are not covered by ROMD's AGPL license. The image illustrates how the
application presents a library; it is not a pack of standalone cover assets or
game files. The publication review records the exact capture, provider provenance,
intended documentation use and applicable conditions.

Both captures demonstrate the interface, not playback compatibility or release qualification.
Brand and interface asset credits are in
[third-party notices](../../../THIRD_PARTY_NOTICES.md).

To refresh these images, follow [demo setup](../../development/demo-data.md).
Provider-sourced box art may appear in product screenshots; retain its provenance,
credits and applicable conditions in the publication review. Original synthetic
demonstration content is also available. The `dev:demo --synthetic` route provides generated qualification fixtures;
do not substitute those for authenticated catalogs in real-library scenarios.
Use a browser capture of the
actual workflow with the [default web theme](../../../web/docs/theming.md).
Check that every visible game card has a loaded cover before capturing the grid;
do not replace missing covers in the screenshot itself.
For provider-sourced captures, confirm uncertain matches in **Enrichment** using
the game, platform and edition, then let the artwork jobs finish.
Check that the live portals and hosted sign-in show the current canonical
artwork; follow the brand guide's [export checks](../../../brand/README.md#reproducible-exports)
if a development server still shows an older logo.
Wait for the page and artwork to finish loading, close menus and overlays, and
hide the development-only Query Devtools overlay, which production builds omit.
Leave sign-in fields empty. Use a desktop viewport at least 1920 CSS pixels wide
at a device pixel ratio of 1, then capture the full viewport. Account for any
scrollbar inset in the capture tool when choosing viewport dimensions. Verify
the actual page viewport and that the saved JPEG is 1920 × 1080; do not enlarge
a smaller capture. Preserve the surrounding navigation and review the pixels for
credentials, account details and local paths before committing.
Keep raw evidence, manifests and source media in ignored storage.
