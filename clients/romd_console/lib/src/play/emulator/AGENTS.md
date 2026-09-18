# AGENTS.md - Console Emulator Runtime

Runtime-specific invariants only.

- `RuntimeAdapter` is the adapter boundary. A new emulator or source port gets
  an adapter plus registration/provisioning support; code above the adapter
  should keep depending on `RuntimeProfile`, `LaunchProvider`, and
  `RuntimeDependencyResolver`.
- Shipped `RuntimeProfileId` values are immutable. Persisted override rules
  reference them; changing a default means appending a new profile, not renaming
  an existing one.
- Profile order is default selection. The first profile supporting a platform
  wins when no override rule applies, so defaults stay before alternates.
- Process commands use structured arguments. Build `CommandPlan.arguments` as a
  `List<String>` and keep `ProcessRunner` calls at `runInShell: false`; never
  build shell-string launch commands.
- Save and state paths are ROMD-scoped roots from `ResolvedPlayTarget`.
  Config writers must point emulators at `saveRoot` and `stateRoot`. Use
  `configRoot` for per-title generated configuration; a standalone emulator may
  instead keep device-wide native settings in its ROMD-scoped runtime user
  directory. Never use `contentRoot` for durable save/config data.
- `contentRoot` is the working directory and evictable content location only.
