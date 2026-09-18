# TestCore Runtime Pilot

Date: 2026-07-08

## Scope

Temporary branch: `codex/testcore-pilot`

Goal: simulate adding a fake standalone emulator runtime named `testcore`, run
runtime validation and negative checks, then discard all fake runtime code.

## Decisions

- `artifact resolver: yes - testcore needed executable discovery and managed artifact provisioning, so the pilot exercised RuntimeDescriptor, RuntimeCatalog, ManagedStandaloneRuntimeProvisioner, and StandaloneRuntimeDependencyResolver wiring.`
- `config writer: no - testcore launched with structured CLI arguments only and had no runtime-specific config, BIOS, controller config, save path, state path, or scoped user-directory format.`

## Walk-Through Result

Completed layers:

- Root routing: root, console, and nested emulator `AGENTS.md` files guided the
  pilot surface.
- Runtime skill checklist: profile id, adapter id, descriptor, artifact
  resolver, dependency resolver, provider registration, command construction,
  and tests were exercised.
- Nested emulator invariants: profile id was appended, process arguments were
  structured, and save/state/config directories came from `ResolvedPlayTarget`.
- Registration/provisioning: temporary `testcore` wiring covered
  `BuiltinRuntimeProfiles`, `RuntimeDescriptor`, `SystemRuntimeProbeEnvironment`
  build defines, `RuntimeCatalog`, `ManagedStandaloneRuntimeProvisioner`,
  `StandaloneRuntimeDependencyResolver`, and `EmulatorRuntimeLaunchEntry`.
- Test coverage: temporary focused tests covered profile registration, command
  shape, prepare directory creation, and process-runner invocation.

Missed by first pilot attempt and corrected before pass:

- The fixture used stale `ResolvedPlayTarget` names (`titleName`,
  `launchRelativePath`) instead of current `displayName` and
  `launchAbsolutePath`.
- The command expectation missed the shared standalone `.app` bundle mapping to
  `Contents/MacOS/<bundle-name>`.

## Negative Checks

Command:

```bash
rg -n "runInShell:\s*true|Process\.(start|run)\(|/bin/sh|cmd\.exe|shell" clients/romd_console/lib/src/play clients/romd_console/test/play
```

Result: only the expected `ProcessRunner` wrapper and invariant comments were
reported. No shell-string runtime launch was found.

Command:

```bash
rg -n "contentRoot.*(save|state|config)|(save|state|config).*contentRoot" clients/romd_console/lib/src/play clients/romd_console/test/play
```

Result: no matches.

## Validation Matrix

Pilot validation with temporary `testcore` code:

- `mise exec -- flutter test test/play/testcore_adapter_test.dart test/play/runtime_profile_test.dart`: passed.
- `mise run analyze` from `clients/romd_console`: passed.
- `mise run test` from `clients/romd_console`: passed.

Final validation after removing all `testcore` code:

- `mise run build`: passed. Output included pnpm ignored-build-script warnings,
  Vite/Rollup pure-comment warnings, and Vite chunk-size warnings; MSBuild ended
  with `Build succeeded` and `0 Warning(s)`.
- `mise run test`: passed deterministic backend tests.
- `pnpm lint` from `web`: passed.
- `pnpm test` from `web`: passed with known React `act(...)` warnings from
  `AuthProvider`.
- `mise run analyze` from `clients/romd_console`: passed.
- `mise run test` from `clients/romd_console`: passed.

Cleanup:

- Removed all temporary `testcore` code and deleted branch
  `codex/testcore-pilot`.
- Removed validation-generated host `wwwroot/` and web `dist/` artifacts.
