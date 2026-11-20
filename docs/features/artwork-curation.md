# Artwork curation

For the current automatic pipeline and completion workflow, see
[Unified artwork completion](unified-artwork-completion.md).

Artwork curation separates Discover (identify a provider game), Preview (inspect
remote candidates), and Apply (retain selected content in ROMD). Preview must
not mutate title state, establish provider associations, or retain gallery
assets. Only explicit Apply creates retained content and selection intent.

The title's **Artwork** section has two tabs. **Presentation** chooses what
players see; **Image library** collects and classifies everything retained for
the title.

Presentation shows Poster, Backdrop, Banner and Logo in the crops consumer
surfaces use, with each role's state: **Pinned**, **Automatic**, **Fallback**
(the poster falls back to the title cover), **Intentionally empty** or
**Missing**. Each role has one **Edit** action and, when you chose it,
**Use automatically**. The editor puts saved images, each linked provider that
supports the role, and **Upload** in one source picker; after you resolve a
provider match it returns to that provider. Compare **Current** and **Proposed**
at desktop, tablet and mobile sizes and set the focal point where the role
supports one. **Use this banner** (or the role's equivalent) commits the image
and its crop together; **Save to library** keeps a provider candidate for later
without changing the role. On a phone the comparison and commit controls stay
in reach while the editor scrolls.

A provider choice imports in the background. The role keeps showing its current
image with progress until the replacement is ready. If the import fails, the role
offers **Retry** (a new request for the same candidate) and **Choose another**.
Leaving with unsaved changes prompts you to discard them.

Backdrop, logo, and poster detail previews use the shared consumer game-detail
composition. Library poster previews use the consumer poster surface.

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

Managers and admins collect media in **Image library**. Filter it by type
(presentation art, packaging, gameplay, manuals, or one type) and by release
(a canonical release, or title-level media). The inspector shows the source,
attribution, region and language, and edits the classification: type, release,
region, language and, for packaging, the preferred scan.

- **Collect from providers** browses linked providers and **Save to library**
  retains the selected images. Provider matching is available in the dialog.
  Manage credentials in **Integrations** and automation preferences in **Settings**.
- **Upload files** accepts several static JPEG, PNG or WebP images (32 MiB, 40
  million decoded pixels and 16,384 pixels per dimension each) and PDF manuals
  (128 MiB). Set the type and release per file; the shared values apply to new
  files, and **Apply these values to every file** updates the waiting ones. A
  release fills in its region.
- IGDB supplies covers, including regional cover variants labelled with their
  region, backgrounds and screenshots. Regional covers stay covers: IGDB does not
  say they are packaging scans.
- SteamGridDB supplies grids, wide banners and logos. Its images are fan-made
  custom art and are labelled **SteamGridDB community art**.
- ScreenScraper (packaging scans and manuals) is not integrated. A pilot needs
  confirmed developer access and permitted use first.

Batches report individual successes/failures. Provider import retries deduplicate
by title, media type, source and stored file; original source URLs identify saved
candidates. Select a retained image's Poster, Backdrop, Banner or Logo role,
review its composition and apply it. Screenshots, packaging and manuals do not
create presentation roles. Animated assets are unsupported.

### Packaging and manuals

Packaging types are box front, box back, box spine, full box wrap, cartridge
front, cartridge back and disc label. Scans keep their proportions. Place each
scan on the canonical release it shows, or keep it title-level when the release
is unknown; existing covers stay covers until you reclassify them. Call an image
release packaging only when its provenance shows that it is. Each packaging type
can have one preferred scan per release (and one title-level); choosing another
keeps the previous scan as an alternative. Screenshots stay a gallery. Packaging
supplements the enrichment checklist and never satisfies a requirement.

Only uploads and library imports can change type, and images and manuals never
exchange types. Enrichment media keeps its provider type but can still be placed
on a release.

A manual is one library entry with a cover preview, its release and language.
**Open manual** pages through it singly or in facing spreads with the arrow,
Page Up/Down, Home and End keys, and offers **Download**. Uploads must be
unencrypted PDFs without JavaScript or launch actions. Packaging and manuals are
admin collection media: the consumer portal and console do not list them.

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
