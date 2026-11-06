# Browsing and playing

Use the **consumer portal** to browse the library assigned to your account.
Your administrator manages catalog sources, imports, ratings and library
publication in the separate admin portal. If you have no library or an empty
one, ask them to check the [first-library setup](first-library.md#if-the-library-looks-empty).

## Find a game

**Home** shows discovery shelves, including featured collections when the
administrator has attached them. **Browse** opens the searchable library.
Search games and use the available system, genre and availability filters to
narrow the results. Open a title to see its description, artwork and releases.

A **title** is the game; a **release** is a particular version, such as a region
or revision. Use the **Releases** tab when you want a specific version. A
catalog entry can exist without the files needed to play it, and availability
can differ between releases of the same title.

## Download

Choose **Download** on the title or on the release you want. The browser owns
the actual save, its progress and its download errors. Check your browser's
download list if a save does not appear.

Formats depend on the available stored representations. See
[downloads and exports](downloads-and-exports.md) for original files, RVZ/CHD
choices and administrator exports.

## Play in the browser

Check [playback compatibility](playback-compatibility.md) for the current systems,
file layouts and browser limits.

When the chosen release is supported, **Play Now** opens the browser player.
ROMD checks and prepares the game before showing **Start game**. Starting the
game loads its emulator; supported systems, required firmware, browser
capabilities and available files determine whether playback is offered.

Use the player's controls for pause, sound, fullscreen and save tools. Use the
back control to return to game details, and read any exit confirmation before
leaving. Save your progress before closing a session.

Browser saves and save states are separate for each account, server installation
and game-file identity. They live in the browser's storage for the player
origin. Another browser, device or player hostname has separate storage.
Export saves you want to keep before clearing site data or moving to another
browser. Server backups do not capture a household browser's local storage.

If this browser has saves from before account isolation, ROMD offers **Older
shared saves found** before starting the emulator. Those older saves have no
recorded owner. Download a backup, then choose **Copy older saves into my
account** only if they are yours, or **Start with separate saves**. Copying keeps
the originals. It does not replace progress already saved for your account.

To revisit recovery, choose **Recover older saves** on the player's pre-game
screen, before **Start game**. If your account already has progress, the older
files remain downloadable for manual import through the emulator's save tools.
If copying fails, keep the originals and use those downloads; do not clear site
data to retry. Each browser account can deliberately recover its own copy of
the former shared data.

If playback is unavailable, read the reason on the game page. A downloadable
game is not necessarily compatible with the browser player. Native emulator
setup, BIOS requirements and Ottercade's local saves are separate from browser
playback; see the [console README](../../clients/romd_console/README.md).

For operators and contributors: [browser-player architecture](../architecture/browser-player.md)
and [consumer delivery](../architecture/consumer-delivery.md).
