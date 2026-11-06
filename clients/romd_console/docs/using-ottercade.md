# Set up Ottercade

Ottercade is experimental and currently runs from source. Start with the
[development guide](development.md) to build it for your native platform.
You can create a local profile before connecting to a server. To browse and
install ROMD games, your server account needs an assigned, published library.

## 1. Choose a profile and connect

Create or select a local profile, then use its server connection action from
the launcher. Enter the **consumer** server origin, rather than the admin portal.
For local development, find the consumer API URL with `mise run dev:status` in
the server checkout. Ordinary container-mode development uses
`http://localhost:11338`; source mode uses `http://localhost:5002`.

Follow the sign-in/device-authorization prompts. Your local profile and the
server account are separate: the profile owns local progress, and the server
account controls access to the assigned library. If the library is empty, ask
the administrator to check [publication and assignment](../../../docs/features/first-library.md#if-the-library-looks-empty).

## 2. Set up a controller

Connect your controller before launching a game. Use the controller setup
workspace to map its controls and follow any prompts for an unrecognized device.
Keyboard navigation is also available. Revisit setup when a controller is
missing, mapped incorrectly or changed.

Controller recognition in Ottercade and input inside an emulator are separate
checks. Confirm that the emulator responds and that control returns to
Ottercade when you exit. Multi-controller seating and emulator support vary;
see [playback compatibility](../../../docs/features/playback-compatibility.md).

## 3. Install and prepare a game

Browse the assigned library, open a game and choose its installation action.
Wait for the installation to complete. Ottercade verifies the downloaded bytes
before adopting them as installed content.

On launch, follow the dependency prompts for the selected emulator, core and
BIOS. Managed downloads are available only for the registered platform/artifact
combination; some runtimes require a local executable. Supply your own lawfully
obtained firmware when required. ROMD distributes no BIOS or game files.

PCSX2 installations can use verified PS2 CHDs. GameCube/Wii format delivery does
not imply a Wii launch profile; the current Dolphin profile targets GameCube,
and console installs currently select original bytes for RVZ-backed games.

## 4. Play and keep your progress

Launch the installed game from the same local profile. Save progress in the
emulator, exit normally, and wait for Ottercade to regain control. Saves and
states are kept separately per profile, server and title, outside the evictable
installed-game files. Releases of the same title on one server share those roots.

Prepare and launch a game online before relying on offline use. Offline play
needs the installed content and runtime dependencies, plus any applicable
retained authorization for that profile and installation. Known revoked access
blocks play; cached offline authorization currently has no maximum age. Missing
content or dependencies need preparation before disconnecting. Back up the app's durable profile data
separately from server backups; see [local storage and access](architecture.md#persistence-acquisition-and-cleanup).

If launch fails, keep the displayed reason and check installation, dependencies,
server access and controller setup. Report the app/server revision, platform,
runtime and a sanitized reproduction without ROMs, BIOS files or credentials.
