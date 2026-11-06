# Archive uploads and path imports

Uploads and path imports extract ZIP and single-volume 7z archives before file
classification. Nested ZIP/7z combinations use the same extraction queue,
job-wide quota, depth limit and worker-owned recovery journal. RAR and gzip are
classified as archives but have no extractor: each surviving archive receives a
visible failed file outcome, and the job completes with errors. They are never
submitted to ROM ingestion. An archive with a supported extension but the wrong
signature fails extraction instead.

## ZIP integrity and metadata limits

The worker accepts single-volume ZIP and ZIP64 archives. Before the framework
reader materializes entries, a fixed-buffer preflight checks the end records
and walks the central directory. It caps the directory at **16 MiB**, its entry
count at **50,000**, and each encoded member name at **4,000 bytes**; extracted
relative paths still have the 1,000-character journal limit. The directory's
actual records must match its declared count and extent. Contradictory ZIP64
records, split archives, truncated metadata, and trailing data outside the final
end-record comment are rejected. A later complete end-record candidate embedded
in a comment is rejected too, so the framework cannot select an unchecked
directory. One optional central-directory digital-signature record may consume
the remaining bounded directory bytes; its contents do not establish authenticity.

Stored and Deflate members are copied through a 64 KiB buffer, with live byte
quotas applied before writes. Every accepted member must match both its declared
uncompressed length and CRC32. Excess decoded bytes are rejected before they are
written; premature EOF and CRC mismatches fail extraction. CRC32 detects damaged
members and does not provide cryptographic authentication. Single-member
extraction uses a delete-on-dispose temporary file, with the same integrity
checks, rather than buffering the whole member in memory.

## 7z support and limits

The worker uses SharpCompress 0.50.4's managed 7z reader. Supported payload coders
are Copy, LZMA and LZMA2, optionally in linear chains with Delta or the library's
fixed-memory BCJ filters (x86, PPC, IA64, ARM, ARM Thumb, SPARC, ARM64, RISC-V).
Solid payload entries are streamed in archive order through one reader: a solid
folder is decoded once. Directories may be emitted first by that reader. Empty
files and directories are created; anti-items are skipped. Extraction never
creates links or restores archived attributes/timestamps; symbolic links and
special file types are rejected. Hard-link creation is never used.

All output paths treat slash and backslash as separators. Absolute paths, drive
letters, NUL and escapes from the output directory are rejected. Relative paths
must fit the 1,000-character outcome-journal limit (including the workspace prefix
when checked by the step). Duplicate names, normalized aliases, case collisions
and file/directory conflicts fail with the existing archive-collision outcome.
Case aliases in implicit parent directories are also rejected on every platform.
Windows device names, trailing dots/spaces and non-portable filename characters
are rejected for deterministic replay across filesystems.

SharpCompress does **not** expose 7z coder properties through its public archive
API, and its 7z decoder bypasses `ReaderOptions.Providers`. ROMD therefore checks
the original header before opening the archive with the library:

- Dictionaries are capped at **64 MiB in total per folder**, including encoded
  header dictionaries. Properties are checked before decoder construction.
- Raw, packed and decoded header buffers are capped at **16 MiB each**. Metadata
  counts are capped at 50,000 and coder chains at eight coders.
- Encoded headers must have one Copy/LZMA/LZMA2 coder. External metadata streams,
  non-linear graphs (including BCJ2), PPMd, other coders, and missing payload CRCs
  are rejected. PPMd rejection prevents any model allocation.
- Encrypted payloads/headers, split names such as `.7z.001`, truncated inputs and
  invalid header/payload CRCs fail with `InvalidDataException`. No passwords or
  sibling volumes are opened.

These bounds limit dictionaries and header parsing, not the entire worker's RSS
or concurrent jobs' aggregate memory. Decoder probability tables, metadata objects
and the 64 KiB copy buffer are additional bounded allocations. The guard is
coupled to the pinned 7z format/library behavior; a dependency upgrade must retain
its rejection tests. There is no separate CPU deadline for managed 7z decoding;
cancellation is checked between bounded reads.

Declared sizes/counts feed expansion and budget preflight; the compressed size is
the source archive's physical length, because solid 7z entries have no individual
compressed size. Enforcement remains live: every created file/directory (including
implicit parents) charges an entry, and each write chunk charges bytes before it
reaches disk. Single-entry extraction returns a delete-on-dispose temporary file
rather than buffering the member in memory. Neither extractor's single-entry API
has a production caller or a quota parameter; production import uses the
quota-enforcing whole-archive operation.

## Recovery and move mode

Before extraction mutates output, the journal records the retained source, chosen
target and pre-attempt quota. A retry removes a partial target and replays the
same archive into the same directory. Publication precedes staged-source deletion;
depth and cumulative quotas survive replay. Policy/corruption failures are
permanent; recoverable I/O failures use the existing bounded retry protocol.

Path imports stage a copy even in move mode. Cleanup requires a persisted
`Completed` phase, a direct durable-storage outcome for the original manifest
entry, and matching source fingerprint/containment checks. ZIP and 7z archive
originals are conservatively retained: member outcomes do not prove storage of
the archive itself. Failed/unsupported sources remain undeleted. Ordinary source
files with direct successful outcomes follow the existing move-mode rules.

Synthetic fixtures, exact 7-Zip commands and header transformations are documented
in [the fixture README](../../tests/Romd.Infrastructure.Tests/Fixtures/SevenZip/README.md).
Backend qualification runs `mise run test` and `mise run test:integration` with
the [configured development container engine](../development/getting-started.md).
The existing process-kill probe qualifies ZIP; 7z has deterministic
partial-output and persisted-upload replay coverage.
