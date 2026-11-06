# Where you can play

ROMD can store and deliver more systems than its browser player or Ottercade
can launch. A successful import establishes file ownership and verification;
the chosen client still needs a compatible runtime and any required firmware.

## Browser player

The current browser player maps these systems to its pinned EmulatorJS cores:

| System | Accepted original-file extensions | Core |
| --- | --- | --- |
| NES / Famicom | `.nes`, `.fds` | FCEUmm |
| SNES / Super Famicom | `.sfc`, `.smc`, `.fig`, `.swc` | Snes9x |
| Nintendo 64 | `.z64`, `.n64`, `.v64` | Mupen64Plus-Next |
| Game Boy / Game Boy Color | `.gb`, `.gbc` | Gambatte |
| Game Boy Advance | `.gba` | mGBA |
| Genesis / Mega Drive | `.md`, `.gen`, `.bin`, `.smd` | Genesis Plus GX |
| Master System | `.sms` | SMS Plus |
| Game Gear | `.gg` | Genesis Plus GX |

Playback requires a complete release with one supported ROM item, within the
browser's size limit (currently 64 MiB). The player must advertise its core,
and the browser must provide WebAssembly and secure-context SHA-256 support.
File extensions alone do not establish compatibility. Firmware-dependent games
are subject to the core's requirements; this flow does not automatically fetch
BIOS files from your server.

Chrome has recorded playback/save-persistence validation. Firefox and Safari
remain outside that completed matrix. The default player loads version-pinned
emulator assets from the EmulatorJS CDN, so first startup needs network access
to those assets. Operators can configure a compatible mirror; a packaged
bundled-runtime mode is not available yet.

Browser saves are scoped to the account, installation and game within the
browser/player origin. Older shared saves have an explicit recovery path.
Export important saves before clearing site data or changing devices. See
[browsing and playing](browsing-and-playing.md).

## Ottercade and external emulators

| Route | Current scope |
| --- | --- |
| Ottercade RetroArch profiles | NES, SNES, N64, Game Boy, Game Boy Color and Game Boy Advance, plus Genesis and PlayStation alternatives |
| Ottercade standalone profiles | PlayStation via DuckStation, PS2 via PCSX2, GameCube via Dolphin |
| Downloads and WebDAV | Use your own compatible emulator for the delivered original, RVZ or CHD format |

All native Ottercade builds and emulator/platform/controller combinations remain
experimental. A registered runtime profile is not a supported-platform claim.
Provisioning availability differs by OS and architecture; follow
[Ottercade setup](../../clients/romd_console/docs/using-ottercade.md) and its
dependency prompts. PSP compression does not add a PSP runtime profile.
Original-layout multi-disc installs do not establish disc-switching or gameplay
qualification.

For another emulator, choose a format it can read from
[downloads and exports](downloads-and-exports.md). Browser playback always
prepares original bytes independently of the download format you selected.
