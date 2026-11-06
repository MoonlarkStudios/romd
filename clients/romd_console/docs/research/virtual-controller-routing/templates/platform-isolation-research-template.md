# <Platform> <OS Build> Virtual Controller Isolation Research

Status: research in progress; classification `unclassified`

## Contract Version And Scope

Record the contract revision, ROMD revision, platform, OS/build, architecture,
probe/broker revision and digest, virtual descriptor revision and digest,
physical provider, isolation mechanism, exact runtime artifacts, and validation
date. This contract is platform-neutral: do not add OS objects, event codes,
handles, platform paths, or emulator ordinals to shared record fields.

## Normalized Contract

### Capabilities

Record supported player count; physical input; virtual input; exclusive
acquisition; hotplug; buttons; sticks; triggers; motion; touch; battery;
player indicators; and each output-feedback type. Every capability is
`supported`, `unsupported`, or `unknown`, with evidence. Unsupported controls
must not be silently dropped or represented as supported.

The conceptual backend operations are:

```text
capabilities
ensurePlayers(P1..P4)
applyRouteSnapshot(snapshot)
clearPlayer(player)
clearAll()
getPhysicalInventory()
getVirtualInventory()
getIsolationStatus()
receiveOutput(player)
shutdown()
```

Input uses `GamepadButtonPosition` physical-position semantics. A normalized
report contains `sequence`, monotonic `timestamp`, `connected`, buttons,
`leftX`, `leftY`, `rightX`, `rightY`, `leftTrigger`, and `rightTrigger`.
Sticks are clamped to `-1.0...1.0`; triggers to `0.0...1.0`. Record malformed,
out-of-range, duplicate, reordered, dropped, and coalesced reports.

### Inventory Records

For each inventory, unavailable fields are explicitly `missing`; never infer or
synthesize them.

Physical inventory record:

```text
observationId, observedAt, observationOrder, provider, providerDeviceId,
registryEntryId?, name?, product?, manufacturer?, guid?, vendorId?, productId?,
serial?, uniqueId?, pathOrLocation?, transport?, primaryUsage?, classification,
syntheticCompatibilityMarker?, backend?, mapping?, capabilities, redactions
```

Virtual inventory record:

```text
observationId, observedAt, observationOrder, backend, backendDeviceId,
stablePlayer, descriptorRevision, registryEntryId?, name?, product?,
manufacturer?, vendorId?, productId?, serial?, uniqueId?, primaryUsage?,
capabilities, lifecycleState, redactions
```

Runtime-visible inventory record:

```text
observationId, observedAt, observationOrder, runtimeArtifact, runtimeBackend,
runtimeDeviceId?, registryEntryId?, name?, product?, manufacturer?, guid?,
vendorId?, productId?, serial?, uniqueId?, pathOrLocation?, transport?,
primaryUsage?, classification, syntheticCompatibilityMarker?, mapping?,
enumerated, openAttempted, openResult, inputObserved, outputObserved,
hotplugObserved, selectedPort?, redactions
```

Provider/runtime identifiers are local observations, not correlations. A
correlation assertion requires the shared primary identity evidence and method
to be recorded separately.

### Observation States

Record enumeration, open, input, and output independently:

1. physical device is not enumerated;
2. physical device is enumerated but cannot be opened;
3. physical device opens but no physical input is received;
4. physical input is received;
5. intended virtual input is received exactly once;
6. physical and virtual input are both received;
7. a blocked physical representation alters runtime numbering or selection.

State 4 or 6 blocks `isolated`. Unexplained state 7 blocks both platform-wide
isolation and any affected runtime scope. States 1-3 are acceptable only when
state 5, stable selection, source attribution, and cleanup are independently
proven for every required case. Output receipt never substitutes for input
evidence.

### Classification Rules

Use exactly `isolated`, `runtimeScoped`, or `notIsolated` only after the required
matrix is complete. Until then use `unclassified` as report status, not as a
classification. `isolated` requires every required hardware/runtime case.
`runtimeScoped` names every passing frozen runtime and every excluded boundary
and requires owner review. Any duplicate event, unexplained port shift,
seizure/grab bypass, unattributed report, cleanup failure, or required mismatch
is `notIsolated` for the affected scope. Do not retry away a mismatch.

## Stable Virtual Identity Requirements

For each player, freeze product/manufacturer, descriptor, vendor/product,
serial/unique identity, creation order, and lifecycle. P1-P4 identities remain
stable across physical reconnect, route replacement, broker restart, and
runtime restart. Empty and reserved players remain present and neutral when the
backend claims persistent devices. A descriptor or identity change invalidates
all dependent runtime evidence.

## Route Snapshot Lifecycle

A snapshot is immutable, revisioned, validated before activation, and applied
atomically. It names stable players and physical routes using the strongest
provider identity available, records mappings/capabilities, and expires on
provider generation or reviewed-session revision changes. High-frequency
reports do not traverse Dart.

Before first activation, clear, reassignment, replacement, disconnect,
shutdown, or handled failure, send a complete neutral report and confirm it was
accepted when the backend can acknowledge reports. Reject partial, stale,
duplicate-player, unsupported-control, and unknown-device snapshots. On
rejection retain or restore a known neutral state; never partially apply.

## Neutralization, Crash, And Watchdog Rules

Neutral means connected state as designed, no buttons, centered sticks, and
zero triggers. Record neutral-report sequence/time and observed runtime state.
Normal shutdown, route clear, physical disconnect, runtime exit, Flutter exit,
broker handled failure, broker forced termination, and watchdog loss must
release physical acquisition and leave virtual devices absent or neutral by a
documented bound. Verify reacquisition by ordinary clients after release.

OS handle cleanup is necessary but not sufficient: prove no stuck runtime
input and no physical device left unavailable. State the watchdog owner,
liveness signal, timeout, privilege boundary, and behavior if either side
crashes. An unsafe forced-termination result blocks isolation.

## Frozen Environment

Record OS/build, architecture, hardware, transport, signing/notarization,
entitlements/privileges, permissions/prompts, runtime and probe hashes,
descriptors, providers/backends, mapping databases, settings, and exact
connection sequence. Missing permission or entitlement evidence remains
missing, not assumed.

## Primary-Source Analysis

Link version-matched platform, runtime, and provider sources. Describe
enumeration, open/acquisition, input, hotplug, synthetic representations,
virtual creation, output, permissions, distribution, and cleanup. Separate:

- primary-source fact;
- direct observation;
- inference or hypothesis requiring a trial.

## Probe Design And Commands

Document build and invocation commands, expected privileges, signing,
entitlements, redaction, output schema, and cleanup. The probe is explicitly
development-only, outside ROMD UI/launch paths, makes no seating/profile/
mapping/schema writes, starts neutral, forwards only proof input, neutralizes
before exit, and never assigns a classification automatically.

## Independent Inventories

Capture baseline, acquired/seized, virtual-active, released, reconnected, and
post-crash inventories independently for every platform API, ROMD provider,
and managed runtime. Do not merge representations before recording them.

## Acquisition And Isolation Semantics

For every representation record: still enumerated; open result; physical input
count; hotplug; output; numbering/order; selected port; permission/prompt; and
post-release behavior. Exclusive-open success is only an access observation.

## Adversarial Matrix

For every case record case id, frozen environment, hardware/transport, exact
sequence, independent inventories, acquisition, virtual identity, runtime order,
open/input behavior, attributed physical and virtual event counts, duplicates,
port, cleanup/neutralization, permissions, latency, output, result, and every
mismatch.

- 8BitDo Pro 2 D-input; 8BitDo Pro 2 X-input; Xbox-style controller
- DualSense USB; DualSense Bluetooth; mixed USB/Bluetooth
- two families A/B; same pair B/A
- identical controllers with distinguishable serials; without distinguishable serials
- controller before probe; controller after probe; disconnect/reconnect acquired
- reconnect with changed ROMD provider id
- emulator restart while probe active; probe restart while emulator active
- probe crash; later Flutter/ROMD crash; emulator crash
- fallback/no-SDL provider

## Duplicate Input And Runtime Selection

State the attribution method. Preserve raw counts and timestamps for physical,
virtual, and runtime observations. Report duplicates, unexplained events,
numbering changes, selected-port changes, and blocked-device influence. One
unattributed or duplicate event is a mismatch.

## Performance And Output Feedback

Measure source-to-virtual and source-to-runtime latency with clock source,
sample count, percentiles, maximum, report rate, dropped/reordered/coalesced
counts, and load conditions. Record output type, runtime source, virtual player,
physical target, latency, and failure. Mark rumble/LED/motion/touch/battery or
other unsupported capabilities explicitly.

## Permissions, Privileges, Entitlements, Signing, Distribution

Record requested and granted entitlements, provisioning profile/team boundary,
code signature, notarization, user prompts, install/activation flow, root/admin
requirements, sandbox implications, direct-distribution viability, and behavior
when denied. Development security bypasses and root-only manual procedures
cannot satisfy a production gate.

## Results, Mismatches, Untested Cases, And Safe Fallback

List every mismatch and untested case. The fallback is no production virtual
routing, no forced runtime references, automatic runtime order, no promise of
custom seating, and current fail-closed shortcut behavior unless a narrower
owner-approved runtime scope exists.

## Invalidation And Revalidation

Invalidate on OS/build, architecture, platform API/backend, probe/broker,
descriptor/identity, signing/entitlement/permission, physical provider, ROMD
identity/enumeration, runtime artifact/source/backend/config, SDL/mapping
database, hardware firmware/mode/transport, isolation policy, or reproduced
mismatch changes. Mark indexed evidence `invalidated`; repeat the full affected
matrix before restoring it. Partial spot checks cannot restore `isolated`.

## Automated Tests And Manual Hardware Evidence

List unit/build/lifecycle tests for parsing, malformed input, normalization,
atomic snapshots, repeated activation, neutralization, shutdown, watchdog, and
forced termination. Separately list manual OS/runtime/hardware cases and raw
redacted evidence. Automated tests cannot prove OS isolation.

## Owner Decisions

Record decision, owner, date, exact platform/runtime boundary, limitations,
evidence link, and revalidation trigger. A broad `runtimeScoped` production
policy requires explicit owner approval.
