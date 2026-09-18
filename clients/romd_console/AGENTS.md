# AGENTS.md - ROMD Console

Flutter console client for couch-distance browsing, local install/cache flows,
controller-first navigation, and emulator launching.

## Invariants

- This client talks to `Romd.Consumer.Host` only. Admin curation, ingestion,
  enrichment, and storage authorization stay outside the app.
- Profiles are local-first. Selecting a profile lands in the launcher without a
  session; connecting to a ROMD server is opt-in and reached from the launcher.
- Local profiles and install/runtime/controller state live in SQLite. ROMD
  passwords, access tokens, and refresh tokens never go in SQLite.
- Refresh tokens are stored through `RefreshTokenStore` in OS secure storage.
  Access tokens stay in memory.
- UI paths must remain controller-first: focus, spatial traversal, gamepad
  intents, and keyboard controls are first-class, not mouse-only afterthoughts.
- Keep handwritten service boundaries around generated code. Do not edit
  `lib/src/data/local_profiles/app_database.g.dart`; update Drift declarations
  and migrations in `app_database.dart`, then regenerate with
  `mise run generate:database`.

## Local Architecture

- `data/consumer_api_client.dart` is the handwritten consumer-host boundary.
- `data/local_profiles/` owns SQLite profile/install/runtime/controller state.
- `play/content/` owns local content materialization and durable save/state/
  config roots.
- `play/controllers/` owns device-wide controller mappings and preferences.
- `play/session/` owns runtime selection and launch coordination.
- `play/emulator/` owns emulator adapters, runtime dependency resolution, and
  provisioning. Read `lib/src/play/emulator/AGENTS.md` before changing it.

## Validation

Run from `clients/romd_console/`:

```bash
mise run analyze
mise run test
```

After changing Drift declarations or migrations, regenerate derived database
code before analysis/tests:

```bash
mise run generate:database
```

If either command fails unexpectedly, read the matching console entry in
`../../docs/known-issues.md` before cleaning generated Flutter state or changing
code.

## Emulator Routing

Use `.agents/skills/romd-console-runtime/SKILL.md` for emulator/runtime work.
Runtime profile ids are persisted user-facing contract keys. Add new profiles
for changed defaults; do not rename shipped ids.
