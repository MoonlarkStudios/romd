# CAS random access

Status: implemented in the storage library and used by the opt-in consumer VFS
endpoint (`/vfs`). New writes default to seekable Zstandard. Continuous
Zstandard (`.zst`) support was removed on 2026-09-27; pre-alpha installations
start from an empty storage root instead of converting.

## API boundary

`IContentAddressableStore.OpenRandomAccessAsync(key)` returns an owned
`IRandomAccessContent` with `Length`, `Integrity`, and
`ReadAtAsync(offset, destination, cancellationToken)`. Missing objects return null.
Negative offsets throw; EOF/beyond EOF and empty destinations return zero. Reads
fill the requested range up to EOF. A corrupt/truncated frame throws
`InvalidDataException`. As with ordinary I/O, a failed read may have modified part
of the destination; callers must not consume it after failure.

Calls on one reader are safe concurrently but serialize to reuse its decoder and
one-frame cache. Separate readers run independently. Dispose readers when finished;
each retains a file handle, its index, decoder, and at most one decoded and one
compressed frame buffer. Store disposal does not dispose already-open readers.
Concurrent disposal waits for an active read; queued reads fail once disposed.

`RetrieveAsync` retains its sequential, whole-object SHA-256 verification contract.
Consume it fully for verification. Seekable objects stream through the same validated
index as positional reads, so decoding stops at the indexed length and every frame
checksum is checked on the way; a corrupt frame throws `InvalidDataException` before
the final SHA-256 comparison. `VerifyAsync(key)` consumes that stream explicitly and
returns false only for a missing object; hash failures, cancellation, and I/O errors throw.
Empty-buffer reads never signal EOF, and force-verification after failure still throws.
`ContentInfo.Format` reports raw or seekable Zstandard.
`StoredSize` aliases the existing `CompressedSize` property on metadata and store
results, making the physical byte count clear even for raw content.

`reader.ReadExactlyAtAsync(offset, destination)` handles short reads and throws
`EndOfStreamException` for an incomplete range. `reader.AsStream(leaveOpen: true)`
provides a read-only seekable stream with an independent position. The default
`leaveOpen: false` transfers reader disposal responsibility to the adapter. The
adapter retains partial-read integrity semantics and is not safe for concurrent
position-changing operations. Synchronous stream reads block; prefer async APIs.

ROMD remains responsible for authorization, release paths, reference ownership,
and frontend metadata. CAS knows none of these concepts.

## Physical representation

The original byte sequence's SHA-256 remains the key. The existing sharded path
layout is unchanged. Physical format never participates in content identity.

| Suffix | Representation | Random reader |
| --- | --- | --- |
| `.raw` | Original bytes | Direct positional I/O |
| `.szst` | Independent Zstandard frames and trailing seek table | Indexed positional I/O |

The raw suffix remains configurable; `.szst` is reserved. Objects in the removed
continuous format (`.zst`) are not recognized, so a root containing them must be
recreated.

New compressed writes follow the [Zstandard seekable format](https://github.com/facebook/zstd/blob/dev/contrib/seekable_format/zstd_seekable_compression_format.md):
independent frames, followed by a skippable seek table with compressed size,
uncompressed size, and the low 32 bits of XXH64 for each frame. Ordinary Zstandard
decoders can read the concatenated frames and skip the table.

The ROMD reader requires the checksum table variant and validates each object
against itself, not against configuration: every frame but the last decodes to one
uniform size from 64 KiB to 1 MiB, the last frame is no larger, each stored frame
fits its compression bound, and the frames and table account exactly for the file.
Before allocating anything in proportion to the footer's frame count, the reader
checks that the table fits the file and carries its magic and exact length where
the footer places it. `MaxContentSize` and `SeekableFrameSize` govern only new
writes, so changing them never makes an existing object unreadable. The table
streams through a pooled buffer; the retained index costs 12 bytes per frame, about
3 MiB for 64 GiB at 256 KiB frames. Frame buffers use the array pool, whose bucket
rounding can retain up to 1 MiB decoded plus 2 MiB compressed at the maximum frame
size, excluding decoder state. Aggregate reader memory scales with active readers;
delivery adapters must bound admission using their deployment budget. The CAS does
not impose a global reader limit. Unsupported table flags fail closed.

Positional readers share seek tables through a cache bounded by
`SeekIndexCacheBytes` (`Romd:Storage:SeekIndexCacheBytes`, default 64 MiB; `0`
disables it), evicted least recently used first. An entry matches only the file it
was read from, by length and last write time, so an object deleted and republished
with another layout is read again; a deletion through the same store evicts it.
Concurrent first opens may each read the table, and the copies are equivalent.
Sequential reads neither use nor fill the cache, since they amortize the table over
the whole object. `cas.index.cache.hits` and `cas.index.cache.misses` count lookups.
Measured on 2026-09-27 with `tools/Romd.Storage.Benchmarks --vfs` (synthetic
objects, Apple M2 Max, macOS, .NET 10, warm OS cache, no HTTP layer): a 64 KiB
range on a 64 GiB object fell from 1.2 ms to 0.1 ms at the median and stopped
allocating its 3 MiB table. Below 1 GiB the difference was within noise.

`SeekableFrameSize` is configurable from 64 KiB to 1 MiB and defaults to 256 KiB.
Changing it affects new writes, not existing readers. `WritePolicy` is
`SeekableZstandard` (the default) or `Raw`, which skips compression entirely.
Hosts bind both from `Romd:Storage`, together with `MaxContentSize` (default
64 GiB); the separate ROM upload limit may not exceed it. Policy changes do not rewrite existing
objects during deduplication.
The default
`MinCompressionRatio = 0.95` applies, as the final check, to the entire representation
including the index. If it does not save 5%, the writer publishes raw bytes instead. Content
no larger than four frames is read once into pooled memory, verified and encoded there, and
only the representation it keeps is written to staging; a source longer than its reported
length is left to the streaming writers. Larger content is sampled first: the writer compresses its leading four
frames with the same level and framing, and if they miss the ratio it writes raw
bytes without producing a full seekable candidate. A passing sample is not a
verdict; the whole-representation ratio still decides, so content whose prefix
compresses but whose body does not still ends raw. Content with an incompressible
prefix and a compressible body is stored raw. Only the representation kept for
publication is synced to disk; a discarded seekable candidate is deleted unsynced. The writer
rechecks the original SHA-256 on each publication pass, including raw fallback.
All writing and hashing passes, including the initial hash of seekable input, enforce
the size limit; the compression sample and the in-memory decision read at most four frames
of already length-checked input, plus one byte to observe its end. Settings are validated
and snapshotted at store construction.

Writes are two steps. `StageAsync` hashes and encodes into a private staging file
without the root mutation lease, so callers can do the expensive work before taking
their own locks. `IStagedContent.PublishAsync` then deduplicates or renames under the
lease. A caller that already knows the SHA-256 passes it as `expectedKey`. That skips
the initial hashing pass, and content already published is not read at all. Encoding
still verifies the key. If staging found an object published that is deleted before
publication, `PublishAsync` encodes it again from the source, so a seekable source must
stay open until the staged content is disposed. `StoreAsync` is staging followed by
publication.

A read locates its first frame by division, since frames are uniform, decompresses
only intersecting frames, validates their checksums, and copies the requested slices. The per-reader
cache is bounded and ephemeral; there is no persistent expanded ROM cache.

## Integrity guarantees

- Sequential full reads: SHA-256 verification against the CAS key.
- Seekable partial reads: non-cryptographic per-frame checksum verification.
- Raw partial reads: no per-read integrity verification.

Frame checksums detect accidental corruption; they do not authenticate the index
or prove that a range belongs to a particular whole-file SHA-256. Cryptographically
verified ranges would require a trusted chunk-digest manifest or Merkle structure,
which is deliberately not claimed or implemented here. Higher layers requiring
whole-object verification must still use full verified retrieval.

## Staging, publication and recovery

Writes hash, compress and stage without the mutation lease. Each staging file is a
`.tmp-<32 hex digits>` file in the root's `.staging` directory, on the objects'
volume so publication is an atomic rename, created exclusively and held open
without sharing until publication. Backups exclude that directory. The lease covers only the
authoritative deduplication check and publication, which flushes the staging file
and moves it into place without overwriting. Long uploads therefore do not hold
the lease while other writers wait. This is process-interruption recovery, not a guarantee against
power loss or filesystem failure; directory durability and backup quiescence
remain operational concerns.

Cancellation and errors delete staging files in-process; a killed process can
leave `.tmp-*` files. `CleanupTemporaryFilesAsync` takes the mutation lease,
deletes only writer-generated staging names that no process still holds open,
skips symlinks, and leaves published objects and unrelated files alone. The
worker runs it once at startup, and retrying is safe. It scans only the staging
directory, so the lease it holds is brief whatever the store's size. On Unix, a cleanup that
lands between a staging file's creation and its lock can remove it; that store
then fails at publication without publishing wrong bytes.

`EnumerateAsync` lists each published object once from a lock-free walk of the
shard directories, never staging files. It is a moving snapshot, so callers acting
on it re-check under their own coordination: the orphan sweep checks rows a batch
at a time and re-checks each candidate under the file's mutation lock before
deleting it. Nothing outside the store reads the directory layout.

Reader opens never wait for the lease. A resolve/open race is retried; once
opened, handles preserve the original snapshot during deletion on filesystems
honoring the platform's open-file semantics. Every mutation acquires an OS-backed
exclusive lease on the permanent `.romd-mutation.lock` file. This serializes
deduplication, publication, deletion and cleanup across cooperating processes
while readers remain independent. A killed process releases its lease
automatically. Never delete or replace the lock file while any process is using
the root. All writers must run this implementation; older writers and external
file mutations bypass coordination. Supported scope is local filesystems with
functioning exclusive file sharing, not distributed/network filesystems. The
root-wide lease serializes only publication; hashing, compression and staging
run in parallel. Cancellation interrupts waiting; store disposal cancels and
awaits all admitted mutations and their cleanup. A source that ignores
cancellation can delay disposal.

## Validation and performance

Run storage correctness tests:

```sh
dotnet test tests/Romd.Storage.Tests/Romd.Storage.Tests.csproj
```

Run backend safety checks with Docker available:

```sh
mise run test
```

The tests cover ranges across frame boundaries, short final frames, EOF,
concurrent readers, corrupt frames/checksums/indexes, non-uniform frames, corrupt
frame counts in sparse files, tables spanning read batches, reads beyond writer
settings, deduplication, staging cleanup, cancellation, and open-handle lifetime.
A corrupted first frame with a successful tail read establishes that a late read
does not decompress earlier frames. Cleanup skips a real child writer's live
staging file and removes it after the child is killed; power-loss recovery is not
tested. Windows/Linux execution and real emulator workloads remain follow-up
validation.

The standalone benchmark uses temporary isolated storage, accepts optional input
file paths, verifies every sampled read, and never modifies supplied files:

```sh
dotnet run -c Release --project tools/Romd.Storage.Benchmarks
dotnet run -c Release --project tools/Romd.Storage.Benchmarks -- /path/to/sample.bin
```

No arguments generates deterministic synthetic repeated, mixed, and incompressible
16 MiB inputs. It compares 64/256/1024 KiB frames, random 4 KiB reads, hot tail reads,
full verified sequential throughput, stored size, and an in-memory legacy-tail
decode baseline. Results use warm OS caches and are not disk-cold, network, VFS,
or emulator measurements. The tool is an exploratory benchmark, not a timing gate.
The recorded synthetic run (macOS arm64, .NET 10) showed the intended trade-off:
on a repeated fixture, larger frames stored less (about 8 MiB at 64 KiB versus
0.5 MiB at 1 MiB) but random 4 KiB reads cost more (about 50 µs versus 260 µs
p50); incompressible input selected raw storage.

Acceptance: exact bytes and unchanged identities, bounded random-read work,
explicit integrity semantics, and reads that validate each object against itself.

Follow-ups: broader content and emulator-trace benchmarks before deployment tuning;
backup coordination; frontend-specific VFS layouts; authorized HTTP range delivery.

## Diagnostics and remaining tuning

Subscribe to the `Romd.Storage` meter for requested/returned/stored/decoded bytes of
positional reads and sequential seekable retrieval, frame-cache hits, active readers, read/open duration, mutation-lease
wait duration, detected corruption failures, and staged bytes synced to disk
(`cas.write.synced`, which counts only kept representations). Instruments have no content-key,
path, or user labels. Read duration includes queueing for admitted reads; mutation
wait records successful lease acquisition. These are storage signals, not network
or application request metrics. Cancellation and I/O errors still propagate.

The expanded benchmark adds a direct raw baseline, p99, repeated cross-boundary
reads, alternating regions, open/close cost, same-reader mixed-size concurrency,
independent readers, allocation deltas, and whole-run peak working set. Allocation
deltas include process-wide activity; pooled memory reuse affects them. Results
remain warm-cache measurements. Cold-cache, real emulator traces, Linux/Windows
filesystem behavior, and WAN delivery require separate runs. The one-frame cache
and reader serialization remain deliberately unchanged pending workload evidence.

The test child executable in `tools/Romd.Storage.TestHost` is a fixture built through
the existing storage test project; it is not a new test suite or production host.
The storage library adds no application schema or generated API contract changes.
VFS paths, library authorization, protocols, and client caching remain outside it.

A 40-ROM GBA sample (308 MiB, macOS arm64, .NET 10, Zstandard level 3) saved
52.9% of raw storage at 256 KiB frames, within 2.7% of continuous Zstandard;
64 KiB frames used about 3% more space but cut the median per-file random-read
p95 from 375 µs to 140 µs. This is not a recommendation for discs or every
emulator.

## Large-file benchmarking

File arguments larger than 128 MiB automatically use the streaming harness;
`--streaming` selects it explicitly for any file size:

```sh
dotnet run -c Release --project tools/Romd.Storage.Benchmarks -- --streaming /path/to/package.pkg
```

It processes one source and one CAS policy at a time without loading the whole
input into a managed array. A temporary continuous-Zstandard baseline is hashed,
round-trip checked, and deleted before CAS trials. Each policy checks identity,
full sequential verification, and sampled random/boundary/alternating reads.
Ensure temporary disk capacity for the largest representation plus filesystem
headroom. Inputs are opened read-only; CAS roots and baselines are disposable.
The production 5% compression-savings threshold remains enabled, so a seekable
policy may select raw storage. Recorded latencies then describe raw reads.

These measurements follow full sequential reads and do not control the OS cache.
They measure large-package byte access, not installation or emulator gameplay.

Recorded large-file runs (macOS arm64, .NET 10, one warm-cache run each):

- Two PS4 packages (2.7 GB and 1.6 GB) were effectively incompressible (under
  0.2% continuous savings); every seekable policy fell back to raw, and attempted
  compression made ingest roughly 2.4–2.9 times slower than requesting raw.
- A 187 MiB NKit GameCube ISO saved 16–22% with seekable frames (29% with
  continuous Zstandard). Random 4 KiB p95 was about 150 µs at 64 KiB, 390 µs at
  256 KiB, and 1.4 ms at 1 MiB.

None of these runs measured network, VFS, installation, or gameplay access.

## Seekable default

On 2026-09-27 seekable Zstandard with 256 KiB frames replaced raw as the write
default. A frame-size benchmark over SNES cartridges, a 1.46 GB GameCube ISO,
a 4.5 GB PS2 DVD image, and a replay of 601 recorded Dolphin disc reads found
per-request opens cheap, 256 KiB frames the best balance of small-read latency
and compression, and GameCube discs still stored raw under the 5% threshold.
Legacy continuous objects were dropped rather than converted because no
installation beyond testing held them. Seekable reads then became self-validating,
so readers carry no size or frame limits and write settings matter only to writers.

On that PS2 disc image, routing sequential retrieval through the
validated frame reader measured 798 MiB/s at 256 KiB frames against 808 MiB/s
through the previous streaming decoder, 756 against 833 at 64 KiB, and 827
against 882 at 1 MiB (one warm-cache run each). The difference pays for
per-frame checksum verification and bounded decoding.
