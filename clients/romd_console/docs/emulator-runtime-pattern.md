# Emulator Runtime Pattern

Trust code over doc. This reference describes the intended shape, but current
source and tests are authoritative when they disagree.

## Shape

Runtime launching is split into four layers:

- `RuntimeProfile`: immutable shipped profile id, adapter id, supported
  platforms, and dependency requirements.
- `RuntimeDependencyResolver`: satisfies a profile's executable, core, and BIOS
  requirements for one target.
- `RuntimeAdapter`: prepares ROMD-scoped directories/config and builds a
  structured `CommandPlan`.
- `RomdPlayCoordinator`: resolves the target, chooses the preferred profile,
  prepares dependencies, and starts the launch provider.

## Profile Registry

`BuiltinRuntimeProfiles.all` is the shipped registry. Profile ids are persisted
in device-local runtime override rules, so ids are append-only once shipped.
Profile order matters: with no override, the first matching profile for a
platform is the default. Put alternates after the default profile they compete
with.

## Resolution And Provisioning

Profiles express requirements. Resolvers satisfy those requirements without
adapters scanning arbitrary files:

- RetroArch profiles require the RetroArch executable and a libretro core.
- Standalone profiles require their executable and, when applicable, BIOS files.
- BIOS resolution matches by documented hash where available and returns a
  runtime-specific BIOS directory.
- Managed artifacts are provisioned through catalog/provisioner code before the
  adapter sees the launch plan.

## Adapter Contract

Adapters own runtime-specific side effects and command construction. A launch
command is always:

- executable path plus `List<String>` arguments,
- `runInShell: false` through `ProcessRunner`,
- `contentRoot` as working directory,
- explicit environment overrides only when the runtime needs a scoped user
  directory.

On macOS, resolved `.app` bundles are launch inputs, not process executables.
Adapters must map them to their inner executable, typically
`Contents/MacOS/<bundle-name>`, before calling `ProcessRunner`.

On Linux x86_64, ROMD provisions the official stable RetroArch AppImage bundle
and per-core `.so.zip` artifacts. The Linux unpacker requires `bsdtar`
(`libarchive-tools`) or 7-Zip and marks the AppImage executable. Launch sets
`APPIMAGE_EXTRACT_AND_RUN=1`, avoiding a hard FUSE dependency while keeping the
AppImage process directly supervised.

RetroArch has a bespoke adapter because it needs core selection and a generated
RetroArch config. DuckStation and PCSX2 use `StandaloneRuntimeAdapter` plus
per-runtime config writers and user-directory environment overrides.

## Save, State, And Config Isolation

Downloaded content is evictable. Saves, save states, screenshots, runtime user
data, and generated config are durable ROMD-owned data:

- content:
  `<base>/content/servers/{serverInstanceId}/{platform}/{title}/{release}`
- saves:
  `<base>/profiles/{localProfileId}/servers/{serverInstanceId}/games/{platform}/{title}/saves`
- states:
  `<base>/profiles/{localProfileId}/servers/{serverInstanceId}/games/{platform}/{title}/states`
- per-title generated config:
  `<base>/config/servers/{serverInstanceId}/{platform}/{title}`
- standalone native settings: `<base>/runtimes/{runtime}/user`

Gate 2 shipped these instance-scoped path constructors and the instance-scoped
install identity. Releases of one title within the same server instance share
save/state/config roots; another ROMD instance cannot collide through reused
encoded ids. Gate 3 central launch authorization and Gate 4 profile play history
remain separate from the path layout; both are now accepted.

Every gameplay writer must point its runtime at `saveRoot` and `stateRoot`.
RetroArch stores ROMD's generated per-title session config under `configRoot`.
Standalone emulators may instead preserve their native device-wide settings in
a ROMD-scoped runtime user directory, while reasserting profile-owned save and
state paths before every launch. Using `contentRoot` for any save, state, or
config data makes durable data evictable and should be treated as a bug. The
active launcher profile owns save/state roots; controller seating and
guest-profile assignments do not redirect progress.

No released console build predates profile partitioning, so gameplay launch has
no legacy save migration or initialization-marker path. It resolves the active
profile roots directly. A future save importer must be an explicit feature and
must never move, delete, or silently adopt external data on launch.

## Ended-Session History

Gate 4 records profile history only when a coordinator session returned
`LaunchStarted` and that same started session later reports `LaunchExited`.
Both zero and nonzero exit codes count. Denied, cancelled, failed, and
`LaunchNotStarted` outcomes do not, including a fabricated
`LaunchNotStarted(LaunchExited)` result.

The record consumes the exact resolved launch profile, server instance, title,
and release after process exit. A later profile/server composition change
cannot redirect ownership. This bookkeeping does not change resolution,
provisioning, adapters, configuration, or process construction:

```text
artifact resolver: no - history consumes the exact resolved launch identity after process exit; runtime dependency resolution did not change
config writer: no - existing roots/config writers unchanged
```
