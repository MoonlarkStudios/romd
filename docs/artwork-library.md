# Title artwork library

Managers and admins can collect images from a title's Artwork tab using **Add
artwork**. Provider collection and file uploads save alternatives; choosing the
poster, backdrop, banner, or logo is a separate action with a composition preview.

- IGDB exposes covers, backgrounds, and screenshots.
- SteamGridDB exposes grids, wide banners, and logos. IGDB artworks supply backdrop candidates.
- Uploads accept multiple JPEG, PNG, or WebP files, with an individual type for
  each file: cover, screenshot, background, banner, logo, 3D box, or title screen.
- Provider matching is available inside the collection dialog. Credentials remain
  managed in Settings. Unavailable and unmatched sources remain visible.
- Each batch reports individual successes and failures. Retrying provider imports
  deduplicates by title, media type, source, and stored file. Already-saved provider
  candidates are identified by their original source URL.

## Retention and contracts

`POST /api/artwork/titles/{titleId}/gallery` accepts a server-issued candidate
reference. The reference is title/user scoped, expires, and is bound to the
provider match revision. Imports validate downloaded image bytes and retain the
original, attribution, and source page. A second match validation occurs inside
the application-owned transaction before committing the append.

Manually collected provider media uses `gallery:<providerId>` as its persisted
source identity. This keeps it separate from enrichment-owned media. Do not strip
that prefix in persistence, merge, or rematerialization code. UI labels display
the provider's readable name. Ordinary uploads retain source `user`.

Schema version 20 adds nullable attribution/source-page columns and exempts
`gallery:` sources from the legacy one-image-per-provider/type unique index.
The worker applies the `GalleryArtworkCollection` migration before API readiness
succeeds. No existing image rows are rewritten by the migration. A database
rollback must account for multiple gallery images before restoring the old
unique index; do not discard collected images to satisfy that index.

The admin client is generated with `pnpm api:update`. Presentation roles are
Poster, Backdrop, Hero (displayed as Banner), and Logo; screenshots and other image
types do not add presentation roles. See [artwork curation](artwork-curation.md)
for review-first backdrop automation, composition, and consumer fallbacks.
Screenshot ordering and additional provider-specific asset categories are not
part of this change.
