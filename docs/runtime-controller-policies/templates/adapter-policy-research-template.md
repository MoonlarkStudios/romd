# <Adapter> <Platform> <Pinned Build> Controller Policy Research

Status: research in progress

## Frozen Runtime Envelope

Record immutable URL, archive SHA-256, source revision, bundle/executable
versions, architectures, signature, platform/build, linked input library and
digest, backend and hints, mapping database and digest, ROMD revision, and
provider implementation. Never substitute a rolling alias for an asset URL.

## Primary-Source Analysis

Link exact-revision source lines and version-matched library documentation.
Explain initialization, enumeration/open APIs, filtering, sorting, hotplug,
classification, identifier construction, and whether identity-bearing
selection exists. Separate direct observation from inference.

## Read-Only Probe

Document commands and supported runtime logs/inventory. Capture independent
ROMD and runtime observations: order, provider/runtime id, name, GUID,
vendor/product, serial, path/location, transport, gamepad/joystick/fallback
classification, backend, mappings and hints. Record unavailable fields as
missing; never synthesize them. The probe performs no seating, mapping,
profile, or schema writes and never classifies a policy itself.

## Candidate Rule And Falsification

State one falsifiable rule, its prerequisites, expected results, and how a
mismatch disproves it before testing.

## Adversarial Matrix

For every case record: case id, envelope, hardware/transports, exact connection
sequence, ROMD inventory/seating, runtime inventory, candidate mapping, emitted
config, observed ports, P1 shortcut owner, result, and every mismatch.

- one controller
- two families A/B and B/A
- Change Order reversed from connection order
- disconnect/reconnect without ROMD restart; changed provider id
- identical controllers with and without distinguishable serials
- DualSense USB and Bluetooth; mixed USB/Bluetooth
- controller before and after runtime start
- exact offline P1; blocked same-name ambiguity; no-SDL/fallback
- runtime-only restart and ROMD-only restart

Minimum hardware: 8BitDo Pro 2 D-input and X-input, Xbox-style, DualSense USB
and Bluetooth, two identical controllers, mixed families, and fallback/no-SDL.

## Results, Mismatches, And Untested Cases

One unexplained mismatch blocks `verified`. List every missing case explicitly.

## Classification And Rationale

Use exactly `verified`, `acceptedBestEffort`, or `uncorrelated`.
`acceptedBestEffort` requires explicit owner approval before implementation.

## Policy Or Explicit Absence

State the exact policy id/rule and fail-closed prerequisites, or state that no
forced-reference policy exists. Provider ordinal remains diagnostic-only.

## Safe Fallback

Describe automatic runtime order, mapping behavior, shortcut ownership, and
no-SDL behavior when prerequisites fail.

## Invalidation And Revalidation

Invalidate for artifact/source, SDL/backend, mapping database, platform/arch,
ROMD enumeration, or reproduced hardware mismatch changes. Repeat the complete
matrix before restoring a policy.

## Automated Fixtures And Tests

List artifact/fingerprint, inventory parsing, prerequisites, reversed order,
identical/reconnect, writer gating, duplicate-reference, shortcut, and fallback
coverage with exact commands/results.

## Manual Hardware Evidence

Attach redacted raw observations. Do not convert absent evidence into a pass.

## Owner Approvals

Record decision, owner, date, exact boundary, limitations, and evidence link.
