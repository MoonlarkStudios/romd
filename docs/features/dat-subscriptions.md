# DAT subscriptions

Subscriptions keep your catalog definitions up to date through ROMD's signed
No-Intro and Redump mirror. They download DAT definitions, not games. Available
systems and catalog forms come from the authenticated directory, rather than a
fixed PlayStation-only list. Choose the form matching your own dumps, such as
headered NES or big-endian N64.

## Subscribe and review

1. In a system, open **Catalog sources → Add source → Subscription**.
2. Choose a published catalog and **Subscribe and check**. For an existing
   compatible manual source, use its **Use subscription** action instead.
3. Review the saved candidate's additions, removals and changed checksums. Browse
   the searchable, paginated changes before approving **Import reviewed catalog**.
4. Follow the accepted job in **Jobs**. Refresh affected audience libraries when
   you are ready to publish the resulting catalog changes.

Enrollment can start the system's first source. It binds an existing source
explicitly, so same-named sources are not selected by guesswork. Repeated
enrollment returns the existing subscription. Repeating approval of the same
reviewed document returns the accepted job receipt.

## Updates and failures

Eligible subscriptions are checked daily. A worker sweep runs every 15 minutes;
failed checks back off after 1, 2, 4, 8, 16 and then 24 hours. Use **Retry check**
to retry immediately. Updates are always reviewed and approved manually.
A pending review retains its exact candidate until approval or another explicit
check. Installed catalogs remain active when a check fails.

The UI may show **Latest refresh delayed** for a retained published catalog.
It can still be offered for review, but the reader must verify current signed
metadata and exact candidate bytes. Missing files, expired metadata, trust errors
and integrity failures remain unavailable.

Inactive sources, removed systems, running imports and pending reviews pause
scheduled checks. **Manual updates** detaches subscription tracking while
preserving installed catalogs, files and jobs. See [system setup](system-setup.md)
for source lifecycle controls and [manual replacement review](dat-replacement-review.md)
for the shared document comparison.

## Trust and deployment

ROMD invokes the companion repository's `distribution candidate` command using
`ProcessStartInfo.ArgumentList`, without a shell or request-controlled command,
URLs, root, or catalog identity. This reuses go-tuf/v2 verification rather than
introducing a second TUF implementation. The standard admin, admin-dev, and worker Docker
targets include the reader built from
[reviewed companion revision `f41d480`](https://github.com/MoonlarkStudios/romd-dat-catalogs/commit/f41d480da850f534044d0ee690747af2ab5cd3c5).
The Go build image, source archive, and original public trust root are pinned by
hash. ROMD builds that reviewed source with the security-serviced Go 1.27.3
compiler; the pinned source revision and public trust root remain independent
of that compiler patch. The build verifies the compiler version and records it
with the resulting binary hash. Only the static binary
and public root enter the admin and worker runtime images. The consumer image
does not include the reader. No signing credentials are included.

Standard images enable the official signed data site by default. They fetch
metadata during explicit checks and due worker checks; they never contact Redump directly. A check does not
automatically approve a DAT. To disable these checks, set
`DatSubscriptions__Enabled=false`.

For development checkouts, [demo preparation](../development/demo-data.md) builds
the production-pinned native reader in ignored `.demo/`; `dev:demo:serve` supplies
these settings without requiring system-wide installation. For manual native
setups, build with the companion's mise toolchain and configure its executable
and independently pinned public root explicitly:

```text
DatSubscriptions__Enabled=true
DatSubscriptions__ReaderPath=/opt/romd/catalog-reader
DatSubscriptions__RootPath=/etc/romd/catalog-trust/1.root.json
DatSubscriptions__Site=https://moonlarkstudios.github.io/romd-dat-data
```

The official mirror publishes complete No-Intro and Redump catalogs. The signed
directory determines which catalog/form is available to this installation;
[the companion publisher](https://github.com/MoonlarkStudios/romd-dat-catalogs)
owns the coverage inventory.
Supply the pinned public root independently of the download site. The reader
retains TUF metadata under `Romd:DataDirectory/dat-subscriptions`; do not reset
that cache to bypass expiry or rollback rejection. Cache identity includes the
root bytes and publisher location. An OS lock serializes access; keep lock files.
The process has a two-minute deadline and downloaded candidates are size/hash
checked again before ROMD's semantic validation.

For a private local trial, set `DatSubscriptions__BundlePath` and clear
`DatSubscriptions__Site` (the standard image supplies a default).
It points at a complete local companion `distribution stage` bundle. The reader
uses the same TUF updater through a file-backed transport, with no network access;
local files still need valid signatures and unexpired metadata. Mount only the
bundle and the independently supplied public root. Do not mount private signing
keys. Local trial signing keys must be disposable and separate from production.
The bundle's synthetic GitHub release URLs are authenticated references mapped
to local assets, not a claim that those assets were publicly released.

## Contributor boundaries

Admin and worker hosts share the reader configuration, trust root and durable
verification cache. Consumer hosts expose neither subscription mutations nor the
reader. A check retains a candidate without activating a DAT version. Approval
atomically accepts the upload job, durable dispatch and subscription receipt;
activation rejects a changed reviewed baseline. Catalog/source locks prevent
concurrent checks or lifecycle changes from replacing a newer result.

Subscription state belongs to the current fresh database baseline. See the
[database boundary](../self-hosting/configuration.md#database-and-installation-boundary)
and the [subscription decision](../decisions/dat-subscriptions.md).
Publisher acquisition and distribution are maintained in the companion project;
ROMD does not provide unattended activation or a catalog history/rollback UI.
