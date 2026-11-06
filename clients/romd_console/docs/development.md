# Ottercade development

This guide covers building, running and testing the Ottercade console client.
Ottercade talks only to the ROMD consumer API (`Romd.Consumer.Host`), never the
admin host. The Dart package and persisted technical identifiers remain
`romd_console` for compatibility.

## Set up

Complete the [server development setup](../../../docs/development/getting-started.md),
including container-engine selection. Then, from the repository root:

```sh
mise run bootstrap --console
mise run doctor --console
mise run dev:containers
```

Ordinary-checkout container mode exposes the consumer API at
`http://localhost:11338`; source mode uses `http://localhost:5002`. Linked
worktrees use their own ports. Run `mise run dev:status` in the server checkout
for its actual URL and use the [development sign-in](../../../docs/development/getting-started.md#development-sign-in)
for that mode. Once the app runs, follow [Ottercade setup](using-ottercade.md).

From `clients/romd_console/`, initialize Flutter through mise and run the checks:

```sh
mise trust
mise install
mise run doctor
mise run analyze
mise run test
```

## Run

```sh
mise run run:macos      # macOS
mise run run:linux      # Linux (on a Linux host)
mise run run:windows    # Windows (PowerShell)
```

The run tasks supply the dev consumer API at `http://localhost:11338`.
For another stack, run Flutter directly, for example
`mise exec -- flutter run -d linux --dart-define=ROMD_CONSUMER_API_ORIGIN=http://localhost:5002`.
The `ROMD_CONSUMER_API_ORIGIN` define overrides the default (the web variable
name `VITE_ROMD_CONSUMER_API_ORIGIN` is also accepted). The origin is only a
locator: the console discovers the server's stable instance identity before
selecting it or touching credentials. See
[profiles and servers](architecture.md#profiles-and-servers-at-a-glance) for how
local data is scoped.

## Native target prerequisites

`mise install` provides Flutter and, on macOS, Ruby/CocoaPods. Native builds
still need the platform SDKs:

- **macOS/iOS:** a full Xcode installation and an active `xcode-select`
  developer directory.
- **Android:** Android Studio or an Android SDK configured through
  `flutter config --android-sdk`.
- **Windows:** Visual Studio or Visual Studio Build Tools with the *Desktop
  development with C++* workload and the optional *C++ ATL for x64/x86*
  component (`flutter_secure_storage_windows` includes ATL headers).
- **Linux:** the normal Flutter desktop prerequisites. Managed RetroArch setup
  supports x86_64 and needs either `bsdtar` (`sudo apt install libarchive-tools`
  on Ubuntu/Debian) or 7-Zip to unpack the official stable bundle. Ottercade
  launches the resulting AppImage through its extract-and-run fallback, so FUSE
  is not required.

## Useful tasks

| Task | Purpose |
| --- | --- |
| `mise run analyze` / `mise run test` | Static analysis and the test suite (the default checks) |
| `mise run generate:database` | Regenerate Drift code after changing `app_database.dart` |
| `mise run theme:check` | Theme token governance checks (Bash scripts; need `rg`) |
| `mise run build:linux` | Release build on a Linux host |
| `mise run bootstrap` | Repair or regenerate native runner files (`--no-overwrite`) |

The console has no visual baseline (golden) tests. Widget tests cover
structure, focus and behavior, and the theme checks enforce the design-token
boundaries; review visual changes by running the app. If visual baselines
return, record them on the platform that checks them: Linux and macOS
rasterize text differently, and the earlier macOS-recorded baselines failed on
Linux.

If a command fails unexpectedly, check the console entries in
[known issues](../../../docs/development/known-issues.md) before cleaning
generated Flutter state or changing code.

## Conventions

- Flutter is pinned in `.mise.toml`. Native runners are checked in for macOS,
  Windows, Linux and Android.
- Keep the UI controller-first: focus states, directional navigation and launch
  actions must work without a pointer.
- Keep generated code behind hand-written service boundaries.
- Read [`AGENTS.md`](../AGENTS.md) for the client's invariants before changing
  storage, authentication or emulator code.
