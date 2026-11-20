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

**Presentation** shows the Poster, Backdrop, Banner and Logo roles above a
preview of the consumer library card, home spotlight or game details. Switch
between Desktop, Tablet and Mobile. Select a role from the strip or click it
in the preview. The editor opens beside the preview on wide screens and below
it on narrow screens.

Choose **Automatic** to let ROMD select an image, pin a specific image, or
choose **Intentionally empty** to keep automatic runs from filling the role.
**Replace image** offers saved images, linked providers that support the role,
and **Upload**. After resolving a provider match, the picker returns to that
provider. **Save to library** keeps a provider candidate for later without
assigning it to a role.

Use **Adjust crop** for a pinned poster, backdrop or banner. If ROMD chose the
current image automatically, choose **Pin this image to adjust its crop** first.
Click or drag on the source image, or move the focal point with the arrow keys.
Select a crop thumbnail to see its outline and corresponding consumer preview.
The logo can be inspected against light and dark backgrounds.

The canvas previews staged changes immediately. Use **Saved** and **Draft**
to compare, undo one role in its editor, or discard the whole draft. **Save
changes** submits the edited roles together. Resetting a role to Automatic
shows the current image until the server resolves the new selection.
If a role changed while you were editing, **Reload latest** refreshes it after
confirmation and discards that role's draft. Leaving with a draft prompts you
to discard it.

Provider choices import in the background. The role shows **Importing** and
players keep seeing its previous image until the replacement is ready. **Needs
attention** marks a failed save or import. A failed import offers **Retry** for
the same candidate or **Choose another**. After a partial save, the roles that
succeeded stay saved and the failed edits remain available to retry.

## Choose a presentation role

| Role | Where it appears | Preferred shape |
| --- | --- | --- |
| Poster | Library card | 2:3, typically 600×900 |
| Backdrop | Game-detail background | 16:9, typically 1920×1080 or larger |
| Banner (`Hero` in the API) | Home spotlight | 96:31, typically 1920×620 |
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
save your choice. You can disable the landscape filter for a deliberate exception.
Pins, existing eligible artwork and pending manual choices are preserved.

If a backdrop is absent or cannot load, game details use the poster composition.
A missing logo shows the game name; a missing poster uses the local monogram
fallback. Choosing a new image keeps the saved presentation visible until its
replacement is ready.

## Collect images

Managers and admins collect media in **Image library**. Filter it by type
(presentation art, packaging, gameplay, manuals, or one type) and by release
(a canonical release, or title-level media). Open an item in the lightbox to
see the image alongside its source, attribution and classification. The previous
and next buttons and the Left and Right arrow keys browse the filtered library.
Escape closes the lightbox and returns focus to its library entry. The lightbox
also supports assigning roles, adjusting crops, and editing classification.

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
