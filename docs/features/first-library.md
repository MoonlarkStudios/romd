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
collections can skip the browser upload: when the operator has configured an
allowed import directory, Managers see **Import from a server folder** on the
import page. Pick a folder from the list and choose **Import this folder**; ROMD
copies it in the background and leaves the original unchanged.

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

Open **Catalog → Needs review** to resolve uncertain provider matches and complete
missing metadata or artwork. Review suggested identities before accepting them.
Open a title to edit its description in **Metadata** or curate images in
**Artwork**. **Releases** shows the files and hashes for each release, and
**Ratings** shows the title's official age ratings.

Use [artwork completion](unified-artwork-completion.md) and
[artwork curation](artwork-curation.md) for the detailed workflow.

## 4. Choose the audience's games

Open **Libraries → New library** and give it a name such as **Living room**.
Choose **Select with rules** to set a system scope or **Handpick games** to add
specific titles. The picker starts with games that have files on your server.
Set a maximum content-rating age and choose **Create draft** to open **Games**.

Review the eligible games and exclusion reasons. In **Content ratings**,
**When a game has no rating** defaults to **Flag for review**. Choose **Show**
when this audience should see unrated games.

Handpicked games still follow tracking and rating rules. Use **Include
untracked titles** to consider the wider catalog or **Include missing games**
to show eligible games that have no downloadable files.

Select **Save changes** if you edited the draft.

## 5. Publish and assign people

Choose **Publish library** and wait for it to finish. After later edits or
imports, choose **Publish changes** when you are ready. People keep seeing the
previous published contents until the new publication succeeds.

The library list and overview show both games and releases. One game can have
several releases, such as regional versions or revisions.

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
| The first library has not been published | Use **Publish library** and inspect the job outcome. |
| The wrong audience is assigned | Check the library's **People** tab and the person's account. |
| Ratings or tracking exclude games | Inspect **Games** results and exclusion reasons; set a deliberate unrated-game policy. |
| Catalog entries exist but no files are owned | Review import outcomes in **Jobs** and the title's files. |
| Imports or rule edits are newer than publication | Use **Publish changes** after saving and after the catalog settles. |
