# Development demo workspace

The repository's ignored **`.demo/` directory** owns the demo inputs, tools and
receipts. `mise run dev:demo` reads its explicit, hash-locked manifest. It never
scans an external collection during ordinary startup or seeding.

## Prepare once

Install the [development prerequisites](getting-started.md). Preparation copies an
explicit selection from a local collection that you supply; ROM and BIOS files
must be lawfully obtained, and the repository ships none of them.

The selection is a private **curation profile**, read from the ignored
`.demo/profile.json` by default or from `--profile PATH`. The repository ships
only the neutral [example profile](../../scripts/dev/demo-profile.example.json);
there is no tracked default, so preparation from a collection fails with a
pointer to the example when no profile is found. Create one and preview it:

```sh
mkdir -p .demo && chmod 700 .demo
cp scripts/dev/demo-profile.example.json .demo/profile.json
# Replace the placeholder entries with files from your collection, then:
mise run dev:demo:prepare --from /absolute/path/to/collection --plan
mise run dev:demo:prepare --from /absolute/path/to/collection
mise run dev:demo --plan
```

The example validates but names placeholder files, so preparing with it
unchanged fails on the first missing file. Profile format (`schemaVersion` 1):

- `name`, `catalogSite`: the profile's label and the signed DAT mirror (HTTPS).
- `roms`: a non-empty list of `system`, `source` and `purpose`. Each `source` is
  `roms/<complete No-Intro DAT name>/<archive>.zip`, relative to the collection
  root; the directory names the DAT that must contain the archive's single member.
- `bios`: a list, which may be empty. Each entry adds `size` and lowercase hex
  `md5` to `system`, `source` and `purpose`; preparation refuses other bytes.

Only listed files are read: there is no globbing, discovery or substitution, and
a missing file fails with its name. Paths stay inside the collection root. The
manifest records the profile's SHA-256; after preparation, seeding, serving,
subscriptions, copying and DAT refreshes read only the manifest and need no
profile. A changed profile is refused until `--refresh --from` re-prepares it.

Disc, container and multi-disc scenarios need additional explicit assets and
appropriate catalogs. The native subscription directory also exposes other
published catalogs, including Redump, through the ordinary Systems workflow.

Original local archives are copied byte-for-byte into content-addressed paths
under `.demo/roms/`. Firmware is copied into `.demo/bios/`. Originals are never
moved, linked, modified or deleted. ZIP members are read with bounds and without
extracting archive paths to disk. Limits are 32 MiB per file/member and 128 MiB
of decoded ROM/BIOS payloads. Runtime packs, shaders, executables and unlisted
firmware are not discovered.

## Use the demo in another worktree

`.demo/` is per checkout and must not be a link. A linked worktree copies
another checkout's prepared workspace instead of re-reading the collection:

```sh
mise run dev:demo:prepare --from-workspace /absolute/path/to/main/.demo --plan
mise run dev:demo:prepare --from-workspace /absolute/path/to/main/.demo
mise run dev:demo --plan
```

The copy is local and offline. It re-verifies the source manifest's hashes and
DAT-to-payload coverage, then copies only the declared ROM/BIOS copies and DAT
snapshots with their provenance receipts. The production-pinned reader and public
root are copied only when they match this checkout's Dockerfile pins and host
platform. Otherwise, run `mise run dev:demo:prepare` afterwards to build them;
that needs no profile.
The source is only read. Its `profile.json`, `state/` receipts, fixtures, logs
and `catalog-cache/` are never copied, so seeding here creates this checkout's own
receipts.

The TUF cache stays with its owner because it is mutable rollback state. Copying
it would couple two checkouts' expiry and version history. Copying DATs needs no
cache, because hashes from the verified manifest pin the snapshots. A later
`--refresh` here starts a new cache from the hash-pinned root, and the reader
verifies the signed chain and expiry before it changes a snapshot. The native
hosts keep their own reader cache under `.data/romd`.

The command refuses this checkout's own `.demo`, links, a missing or failing
source, a destination `.demo/profile.json` that differs from the source
manifest's profile, and an existing prepared workspace with
different inputs. It never overwrites; an identical workspace is a verified
no-op. The manifest is written last and verified in place. A failed copy removes
every file it created, leaving no prepared workspace. `--from-workspace` cannot
be combined with `--from`, `--refresh` or `--profile`.

## DAT and BIOS research decisions

Prepare fetches **complete No-Intro catalogs** for every selected system from
ROMD's signed mirror. It builds the existing Go/TUF reader using the exact source
archive checksum, source revision, Go version and independent public trust-root
hash pinned in [Dockerfile](../../Dockerfile). The explicit Go tool is installed
through mise if needed; it is not an additional mandatory source-host prerequisite.
No mutable sibling checkout or first-download trust root is used.

The reader verifies the signed metadata, catalog/system/name binding and exact
DAT bytes. Its persistent `.demo/catalog-cache/` retains expiry/rollback state;
never delete that cache to bypass verification failures. An acquisition failure
leaves the prior manifest and retained objects intact. Upstream acquisition may
be delayed while the authenticated mirror retains a complete prior catalog;
the snapshot receipt identifies the exact publication and document used.

Preparation matches every selected cartridge member's exact size and SHA-1 to
one entry in the appropriate complete DAT. Choose the DAT variant that matches
the archive representation, such as a headered NES catalog for headered dumps.
A missing or ambiguous match fails preparation; generating a self-matching game DAT is not a fallback. DAT files are
stored unmodified in `.demo/dats/`, with their authentication/provenance receipts.
The complete catalog remains available for catalog and subscription scenarios;
the demo library explicitly includes only the selected title IDs.

No-Intro's primary database is [DAT-o-MATIC](https://datomatic.no-intro.org/).
The signed distribution and update boundary is maintained by the
[companion publisher](https://github.com/MoonlarkStudios/romd-dat-catalogs) and
[ROMD subscription runbook](../features/dat-subscriptions.md).

Each profile BIOS entry declares its file length and MD5, for example as
documented by the target emulator core. Preparation checks those values and
locks SHA-256 as well. The helper creates a clearly named local **BIOS
Inventory** DAT for firmware intake; that inventory is not presented as an
upstream No-Intro catalog. BIOS use is core-specific and often optional. No
firmware or commercial ROM is downloaded by the preparation command.

## Start, seed and subscribe

```sh
mise run dev:demo:serve
# Second terminal, same checkout:
mise run dev:demo
mise run dev:demo:subscriptions
```

Without `--wait`, seeding fails immediately unless this checkout's session is
already `ready`. In a single terminal or agent shell, start the controller in the
background and let seeding wait for it:

```sh
mise run dev:demo:serve >"${TMPDIR:-/tmp}/romd-demo-serve.log" 2>&1 &
mise run dev:demo --wait          # default bound 900 s; or --wait SECONDS
mise run dev:demo:subscriptions --wait
```

`--wait` polls the same record as `mise run dev:status` for this checkout only.
It prints state changes and continues through `starting` and `reloading`. An
inactive session is tolerated for 90 seconds while the controller checks tools
and containers. Losing an active controller fails immediately, as does any other
active state (such as `reload-failed`, `backend-exited` or `stopping`) or
reaching the bound; inspect `dev:status` and its run logs.
Stop the background controller like Ctrl-C: send `SIGINT` to the `pid` reported
by `mise run dev:status`, then confirm the state is `stopped`. Keep the log
outside the repository; it can contain local paths.

`dev:demo:serve` runs the ordinary worker-first source controller, with the
prepared reader/root/site supplied only to its child environment. Checkout
ports, database, `.data/romd` durable storage, reload and owned shutdown still
follow [development setup](getting-started.md). `.demo` contains import inputs;
it does not replace CAS, signing keys, server identity or the database.
`mise run dev` also works for offline seeding from prepared DATs; use the demo
serve command to enable signed subscriptions on the native hosts.

Seeding uses the existing authenticated admin APIs and worker jobs to import the
full DATs, original ZIP archives and firmware, publish a dedicated IncludeOnly
library, and assign a consumer account. It verifies clean job completion and
consumer login/library/owned-title visibility plus exact BIOS download bytes.
The library allows unknown ratings
for this development selection and does not become the default library. Existing
real-title metadata/artwork and existing user passwords/roles/assignments are
preserved. The consumer email is derived from the dataset fingerprint; the command
prints it with development password `RomdDemo123!`.

The administrator is the normal Development bootstrap account (`admin@localhost`,
`ChangeMe123!` unless customized). Supply `ROMD_DEMO_ADMIN_EMAIL` and
`ROMD_DEMO_ADMIN_PASSWORD` privately in the seeding terminal's environment when
needed. Credentials never belong in command arguments, tracked files or receipts.

The subscription command discovers the authenticated directory, subscribes/checks
each selected catalog and retains its preview in `.demo/state/`. **It never calls
the approval endpoint.** The checked candidate can be newer than the frozen
snapshot; review and approve it through **Systems → Add source → Subscription**
or the existing source controls. Worker checks remain enabled on their ordinary
schedule, with future activation requiring review. After an approved update,
explicitly refresh the workspace snapshots before reseeding if their hashes differ.

Reruns of preparation verify and reuse the existing workspace without fetching
new DATs. Reruns of seeding reuse accepted jobs, the library and consumer account.
Terminal job errors/rejected files fail; inspect **Jobs** and retry that job rather
than discarding receipts. `--timeout SECONDS` bounds each job/source-link wait
(default 600). Changed active DAT bytes or an edited demo account/library produce
an error instead of being overwritten.

To intentionally refresh DAT snapshots using the already staged files:

```sh
mise run dev:demo:prepare --refresh
```

To revise the profile or source bytes, edit `.demo/profile.json` (or pass
`--profile PATH`) and use
`dev:demo:prepare --refresh --from /absolute/path/to/collection`. A complete
candidate is validated before the manifest is atomically replaced. Content-addressed
objects and old dataset receipts remain available; no global reset/prune runs.

## Private layout and qualification

```text
.demo/
  profile.json              # Private curation profile (input to --from preparation)
  manifest.json             # Explicit inputs, cases, hashes and DAT provenance
  roms/  bios/  dats/        # Immutable prepared copies and full DAT snapshots
  tools/                    # Production-pinned native reader and public root
  catalog-cache/            # Persistent TUF verification state
  fixtures/                 # Explicit synthetic qualification assets
  state/<fingerprint>/      # Job receipts, consumer verification, subscription reviews
  logs/                     # Preparation/reader diagnostics
```

`.demo/` is excluded from Git **and container build contexts**. It is local to
each checkout; see [another worktree](#use-the-demo-in-another-worktree) to copy it.
Do not commit its private content, inventory or raw diagnostics.
Its root uses mode `0700`; JSON receipts use atomic mode-`0600` writes. No signing
private key is needed. Normal shutdown preserves assets and runtime state.

Generated UI/edge-case fixtures are explicit: `mise run dev:demo --synthetic`.
They exercise missing content, authored metadata, original fixture covers and a
BIOS placeholder; the generated bytes are not usable games or firmware. This
offline qualification mode does not replace the prepared real demo.

After committing tooling changes:

```sh
mise run dev:smoke --demo
mise run dev:smoke --parallel --demo --demo-workspace "$PWD/.demo"
```

Qualification copies only the manifest's declared immutable inputs into an owned
temporary checkout, never an arbitrary external collection, another checkout's
receipts or its TUF cache. It seeds each dataset twice, checks reused job/library
identities and real consumer access, and exercises native signed subscription
checks/previews without auto-approval. Existing source lifecycle, checkout
isolation and owned-cleanup rules apply. Private evidence retains receipts, not
ROM/BIOS copies. See [source qualification](getting-started.md#source-workflow-qualification).
This does not establish emulator playback, firmware/core compatibility,
shared-browser-cookie SSO, or every file in the original collection.
