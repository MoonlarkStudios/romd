# Downloads and exports

Download a particular game from the consumer portal, or use the admin portal to
export files and whole libraries. A catalog definition alone is not downloadable:
ROMD must have the required local content and your account must have access.

## Download a game

In the **consumer portal**, open a game and choose **Download**. Use **Releases**
when you want a specific regional version or revision. Review the available files
and format before starting the save.

The browser manages the actual download, progress and destination. ROMD handing
it off is not a confirmation that the file finished saving. Check the browser's
download list for completion or errors; request the download again if its
short-lived link has expired.

In the **admin portal**, title and release downloads package the available files
as a ZIP. ROM detail downloads return the chosen individual file. An incomplete
release export contains only local files and is named with an
`-available-files.zip` suffix; it is not a complete playable release.

## Choose a format

### Download a multi-disc game

In the consumer portal, open **Releases → Disc sets**. These are the sets an
administrator accepted and explicitly published to your library. Select the
discs you want, then choose **Download original ZIP** or **Download CHD where
available**. Known omissions and missing expected discs require confirmation
that the download is incomplete. An unknown total is shown as unknown.

Extract the ZIP and open its **M3U playlist** in an emulator that supports
playlists. The playlist is named after the edition and keeps the same name for
partial and complete downloads. Discs live in separate `disc-01/`, `disc-02/` directories, so identical
filenames cannot overwrite each other. Original downloads preserve each complete
CUE/BIN layout and required companions. CHD downloads use one verified CHD per
eligible disc and update the playlist; discs without a usable CHD keep their
original layout. Required sidecars prevent CHD substitution. The download does
not start compression or combine several discs into one CHD.

Disc switching and shared saves depend on your emulator. These downloads do not
enable Ottercade's gated group playback or browser playback.

### Other downloads

The server lists the formats each download offers, with their names.

| Choice | What you receive |
| --- | --- |
| **Original** | The original logical files, reconstructed when storage is compressed. Always offered. |
| **RVZ where available**, **CHD where available** and any other stored format | That format for each member that has it; original files for the others. |

A title or release ZIP lists a stored format only when its ZIP would contain at
least one file in that format, so a cartridge-only title offers a single
download. A library export lists the stored formats of the systems the library
allows. A ZIP can therefore contain more than one format. These choices select
existing representations; they do not start a new conversion. Individual file
downloads offer only formats available for that file. A representation that
disappears before delivery fails rather than silently substituting different
bytes.

A file matched to different systems can still offer verified compressed bytes
and its decoded original. That download makes no emulator compatibility claim
unless the server resolves one media kind; choose a format your emulator can
read.

See [disc compression](disc-compression.md) for supported systems, retention of
original bytes, and native-media limits. Browser playback prepares original game
bytes independently of the format you choose for a download.

## Export a library

In the **admin portal**, open **Libraries**, choose a published library, select
its export format, and choose **Export library ZIP**. Follow **View export
progress** to the job, then download the completed archive. Exports run in the
background; an existing export must finish before another can start for the
same scope. Review job outcomes for skipped missing content.

A library export contains games from that published audience, not an installation
backup. It does not preserve accounts, configuration, signing keys or database
state. Operators should use [backup and restore](../self-hosting/production-deployment.md#backup-and-restore).

## If a download fails

- Check that the release has local files and is still in your published library.
- Review the browser's download errors and retry with a fresh download action.
- If media decoders are busy, wait and retry; a server operator can investigate
  ongoing failures in **Jobs**, **Diagnostics** and the media-processing runbook.
- Do not share generated download URLs: they provide temporary access to content.

Contributors: [download and export contracts](../architecture/downloads-and-exports.md)
cover authorization, ticket lifetimes, streaming, ZIP paths and worker behavior.
