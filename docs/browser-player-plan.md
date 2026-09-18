# Browser Player Plan: Dedicated Origin, Configurable Asset Source

Status: Phase 1 implemented and human-validated in Chrome (2026-07-22) —
handshake, CDN core boot, ROMD-controlled shutdown with save persistence
across browser restart, and external save-state import/export all
confirmed. Firefox/Safari matrix and the remaining acceptance probes are
outstanding. Supersedes the fetch-and-stage asset architecture in
`docs/consumer-browser-playback-spike.md`.

## Goals

- Browser playback is a quick, low-ceremony way to play ROMD-hosted ROMs.
- The emulator runs isolated from ROMD origins: it can never read tokens,
  call authenticated APIs, or see signed content URLs.
- One declarative configuration axis selects where EmulatorJS assets come
  from: the official versioned CDN (default, zero asset staging) or a
  deterministic self-hosted bundle (offline/pinned, opt-in, Phase 2).
- The same player integrates into the consumer portal now and the admin
  portal later, unchanged.
- In-browser saves persist across sessions from day one, keyed by stable
  content identity.

## Non-goals (deferred)

- Threaded cores and pop-out mode (research follow-up; see Phase 3 — the
  previously proposed `window.open` Blob handoff is incompatible with
  `COOP: same-origin`, which severs cross-origin opener references).
- Server-side save sync (bridge events make it possible later).
- Admin-portal playback (requires admin-side manifest/grant plumbing).
- Disc/large/unknown/multi-file content (unchanged from the spike).

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
│    contentSha256, relativePath,│            │                                │
│    rom: Blob} ────────────────►│            │ saves: native EmulatorJS       │
│  ◄── hello{protocol, version,  │            │ IndexedDB on player origin,    │
│       source, cores}           │            │ storage identity derived from  │
│  ◄── ready/started/exit/error/ │            │ contentSha256                  │
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

## Runtime player-origin discovery (Phase 1)

Build-time discovery is not viable: the same consumer build is already
served from a public origin and a LAN origin (`compose.prod.yaml` —
`ROMD_CONSUMER_PUBLIC_URL` and `ROMD_CONSUMER_LAN_URL`). Discovery is a
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

`web/tools/emulatorjs/pin.json` slims to the declarative selection the
player build consumes:

```json
{ "schemaVersion": 2, "version": "4.2.3", "cores": ["fceumm", "snes9x", "gambatte", "mgba"] }
```

Operator-facing env on the `romd-player` service, rendered into
`/player-config.json` and nginx security headers by the container
entrypoint (fail-fast on invalid combinations):

```yaml
ROMD_PLAYER_EMULATORJS_SOURCE: cdn        # cdn | bundled (default cdn)
ROMD_PLAYER_EMULATORJS_DATA_PATH: ""      # optional mirror override
ROMD_PLAYER_CORES: ""                     # optional subset of pin cores
ROMD_PLAYER_ALLOWED_PARENTS: https://romd.example,http://romd.lan
```

Derivation rules, in order: explicit `DATA_PATH` override → `bundled`
local path → versioned CDN path (`https://cdn.emulatorjs.org/<ver>/data/`;
never `stable`/`latest` — upgrades stay intentional via the pin).

`player-config.json` is served with `Access-Control-Allow-Origin: *`
(public capability data only; embedding is controlled by
`frame-ancestors` and bridge origin validation). Portals fetch it to gate
Play buttons. This is **capability advertisement**, not proof of asset
reachability: in `cdn` mode a launch can still fail if the CDN is down,
and the bridge must surface that as a bounded loader timeout with a clear
asset-loading error (not a hang). In `bundled` mode the entrypoint
validates the requested cores against the staged stamp and never
advertises a core missing from the image.

## Bridge protocol (v1)

Protocol lives in a new tiny workspace package **`@romd/player-protocol`**
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
   `load { core, displayName, contentSha256, relativePath, rom: Blob }`.
   The player validates `core` against its own configured core list and
   rejects ROMs over the size ceiling.
4. Player creates its own blob URL, derives the **save/storage identity
   from `contentSha256`** (exact EmulatorJS mechanism pinned during
   implementation; `displayName` is presentation only), sets `EJS_*`
   globals, injects the loader `<script>` from its configured `dataPath`.
5. Player emits `ready`, `game-started`, `save-updated`, `exit`, `error`
   over the port. All events are schema-validated by the parent; the
   global `window.message` path is used only for the initial handshake.
6. Shutdown (see gate below): parent sends `shutdown`, waits for `exit`
   (bounded timeout), then removes the iframe.

## Phase 1 gate: proven save persistence and shutdown

EmulatorJS 4.2.3 has no configuration-level exit API (noted in the current
`browserPlayback.ts`); its bottom-bar exit button drives an internal exit
path that flushes saves. Phase 1 is not complete until ROMD-initiated
shutdown demonstrably flushes saves, using the first workable strategy of:

1. a supported EmulatorJS API, if one exists in the pinned version;
2. a pinned-version adapter that invokes the same internal exit routine
   the exit button uses (acceptable because the version is pinned);
3. periodic save flushing plus a best-effort final flush on `shutdown`.

Additionally: the current observational `EJS_onSaveState` /
`EJS_onLoadState` callbacks are removed unless the bridge fully implements
their behavior — in 4.2.3, registering them replaces the default
save-state/file-picker paths rather than observing them (verify against
the pinned source during implementation).

Acceptance: start a game, save in-game, stop **via ROMD controls** (not
the EmulatorJS exit button), restart the browser, relaunch — the save is
present. Repeat with two releases sharing a title but differing hashes —
saves do not collide.

## Phase 1 — scope

Contract freeze first: the `@romd/player-protocol` messages and the
runtime-config endpoint shape are frozen before implementation splits
across surfaces (player package, consumer bridge, backend endpoint,
deployment files are otherwise too interdependent).

1. **`web/packages/romd-player-app`** (vanilla TypeScript + Vite):
   `index.html`, `src/main.ts` (EJS wiring), `src/bridge.ts`,
   `src/config.ts`. Vitest: origin/init validation, message parsing
   rejection paths, config derivation (cdn/override), shutdown
   sequencing.
2. **`web/packages/romd-player-protocol`** (`@romd/player-protocol`):
   types + guards + limits; consumed by player and consumer app.
3. **Consumer host**: `GET /api/playback/config` + `PlayerOrigins`
   configuration binding + tests (origin matching incl. forwarded
   headers).
4. **Consumer app**: `services/playerBridge.ts` replaces the srcdoc flow;
   delete `buildEmulatorJsPlayerDocument` and vendor-path constants
   (platform mapping/preflight logic stays); playability gating from
   `/api/playback/config` + `player-config.json`
   (mappings ∩ advertised cores); same-origin refusal; CDN-failure launch
   error surfaced distinctly from "player unavailable".
5. **Deployment**: Docker player build stage + `romd-player` nginx target
   with config/header-rendering entrypoint; consumer frontend stage drops
   the EmulatorJS fetch layer and `libarchive-tools`; base/dev/prod
   compose additions (`ROMD_PLAYER_PUBLIC_URL`, `ROMD_PLAYER_LAN_URL`,
   allowed parents, playback config map); prod nginx/cloudflared route for
   the player subdomain.
6. **Removals** (git history preserves; Phase 2 reintroduces what it
   needs): `fetch.mjs` + its tests, the `ci.yml` fetch-tooling step, the
   `emulatorjs-pin.yml` workflow, mise `emulatorjs:fetch`/`update` tasks,
   web `emulatorjs:*` scripts, consumer `public/vendor` staging,
   `VITE_EMULATORJS_CORES` gating and its tests, `.gitignore`/
   `.dockerignore` vendor entries.
7. **docs**: update the spike doc; add the insecure-context
   `crypto.subtle` limitation (parent-side SHA-256 requires a secure
   context; plain-HTTP non-localhost LAN cannot verify → playback
   unavailable there) to `docs/known-issues.md` — it is not currently
   tracked anywhere.

## Phase 2 — `bundled` asset source (deterministic/offline, opt-in)

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

## Phase 3 — research and shared surfaces (later)

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

## Risks and open questions

- **CDN trust and availability**: versioned CDN paths are a convention,
  not content-addressed; compromise or outage affects `cdn` mode.
  Mitigations: CSP, the isolation boundary, day-one `DATA_PATH` mirror
  override, Phase 2 as the deterministic escape hatch, and explicit
  bounded-timeout launch errors. Accepted for v1.
- **Per-session privacy leak to the CDN** (IP + cores played) in `cdn`
  mode. Documented in `THIRD_PARTY_NOTICES.md`; `bundled` mode is the
  remedy.
- **EmulatorJS save-identity mechanism**: deriving storage identity from
  `contentSha256` needs a pinned-version verification of exactly which
  option keys storage (gate acceptance covers the observable behavior).
- **Saves migration**: existing spike saves live in the consumer origin
  and do not carry over. Accepted (spike stage).
- **Safari behaviors**: same-site placement avoids ITP partitioning, but
  the browser matrix below is the arbiter.

## Verification

This change touches web, deployment composition, and the auth/storage
boundary:

- `pnpm lint`, `pnpm test`, `pnpm build` from `web/`.
- `docker compose config` for base, development, and production overlays.
- Build and smoke-test the `romd-player` and `romd-consumer` targets.
- `mise run test:integration` (compose/host topology changed); account
  for the pre-existing failures documented in `docs/known-issues.md`.
- Browser matrix (Chrome, Firefox, Safari): parent-DOM/storage access from
  the frame fails; signed URL and tokens never observable frame-side;
  hash-verification failure blocks launch; all mapped cores boot from the
  numeric CDN path; gamepad, fullscreen, audio, pointer lock; save
  persistence across browser restart via ROMD-controlled shutdown;
  distinct save identities for same-title/different-hash releases; CDN
  failure produces the bounded launch error; public and LAN origins each
  resolve their configured player origin end to end.
- Header tests: CSP contents per mode, `frame-ancestors` enforcement,
  `Access-Control-Allow-Origin` on `player-config.json` only, embed
  refusal from non-allowlisted parents, bridge refusal of a same-origin
  player.
