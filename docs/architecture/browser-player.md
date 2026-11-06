# Browser player

The browser player is a quick, low-ceremony way to play ROMD-hosted ROMs in a
browser. EmulatorJS runs on a **dedicated player origin**, isolated from ROMD:
it can never read tokens, call authenticated APIs, or see signed content URLs.

Status: implemented and validated in Chrome (handshake, CDN core boot,
ROMD-controlled shutdown with save persistence across browser restart, and
save-state import/export). The Firefox/Safari matrix remains outstanding.

Design goals:

- One declarative setting selects where EmulatorJS assets come from: the
  official versioned CDN (default, zero asset staging) or, later, a
  deterministic self-hosted bundle.
- The same player can embed in the consumer portal now and the admin portal
  later, unchanged.
- In-browser saves persist across sessions, scoped to the authenticated account,
  server installation and stable content identity.

## Architecture

```
consumer portal (romd origin)                 romd-player (dedicated origin)
┌────────────────────────────────┐            ┌────────────────────────────────┐
│ manifest → signed grant →      │            │ player.html + bridge           │
│ download → size/SHA-256 verify │            │ reads /player-config.json      │
│ (downloadVerification.ts as-is)│            │                                │
│                                │  iframe    │ EJS_pathtodata =               │
│ playerBridge.ts ───────────────┼───────────►│  cdn:     https://cdn.emulator │
│  init(port, targetOrigin=      │  Message   │           js.org/<ver>/data/   │
│       playerOrigin)            │  Channel   │  bundled: /vendor/emulatorjs/  │
│  load{core, displayName,       │            │           <ver>/data/         │
│    contentSha256, saveId,     │            │                                │
│    rom: Blob} ────────────────►│            │ saves: native EmulatorJS       │
│  ◄── hello{protocol, version,  │            │ IndexedDB on player origin,    │
│       source, cores}           │            │ storage identity derived from  │
│  ◄── ready/started/exit/error/ │            │ account/instance/content saveId│
│       save-updated             │            │                                │
└────────────────────────────────┘            └────────────────────────────────┘
```

### Origin invariants

- **Cross-origin, same-site.** The player origin must differ from the
  portal origin (isolation) but share its registrable domain/site
  (e.g. `play.romd.example` under `romd.example`, or the same LAN host on
  a different port). Cross-*site* embedding gets partitioned — and in
  WebKit ephemeral — iframe storage, which would silently break save
  persistence. Same-site placement avoids state partitioning entirely
  while keeping origin-scoped isolation of tokens and storage.
- **The bridge refuses to launch when
  `new URL(playerOrigin).origin === window.location.origin`.** A
  same-origin player combined with `allow-scripts allow-same-origin`
  would eliminate the boundary. It also warns (telemetry/console) when
  the player host is not a suffix match of the portal site, as a
  best-effort cross-site misconfiguration signal.
- Iframe definition (fixed, not configurable):

  ```html
  <iframe
    sandbox="allow-scripts allow-same-origin allow-pointer-lock allow-downloads"
    allow="autoplay; gamepad; fullscreen; screen-wake-lock"
    referrerPolicy="no-referrer"
  />
  ```

  `allow-same-origin` keeps the *player's* origin (already not ROMD's) and
  is required for persistent saves. Do not remove it "to harden" — that
  recreates opaque-origin save loss.

### Trust boundaries

- Tokens and signed grant URLs never cross into the player. The parent
  redeems and SHA-256-verifies content exactly as today; only the verified
  `Blob` transfers (structured clone).
- Isolation comes from the cross-origin boundary; the sandbox attribute is
  defense in depth. A compromised CDN or player can at worst see the ROM
  bytes and fake player events — never ROMD credentials or APIs.
- The player document carries a CSP allowing `'self'`, the configured
  asset origin, and required `blob:` script/worker sources
  (`wasm-unsafe-eval` for WebAssembly and `unsafe-eval` for EmulatorJS
  4.2.3's Emscripten `cwrap` runtime, confined to the credential-free
  player origin); `connect-src` limited to
  `'self'`, the asset origin, and `blob:`/`data:` as empirically required.
  `frame-ancestors` restricted to the configured parent origins.

## Runtime player-origin discovery

The consumer discovers its player origin at runtime, so a released web build
can be configured without recompilation. Production Compose supplies one
canonical consumer/player pair through `ROMD_CONSUMER_PUBLIC_URL` and
`ROMD_PLAYER_PUBLIC_URL`. Additional origins need an operator overlay with
matching authentication, CORS and parent/player mappings. Discovery uses a
small anonymous consumer-host endpoint:

```
GET /api/playback/config        (consumer host, anonymous, no-store)
→ { "playerOrigin": "https://play.romd.example" }   // or null
```

Resolution: the request's origin (Host/forwarded headers, already
proxy-aware) is matched against a configured parent→player map, falling
back to a single default:

```
Romd__BrowserPlayback__PlayerOrigins__0__Parent: https://romd.example
Romd__BrowserPlayback__PlayerOrigins__0__Player: https://play.romd.example
Romd__BrowserPlayback__PlayerOrigins__1__Parent: http://romd.lan
Romd__BrowserPlayback__PlayerOrigins__1__Player: http://romd.lan:8091
Romd__BrowserPlayback__DefaultPlayerOrigin: ""        # optional
```

`null` (no match, no default) ⇒ portal shows playback as unavailable with
a clear reason; downloads unaffected. Dev: `appsettings.Development`
defaults to `http://localhost:5175`.

## Declarative player configuration

`web/tools/emulatorjs/pin.json` is the declarative selection the player
build consumes: a schema version, the pinned EmulatorJS version, and the
supported core list.

Operator-facing env on the `romd-player` service, rendered into
`/player-config.json` and nginx security headers by the container
entrypoint (fail-fast on invalid combinations):

```yaml
ROMD_PLAYER_EMULATORJS_SOURCE: cdn        # cdn (bundled is planned)
ROMD_PLAYER_EMULATORJS_DATA_PATH: ""      # optional mirror override
ROMD_PLAYER_CORES: ""                     # optional subset of pin cores
ROMD_PLAYER_ALLOWED_PARENTS: https://romd.example,http://romd.lan
```

Current derivation: explicit `DATA_PATH` override → versioned CDN path (`https://cdn.emulatorjs.org/<ver>/data/`;
never `stable`/`latest` — upgrades stay intentional via the pin).

`player-config.json` is served with `Access-Control-Allow-Origin: *`
(public capability data only; embedding is controlled by
`frame-ancestors` and bridge origin validation). Portals fetch it to gate
Play buttons. This is **capability advertisement**, not proof of asset
reachability: in `cdn` mode a launch can still fail if the CDN is down,
and the bridge must surface that as a bounded loader timeout with a clear
asset-loading error. The entrypoint currently accepts only `cdn`; `bundled`
is deferred. The advertised core subset must come from the image's pinned list.
A custom data path must actually host compatible assets; advertisement alone
cannot establish that they are present.

## Bridge protocol (v2)

The protocol lives in the workspace package **`@romd/player-protocol`**
(consumed by both the player and the portals) exporting:

- message types;
- runtime type guards/parsers for every inbound message on both sides;
- limits: ROM Blob ceiling (the existing 64 MiB `browserPlaybackMaxBytes`),
  string length caps;
- the allowed event-name set;
- the protocol-version check.

Type-only sharing is insufficient — both sides parse untrusted runtime
messages.

Sequence:

1. Parent loads `iframe src=<playerOrigin>/`; on iframe `load`, posts
   `init` with a transferred `MessagePort`, explicit
   `targetOrigin = playerOrigin`.
2. Player accepts **exactly one** `init`, only from `window.parent`, only
   when `event.origin` is in its configured parent allowlist; adopts the
   port; replies `hello { protocol, emulatorJs: { version, source }, cores }`.
3. Parent validates `hello`, then sends
   `load { core, displayName, contentSha256, saveId, recoverOlderSaves, relativePath, rom: Blob }`.
   The player validates `core` against its own configured core list and
   rejects ROMs over the size ceiling.
4. The parent derives `saveId` as SHA-256 of a domain-separated tuple of the
   manifest's server installation ID, authenticated account ID and verified
   content hash. Only the opaque digest crosses into the player. Missing save
   identity and version-1 peers fail closed. The player uses `romd-v2-<saveId>`
   for its game name and named ROM files; `displayName` is presentation only.
   Before loading emulator assets, it inspects old shared saves and offers
   explicit recovery when appropriate. Recovery-required/completed events suspend
   the parent's asset timeout while the user chooses. It then scopes the save
   database and loads the configured emulator assets from its own Blob URL.
5. Player emits `ready`, `game-started`, `save-updated`, `exit`, `error`
   over the port. All events are schema-validated by the parent; the
   global `window.message` path is used only for the initial handshake.
6. Shutdown (see below): parent sends `shutdown`, waits for `exit`
   (bounded timeout), then removes the iframe.

## Shutdown and saves

EmulatorJS 4.2.3 has no configuration-level exit API. The player uses a
pinned-version adapter (`romd-player-app/src/shutdown.ts`) that invokes the same
internal exit routine as the EmulatorJS exit button, so ROMD-initiated shutdown
flushes saves. The native routine immediately unmounts `/data/saves`, which
would let queued IDBFS persistence delete its stored files. The adapter retains
that mount for the frame lifetime and explicitly flushes after the native exit
handlers return. It acknowledges exit only after IndexedDB commits and the
native abort grace period elapses. Save errors and timeouts are failures, shown
by the consumer before navigation; they never acknowledge successful saving.
The adapter refuses to run against any other EmulatorJS version.
Do not register observational `EJS_onSaveState` / `EJS_onLoadState` callbacks:
in 4.2.3 they replace the default save-state paths rather than observe them.

Expected behavior: save in-game, stop via ROMD controls, restart the browser and
relaunch — the save is present. Two releases sharing a title but differing in
hash keep separate saves. Abrupt page closure or authentication replacement
cannot wait for the controlled shutdown acknowledgement; normal native
autosaving still applies.

### Account isolation and older shared saves

The pinned 4.2.3 IDBFS adapter redirects the frame's `/data/saves` database to
`romd-browser-saves-v2-<saveId>` for the entire frame lifetime, including late
native flushes. Account/game databases retain IDBFS's version-21 `FILE_DATA`
store and timestamp index. Separate backing databases prevent concurrent tabs'
whole-filesystem synchronization from overwriting another account's files.
Browser states remain in `EmulatorJS-states`, under account-scoped keys.
Review both storage mechanisms before changing the emulator pin.

Pre-v2 files and states have no recorded owner. The player offers download and
explicit copy before loading a core; it never automatically claims them for the
next account. Recovery retains originals and uses insert-only transactions in
the destination databases. Existing progress disables copying; a raced insert
aborts that database's transaction. A failure between database copies can leave
some copied data, so failure remains visible and all originals remain available
for download/manual import. Never clear browser storage as a recovery step.
The pre-game **Recover older saves** action reopens this path even after an
account has progress; normal launches with existing progress skip the prompt.

The consumer waits for its authenticated profile and keys the player session by
account. Switching accounts cancels pending preparation and discards old frame
callbacks. Authentication profile requests are generation-fenced so an earlier
response cannot restore a signed-out or replaced account.

## Validation

Changes here touch web, deployment composition, and the auth/storage boundary.
Run the web checks (`pnpm lint`, `pnpm test`, `pnpm build` from `web/`), validate
the Compose overlays, and smoke-test the `romd-player` and `romd-consumer`
targets. Browser checks: the frame cannot reach parent DOM or storage, signed
URLs and tokens are never visible frame-side, hash-verification failure blocks
launch, saves survive ROMD-controlled shutdown, and a CDN failure produces a
bounded launch error.

## Future work

Not yet supported: threaded cores and pop-out mode, server-side save sync,
admin-portal playback, and disc/large/multi-file content.

### `bundled` asset source (deterministic/offline, opt-in)

Rebuild the pinned-release fetch tooling (SHA-verified, stamp-enforced,
exact core selection; the earlier implementation was superseded before it
was committed, so this is a reimplementation from this design, not a git
revert) staging into the player image at build
(`--build-arg EMULATORJS_ASSET_SOURCE=bundled`).
`ROMD_PLAYER_EMULATORJS_SOURCE` selects the mode at runtime; the
entrypoint fails fast if `bundled` is requested without staged assets and
derives advertised cores from the stamp. The path-scoped end-to-end pin
CI workflow returns with it. Portals need no changes.

Acceptance: airgapped compose deployment plays all staged cores with zero
outbound requests from the player origin.

### Research and shared surfaces

- **Admin portal embed**: same player + bridge; requires admin-side
  manifest/signed-grant endpoints and client plumbing (separate backend
  scope).
- **Threaded cores / pop-out**: open research. `COOP: same-origin`
  (required for cross-origin isolation) severs cross-origin opener
  references, so the opener-handle Blob handoff does not work. Candidate
  directions to evaluate: pre-persisting the ROM into player-origin
  storage during an embedded session before popping out; a one-time
  narrowly-scoped grant fetched by the player (weakens the no-bearer-URL
  property; needs design); `COEP: credentialless`. No mechanism is
  currently claimed.
- Optional server-side save sync via `save-updated` events.

## Known risks

- **CDN trust and availability**: versioned CDN paths are a convention,
  not content-addressed; compromise or outage affects `cdn` mode.
  Mitigations: CSP, the isolation boundary, day-one `DATA_PATH` mirror
  override, the planned `bundled` mode as the deterministic escape hatch, and explicit
  bounded-timeout launch errors. Accepted for v1.
- **Per-session privacy leak to the CDN** (IP + cores played) in `cdn`
  mode. Documented in `THIRD_PARTY_NOTICES.md`; `bundled` mode will be the
  remedy.
- **Pinned save adapters**: ROM filenames, browser-state keys and the IDBFS
  database redirect depend on the reviewed 4.2.3 runtime. The source browser
  regression covers isolation, recovery and restart; a pin change needs renewed
  storage review and browser qualification.
- **Safari behaviors**: same-site placement avoids ITP partitioning, but
  the browser matrix below is the arbiter.
