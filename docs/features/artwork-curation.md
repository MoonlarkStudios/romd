# Artwork curation

For the current automatic pipeline and completion workflow, see
[Unified artwork completion](unified-artwork-completion.md).

Artwork curation separates Discover (identify a provider game), Preview (inspect
remote candidates), and Apply (retain selected content in ROMD). Preview must
not mutate title state, establish provider associations, or retain gallery
assets. Only explicit Apply creates retained content and selection intent.

The Artwork tab opens a persistent composition workspace. Select a role, then
choose a saved image, provider candidate, or upload to preview it inline. Current
and Preview comparison, desktop/mobile views, and focal controls remain beside
the image choices. Apply commits the draft; Discard restores the saved artwork.
Leaving with a draft requires a discard decision. Provider imports keep the
saved presentation visible until publication completes.

Backdrop, logo, and poster detail previews use the shared consumer game-detail
composition. Library poster previews share the consumer poster surface. The
title header is compact on this tab so the composition remains prominent.
Automatic-run history and the full saved library are collapsed secondary sections.

## Choose a presentation role

| Role | Where it appears | Preferred shape |
| --- | --- | --- |
| Poster | Library card | 2:3, typically 600×900 |
| Backdrop | Game-detail background | 16:9, typically 1920×1080 or larger |
| Banner (`Hero` in the API) | Wide header | 96:31, typically 1920×620 |
| Logo | Transparent title art | Natural proportions |
| Screenshot | Gameplay gallery | Original proportions |

Review the actual desktop and mobile compositions. A landscape image with a
large logo or baked-in text may make a poor backdrop. Focal-point controls adjust
the composition; they do not destructively crop the retained original.

## Backdrop automation and composition

New installations enable automatic backdrop acquisition with review off. Saved
administrator preferences remain authoritative. IGDB artworks supply automatic
backdrop candidates; screenshots can be selected manually. SteamGridDB heroes
are banners, rather than automatic backdrops.

Enable **Review backdrops** under **Settings** to require a choice before a
suggested backdrop is downloaded and selected. **Review backdrop** opens suitable
landscape candidates. Inspect desktop/mobile previews, set the focal point and
apply your choice. You can disable the landscape filter for a deliberate exception.
Pins, existing eligible artwork and pending manual choices are preserved.

If a backdrop is absent or cannot load, game details use the poster composition.
A missing logo shows the game name; a missing poster uses the local monogram
fallback. Choosing a new image keeps the saved presentation visible until its
replacement is ready.

## Collect images

Managers and admins can use **Add to library** on a title's **Artwork** tab to
retain alternatives, then expand **Saved artwork library** to inspect them.
Collecting an image is separate from selecting its presentation role.

- IGDB supplies covers, backgrounds and screenshots, including artwork for backdrops.
- SteamGridDB supplies grids, wide banners and logos.
- Uploads accept multiple static JPEG, PNG or WebP files, each classified as cover,
  screenshot, background, banner, logo, 3D box or title screen. Originals are bounded
  to 32 MiB, 40 million decoded pixels and 16,384 pixels per dimension.
- Provider matching is available inside the collection dialog. Unavailable and
  unmatched sources stay visible. Manage credentials in **Integrations** and
  automation preferences in **Settings**.

Batches report individual successes/failures. Provider import retries deduplicate
by title, media type, source and stored file; original source URLs identify saved
candidates. Select a retained image's Poster, Backdrop, Banner or Logo role,
review its composition and apply it. Screenshots and other media types do not
create additional presentation roles. Animated assets are unsupported.

## Hide or delete images

**Hide** excludes an unsaved provider candidate without downloading it. **Delete**
removes a retained image from this title after confirmation of the affected
roles. These exclusions prevent automatic reacquisition. A role losing its
selected image returns to automatic resolution; other titles sharing the stored
file keep their images. Exclusions survive title merges and also apply to
metadata media downloads. There is no rejected-images section.

For contributors: [artwork architecture](../architecture/artwork-pipeline.md)
owns provider references, revision fences, retention, delivery variants and
web/console cache boundaries.
