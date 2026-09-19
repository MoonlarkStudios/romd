# Importing ROMs for tracked titles

In the admin import workspace, choose **ROMs to keep → Tracked titles only**.
Track the titles you want before starting the import. The default remains
**Import everything**, with the existing tracking and unmatched-file options.

Tracked-only imports use catalog hash matches, not filenames. Each ROM is kept
only if at least one matching title is currently tracked when that file is
processed. Matching regions and revisions are all eligible; pinned releases and
preferred-region selection do not further restrict this mode. Tracking changes
during a long import affect subsequent files.

The filter runs before permanent storage and duplicate relinking. It never adds
tracking intent, even when the same bytes also match untracked titles. Unmatched
ROMs, untracked matches, and BIOS-only files are skipped. Existing stored ROMs
are not deleted. DAT files inside mixed archives still import normally.

The entire upload still transfers and extracts into temporary storage. This
option reduces retained content, not upload size or the need to hash files.
The web client checks for tracked titles before transferring bytes. The API
also rejects tracked-only submissions when nothing is tracked, and the
worker checks again before extraction. Skips count as processed/rejected files,
not processing errors; tracked-only results label them as skipped and per-file
provenance explains why. For server-path move imports, skipped source files are
not considered durably stored and are preserved by source cleanup.

## API and persistence

- `POST /api/upload?trackedOnly=true`
- `POST /api/upload/rom?trackedOnly=true`
- `POST /api/upload/from-path` with `trackedOnly: true` in the request body

`trackedOnly` overrides `allowUnidentified` and suppresses automatic title
tracking regardless of `archiveOnly`. The upload job exposes `trackedOnly` in
its result. Reusing a request ID with a different filter is a conflict.

Schema version 33 adds the persisted upload-job choice. Historical upload jobs
are backfilled to false. Deploy the worker migration before the API hosts; do
not roll the admin independently against schema version 32.

Resetting an existing collection is separate from importing. Preserve accounts,
configuration, reference catalogs, and catalog assets. Any approved reset must
invalidate payload projections and artwork selections, reset enrichment state
so artwork can be fetched again, and reclaim only unreferenced content through
the storage layer. Do not delete the CAS directory wholesale.
