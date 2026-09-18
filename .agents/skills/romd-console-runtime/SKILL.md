---
name: romd-console-runtime
description: Use when changing ROMD Console emulator/runtime launching, runtime profiles, emulator adapters, dependency resolvers, runtime provisioning, BIOS handling, config writers, save/state/config paths, or controller mapping emitted into emulator config.
---

# ROMD Console Runtime

Use this skill for emulator and runtime work in `clients/romd_console`.

## First Reads

Read these before editing:

- `AGENTS.md`
- `clients/romd_console/AGENTS.md`
- `clients/romd_console/lib/src/play/emulator/AGENTS.md`
- `clients/romd_console/docs/emulator-runtime-pattern.md`

## Required Decisions

Record each decision in the worker summary exactly as:

- `artifact resolver: yes/no - <reason>`
- `config writer: yes/no - <reason>`

Artifact resolver decision:

- Yes when the runtime needs an executable, libretro core, BIOS files, managed
  artifact download, app bundle lookup, developer override, or local discovery.
- No only when all dependencies already arrive in `EmulatorLaunchPlan` through
  an existing resolver that is correct for the new profile.

Config writer decision:

- Yes when the runtime needs generated config, a scoped user directory,
  controller mapping, BIOS path configuration, memory-card/save paths, state
  paths, screenshots, or environment files.
- No only when structured CLI arguments and environment overrides fully point the
  runtime at ROMD-owned save/state/config paths.

## Implementation Checklist

- Add new shipped profile ids only by appending; never rename existing ids.
- Keep default profiles before alternates for the same platform.
- Register the adapter/provider/resolver/provisioner path needed by the profile.
- Build commands as `CommandPlan` with `List<String>` arguments.
- Keep process launching at `runInShell: false`.
- Route save/state/config to `saveRoot`, `stateRoot`, and `configRoot`, not
  `contentRoot`.
- Add tests for profile selection/order, resolver/provisioning behavior,
  command arguments, prepare/config output, and negative path handling.
- Build test fixtures from current `ResolvedPlayTarget` and launch-plan source
  fields; do not rely on older field names from memory or prior reports.

## Negative Checks

Before finalizing, search the touched runtime surface for:

```bash
rg -n "runInShell:\\s*true|Process\\.(start|run)\\(|/bin/sh|cmd\\.exe|shell" clients/romd_console/lib/src/play clients/romd_console/test/play
rg -n "contentRoot.*(save|state|config)|(save|state|config).*contentRoot" clients/romd_console/lib/src/play clients/romd_console/test/play
```

If either search finds a real shell-string launch or content-root save/config
path, fix it or report it as a blocking finding.

## Validation

Run from `clients/romd_console`:

```bash
mise run analyze
mise run test
```

If validation fails unexpectedly, read `../../docs/known-issues.md` before
retrying or changing code.
