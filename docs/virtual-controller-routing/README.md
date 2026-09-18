# Virtual Controller Routing Isolation Research

This area contains Gate 0 evidence for ROMD virtual-controller routing. It is
separate from adapter correlation research because the question is stronger:
can ROMD prevent a managed emulator from receiving corresponding physical
input while it reliably receives the intended ROMD virtual device?

The shared contract and evidence format are defined in the [platform isolation
template](templates/platform-isolation-research-template.md). Platform reports
must use that template without weakening missing-data, mismatch, cleanup, or
upgrade rules.

## Evidence Index

| Platform | OS/build and architecture | Probe revision | Virtual backend | Isolation mechanism | Runtime artifacts | Classification | Validated | Status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| macOS | 26.5.2 / 25F84, arm64 | development Swift CLI; current milestone | CoreHID candidate, not activated | IOHID exclusive seizure | DuckStation v0.1-10998; RetroArch 1.22.2 Metal; PCSX2 2.6.3 | `notIsolated` | 2026-07-10 | current; RetroArch MFi received physical input during seizure |

See [the macOS report](macos-isolation-25F84.md) for the exact evidence and
untested matrix. `current` means the report accurately describes the evidence
state; it does not mean isolation is approved.

## Classification Policy

Every completed platform report uses exactly one classification:

- `isolated`: every required frozen runtime and hardware case blocks physical
  input, consumes the intended virtual input exactly once, preserves stable
  runtime selection, and cleans up safely.
- `runtimeScoped`: only explicitly named frozen runtimes satisfy those rules.
  This is evidence for owner review, not independent production approval.
- `notIsolated`: physical or duplicate input, unstable numbering/selection,
  unsafe cleanup, or another required mismatch remains possible.

An incomplete report remains `unclassified`. It must not use a passing
classification. One mismatch is blocking; successes are never averaged and a
successful exclusive-open call is not isolation evidence.

## Evidence Handling

Raw captures belong in a separately access-controlled evidence bundle, not in
Git. Before attaching excerpts, replace unrelated filesystem paths, usernames,
host serials, hardware UUIDs, provisioning identifiers, signing identities,
tokens, and secrets with typed redaction markers. Preserve controller identity
fields only when required to distinguish the tested devices and document each
redaction. Never invent missing identity or correlation.
