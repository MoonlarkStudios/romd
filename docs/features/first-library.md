# Your first library

This walkthrough takes an administrator from an empty ROMD server to a library
someone in the household can browse and play. First complete
[server installation](../self-hosting/README.md) or
[development setup](../development/getting-started.md), then sign in to the
**admin portal** with the account configured for that installation.

ROMD supplies no games or BIOS files. You provide your own collection. A DAT
catalog describes expected releases and their checksums; importing a DAT does
not download any games.

## 1. Add a system and a catalog

Open **Systems → Add system**, search for a system, and choose **Add to your
systems**. Adding a system enables your local workspace; it does not require
choosing a catalog immediately.

In that system, open **Catalog sources → Add source**. Choose an available
verified subscription or upload a compatible extracted DAT/XML file. Review
the document before approving its import, then watch the accepted work in
**Jobs**. A source contributes release definitions; more than one source can
belong to a system.

Subscription updates are prepared for review, never silently activated.
For source setup, replacement and removal, see
[Systems and catalog sources](system-setup.md) and
[DAT subscriptions](dat-subscriptions.md).

## 2. Import your games

Use **Import files** on the Dashboard or the import action in **ROMs**. Select
the target system and upload your files or supported ZIP/7z archives. Large
collections can also use a server-side path import when the operator has
configured an allowed import directory.

Wait for the job to complete and inspect its file outcomes. ROMD matches
content against the catalog by checksum. Import failures or unmatched files
need review; a completed upload alone does not mean every file was accepted.

Open **Catalog → Tracked titles** to review your collection. Use **All titles**
to find other catalog entries and choose titles to track. Tracking is your
collection intent; having files is a separate fact. New library rules normally
consider tracked titles only.

See [Importing ROMs](archive-imports.md),
[tracked-title imports](tracked-title-imports.md) and
[disc compression](disc-compression.md) for supported formats and limits.

## 3. Add metadata and artwork

Configure the providers you want under **Integrations**. IGDB supplies game
metadata and artwork; SteamGridDB supplies additional artwork. Provider setup is
optional: you can inspect imported files and manage your library without it.

Open **Enrichment** to review tracked titles, resolve uncertain provider matches
and complete missing metadata or artwork. Review suggested identities before
accepting them. A title's own workspace also lets you edit its description and
curate its retained artwork.

Use [artwork completion](unified-artwork-completion.md) and
[artwork curation](artwork-curation.md) for the detailed workflow.

## 4. Choose the audience's games

Open **Libraries → New library**. Give it a name such as **Living room**, choose
the initial selection and set a maximum content-rating age. Creating a library
opens its **Games** tab.

Start with **Content ratings**, including **When a game has no rating**. The
default holds unrated games for review. Choose **Show** only when that is the
policy you want for this audience; an imported game can be hidden simply because
it has no rating yet.

Then choose **Select with rules** or **Handpick games**. Review the eligible
results and exclusion reasons beside your draft. Handpicked games still obey
tracking and rating restrictions. **Include untracked titles** is an explicit
opt-in, and **Include missing games** is separate from owning playable files.

Select **Save changes**. Saving records your rules; the audience receives them
only after publication.

## 5. Publish and assign people

Choose **Refresh library** and wait for it to finish. This publishes the first
consumer snapshot. After later edits or imports, refresh again when you are
ready to publish those changes. There is no scheduled automatic publication;
people keep seeing the previous published contents in the meantime.

The overview counts **releases**, so a title with regional versions or revisions
can contribute more than one item. This differs from a count of distinct titles
or owned games.

In **People**, use **Assign a person**. Create accounts through **Manage people
and roles** when needed. Each person has one assigned library, wherever they
play. Use **Preview** to inspect the audience's published experience.

Optionally create reusable lists in **Collections**, attach them to the library
and arrange their featured placement. A collection organizes games; it does not
override the audience's access rules. See
[audience libraries](library-audience-workspace.md) and
[collections](collection-workspace.md).

## 6. Browse and play

Open the **consumer portal** and sign in as the assigned person. **Browse**
searches their library; opening a game shows its details and releases. Choose
**Download**, or **Play Now** when that release supports browser playback.

For a TV and controller, see [Ottercade](../../clients/romd_console/README.md).
Its native builds and emulator/platform combinations remain experimental.

Next: [Browsing and playing](browsing-and-playing.md),
[downloads and exports](downloads-and-exports.md), or the
[full documentation index](../README.md).

## If the library looks empty

| Check | What to do |
| --- | --- |
| The first library has not been published | Use **Refresh library** and inspect the job outcome. |
| The wrong audience is assigned | Check the library's **People** tab and the person's account. |
| Ratings or tracking exclude games | Inspect **Games** results and exclusion reasons; set a deliberate unrated-game policy. |
| Catalog entries exist but no files are owned | Review import outcomes in **Jobs** and the title's files. |
| Imports or rule edits are newer than publication | Refresh the library after saving and after the catalog settles. |
