# Ottercade

Ottercade is ROMD's Flutter-based 10-ft frontend. This client is a consumer
application: it talks to `Romd.Consumer.Host`, not the admin host. The Dart
package and persisted technical identifiers remain `romd_console` for
compatibility.

## Setup

From the repository root:

```sh
docker compose -f compose.dev.yaml up --build romd-worker romd-admin romd-consumer
```

From this directory, initialize Flutter through mise:

```sh
mise trust
mise install
mise run doctor
mise run analyze
mise run test
```

## Run

```sh
mise run run:macos
```

On Windows, use:

```powershell
mise run run:windows
```

Profile creation currently prefills the ROMD server with the dev consumer API at
`http://localhost:11338`; the current baseline still permits clearing it and
launching shared installs. The planned profile-library work replaces that
behavior: the console first discovers a stable ROMD server instance identity,
then the profile selects that instance and acquires games through its live Store.
No selected instance means no ROMD Home or Local Library. Override the prefilled
default with `--dart-define=ROMD_CONSUMER_API_ORIGIN=...` or the existing web variable name,
`--dart-define=VITE_ROMD_CONSUMER_API_ORIGIN=...`.

The planned design stores local games by profile + stable server instance +
release after verified Store download/attach. Sign-out keeps the selected
instance and its cached games. Switching instances swaps remembered local
libraries without deleting them. A server moving origin preserves state; a
replacement at the same origin receives no former token or local state. Origin
discovery occurs over configured HTTPS with redirects disabled before current
credential access. The public instance UUID is a namespace marker, not
authentication; ordinary TLS/OAuth provides authentication. Version-10
origin-keyed secrets are deleted without being read/sent/rekeyed, and cleanup
failure disables network auth until retry/fresh sign-in. This work is not yet implemented;
Gate 0 is reopened for review, and prior Gate 1 results are superseded history.
See `../../docs/decisions/console-profile-access-and-offline-grants.md` and
`../../docs/console-profile-home-library-plan.md`.

## Native Target Prerequisites

`mise install` provides Flutter and, on macOS, Ruby/CocoaPods. Native builds
still need the platform SDKs:

- macOS/iOS builds require a full Xcode installation and an active
  `xcode-select` developer directory.
- Android builds require Android Studio or an Android SDK configured through
  `flutter config --android-sdk`.
- Windows builds require Visual Studio or Visual Studio Build Tools with the
  Desktop development with C++ workload and the optional C++ ATL for x64/x86
  component. `flutter_secure_storage_windows` includes ATL headers.
- Linux builds require the normal Flutter desktop prerequisites for Linux.
- Linux managed RetroArch setup supports x86_64 and needs either `bsdtar`
  (`sudo apt install libarchive-tools` on Ubuntu/Debian) or 7-Zip to unpack the
  official stable bundle. ROMD launches the resulting AppImage through its
  extract-and-run fallback, so FUSE is not required.

## Notes

- Flutter is pinned in `.mise.toml`.
- Native platform runners are checked in for macOS, Windows, Linux, and Android.
- `mise run bootstrap` can repair/regenerate runner files with `--no-overwrite`.
- `mise run run:windows` runs the Windows desktop client against the local
  consumer API.
- `mise run run:linux` and `mise run build:linux` must run on a Linux host.
- Keep the UI controller-first: focus states, directional navigation, and launch
  actions should work without a pointer.
- Keep generated API clients isolated behind hand-written services when we add
  them.
