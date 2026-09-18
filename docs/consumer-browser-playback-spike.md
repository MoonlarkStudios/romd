# Consumer Browser Playback Spike

Status: superseded by the dedicated-origin Phase 1 implementation described in
`docs/browser-player-plan.md`.

## Retained findings

The spike proved that a small cartridge Release can use the existing consumer
manifest and anonymous signed content grant flow. The portal downloads the full
object, checks its byte count, verifies SHA-256, and only then makes the ROM
bytes available to the emulator. Large, disc, unknown, seek-heavy, and
multi-file content remains Download-only.

The supported cartridge mappings remain:

| Platform names | Extensions | EmulatorJS core |
| --- | --- | --- |
| Super Nintendo, SNES | `.sfc`, `.smc`, `.fig`, `.swc` | `snes9x` |
| Nintendo Entertainment System, NES, Famicom | `.nes`, `.fds` | `fceumm` |
| Game Boy, Game Boy Color | `.gb`, `.gbc` | `gambatte` |
| Game Boy Advance | `.gba` | `mgba` |

The browser playback ceiling remains 64 MiB. The portal still requests a fresh
manifest for each attempt, redeems the grant without credentials, and rejects
byte-count or digest mismatches.

## Product architecture

The same-origin `srcDoc` experiment and consumer-app vendor staging have been
removed. The product route now discovers a dedicated player origin through
anonymous `GET /api/playback/config`, then fetches that origin's public
`/player-config.json` capability document. Playability is the intersection of
ROMD's platform mapping and the cores advertised by the player.

The player iframe is always cross-origin and uses a fixed sandbox, permissions
policy, and no-referrer policy. A `MessageChannel` carries the versioned player
protocol. The parent sends only the selected core, display name, verified
content hash, relative path, and verified `Blob`; bearer tokens and signed
grant URLs never cross the boundary. The bridge validates every inbound
message, bounds frame/handshake/asset-loading time, and waits a bounded period
for the player's `exit` event during ROMD-initiated shutdown.

EmulatorJS asset selection is declarative. Phase 1 defaults to the numeric
versioned official CDN path (or an operator-supplied mirror) derived from
`web/tools/emulatorjs/pin.json`. The repository no longer downloads or stages
EmulatorJS during the consumer build. Deterministic bundled assets are deferred
to Phase 2.

## Operational constraints

- The player origin must differ from the portal origin. Same-origin
  configurations are refused.
- The origins should remain same-site so player-origin saves are not silently
  partitioned; a likely cross-site configuration emits a warning.
- SHA-256 verification requires `crypto.subtle`. Plain-HTTP non-localhost LAN
  origins generally lack it, so browser playback is unavailable there while
  normal downloads remain available.
- CDN capability advertisement does not prove asset reachability. Loader and
  CDN failures are reported as a distinct bounded launch error.
- Saves created by the earlier same-origin spike do not migrate to the
  dedicated player origin.

No known playable cartridge fixture is committed. Browser acceptance still
requires owned ROM content and the Chrome, Firefox, and Safari matrix in
`docs/browser-player-plan.md`, including ROMD-controlled shutdown/save
persistence and distinct save identity for same-title Releases with different
content hashes.
