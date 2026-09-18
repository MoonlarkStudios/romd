# Console Controller Identity Roadmap

Status: active execution roadmap; supersedes the planned continuation of the
controller-management roadmap after its landed Slices 1-6

Created: 2026-07-11

Related:

- [Console Controller Management Roadmap](console-controller-management-roadmap.md)
  (authoritative for landed Slices 1-6 and their contracts)
- [Console Virtual Controller Routing Roadmap](console-virtual-controller-routing-roadmap.md)
  (parked 2026-07-11; historical routing evidence)
- [Runtime Controller Adapter Policies](runtime-controller-policies/README.md)
- [macOS playerIndex propagation research](virtual-controller-routing/macos-playerindex-propagation.md)
- [macOS isolation research](virtual-controller-routing/macos-isolation-25F84.md)

## The Pivot

Deterministic per-player port routing is out of scope for ROMD. The complete
evidence chain closed every mechanism: cross-process ordinals are not identity
(DuckStation `SDL-N`, RetroArch MFi slots), `GCController.playerIndex` is
per-process, IOHID seizure isolates against neither macOS joypad envelope, an
in-process libretro embed conflicts with multi-emulator support, and the Linux
kiosk must run stock input configuration.

The reframed product promise:

> ROMD manages who is playing and how their controllers behave. The emulator
> assigns ports, exactly as it does on every desktop.

This is the industry-standard frontend position. What ROMD keeps is
everything identity- and configuration-shaped: exact SDL GUID+serial identity,
session seating UX, local profiles, mapping persistence, capability filtering,
fail-closed writers, and honest copy. What ROMD stops pretending to control is
which physical pad the emulator calls player 1 in multi-pad sessions.

Owner decisions recorded 2026-07-11:

1. The routing roadmap is parked; no macOS or Linux routing work is scheduled.
2. The Change Order ceremony is kept and reframed as session profile
   assignment: claiming a seat attaches identity (who is playing), not an
   emulator port.

## Carried-Forward Contracts

All non-negotiable contracts from the management roadmap remain in force
except where explicitly amended here:

- Player seating stays ephemeral; no durable seating rows.
- Exact seating identity stays `sdlGuid + serial`; mapping persistence stays
  `localProfileId + sdlGuid`; no invented identity on fallback paths.
- Passive enumeration creates no rows.
- Change Order never changes the active profile or library.
- Returning to attract clears session seating.
- Writers stay fail-closed: forced runtime references require a named adapter
  policy backed by evidence.
- Runtime profile ids never rename; commands stay structured with
  `runInShell: false`; save/state/config stay under ROMD-owned roots.

Amended meanings:

- A player seat now primarily answers "whose profile is on this pad," not
  "which emulator port this pad will get."
- "P1 alone owns shortcuts" is retained only where a physical shortcut owner
  is provable (see Slice 2); multi-pad shortcut policy is an open decision for
  Slice 6.

## Sequenced Roadmap

### Slice 1 - Close-Out And Honest Copy Audit

Status: docs landed in this milestone; copy audit pending

Purpose: make every surface truthful under the pivot before building on it.

Scope:

- park the routing roadmap and cross-link the three controller documents
  (landed with this roadmap);
- audit Players, Change Order, and pre-launch review copy for any residual
  implication that ROMD sets in-game player order; management-roadmap Slice 1
  removed most of this — verify and close the remainder;
- keep the honest uncorrelated messaging exactly as shipped;
- no schema, writer, or behavior changes.

Acceptance criteria:

- No normal-flow copy promises emulator port order.
- `mise run analyze` and `mise run test` pass from `clients/romd_console`.

### Slice 2 - Single-Controller Exact Correlation

Status: implemented and owner hardware accepted 2026-07-11 for DuckStation and
the calibrated RetroArch 8BitDo Pro 2 USB envelope

Implementation evidence: this change.

Purpose: un-suppress the dominant case. With exactly one connected controller,
the emulator's port 1 device is that controller by construction — no
cross-process identity is required.

Evidence basis: in every 2026-07-10/11 hardware trial (MFi, iohidmanager, and
sdl2 envelopes), the single connected 8BitDo Pro 2 received index 0 / port 1.

Scope:

- when the revision-checked launch snapshot contains exactly one connected
  controller with exact SDL GUID+serial identity, classify its runtime reference
  `verified` under `retroarch-macos-single-controller` or
  `duckstation-macos-single-controller` as appropriate;
- writers may then project that controller to port 1 and emit its mapping binds;
- DuckStation may emit its symbolic SDL shortcut chords. RetroArch may emit the
  twice-reproduced physical MFi shortcut values only under
  `retroarch-macos-single-controller-mfi-8bitdo-pro2-usb`; other RetroArch
  singleton controllers remain on the port-only policy with shortcuts nulled;
- exactly-one means one connected physical controller in the snapshot;
  offline exact reservations do not block the classification because a hole
  cannot manifest physically;
- multi-controller snapshots remain `uncorrelated` and keep today's behavior;
- document the mid-session hotplug caveat: a second pad connecting after
  launch is outside the snapshot's promise and follows emulator behavior.

Resolved decisions in this change:

- reuse `verified`; the named policy id already records the single-controller
  proof in structured diagnostics, so a second correlation enum label would add
  no persisted or user-facing distinction;
- restrict the policy to the SDL provider with exact GUID+serial identity.
  GUID-only and fallback/no-SDL single-pad support remain explicit follow-ups;
- enable DuckStation's symbolic `SDL-0` shortcuts, whose binding vocabulary is
  defined by the pinned source;
- add a device/envelope-specific RetroArch shortcut policy only after two clean
  isolated MFi calibration runs produced byte-identical configs. Unknown
  RetroArch controller identities remain suppressed.

Acceptance criteria:

- Focused writer tests cover single-pad verified emission, multi-pad
  suppression parity, reserved-hole non-blocking, and no-SDL fallback.
- Owner hardware confirms the managed RetroArch and DuckStation artifacts put
  the lone 8BitDo Pro 2 USB pad on port 1 and apply the generated controls.
- Owner hardware confirms DuckStation shortcuts land on that pad. RetroArch
  shortcut acceptance requires the calibrated 8BitDo policy's separate live
  ROMD check; calibration/config-byte evidence alone is not runtime acceptance.

Primary risk: accidentally widening the policy beyond snapshot-time
single-controller. The classification must derive from the reviewed snapshot,
not live device count at write time.

Automated evidence for this change: `mise run analyze` completed with no issues;
the final focused RetroArch contract/provider/writer rerun passed 76 tests, and
the console suite passed 561 tests. These signals prove classification and
config bytes, not live runtime behavior. DuckStation functional hardware acceptance
completed 2026-07-11: with only the 8BitDo Pro 2 connected over USB, the owner
confirmed port 1 plus menu, save/load, next/previous slot, and screenshot
shortcuts. RetroArch calibration completed twice from clean isolated configs:
the saved files were byte-identical and produced modifier `2`, menu `3`, save
`11`, load `10`, slot next/previous `7`/`6`, and screenshot `1`. The subsequent
live ROMD run with only that USB pad confirmed port-1 gameplay plus menu,
save/load, next/previous slot, and screenshot shortcuts.

The snapshot promise ends at process start. Connecting a second controller
mid-session does not reclassify or rewrite the launch; subsequent device and port
behavior belongs to the emulator. Hardware acceptance therefore begins with one
pad and excludes hotplug during the run.

### Slice 3 - Session Profile Assignment

Status: complete; explicit join, identity, and available-pad contract automated
2026-07-11; owner couch/hardware acceptance confirmed 2026-07-12

Implementation evidence: `e49471f` (`feat(console): add explicit controller
joining`).

Purpose: make Players an honest account of who has joined the couch session.
Connection establishes availability, an explicit L+R gesture joins a player,
and the joining controller may attach a local profile for the session.

Scope:

- P1 remains bound to the active profile chosen at start; unchanged;
- passive connection makes a controller available but does not express player
  intent or assign a P1-P4 seat;
- L+R on an available controller is the explicit join gesture while ROMD owns
  the foreground; call this joining, never registration;
- a joining guest (P2-P4) may optionally attach another local profile through
  a conditional `Who's using this controller?` surface; guests without an
  assignment remain anonymous session guests;
- assignment is session-only state on the existing in-memory claims; no new
  durable tables and no controller-to-profile affinity rows;
- per-player mapping resolution consumes the assigned profile's
  `localProfileId + sdlGuid` rows when present, falling back to the active
  profile's rows as today;
- Players, Change Order, and pre-launch review display the same joined roster
  and profile names; available controllers appear outside the P1-P4 roster;
- the active library, parental scope, and content visibility remain governed
  by the active profile alone; guest assignment never changes them.

Acceptance criteria:

- Passive connection creates no seat, profile assignment, mapping row, or other
  durable intent.
- L+R from one available controller creates one pending join and never fires
  while an emulator owns the foreground.
- Cancel leaves the controller available; accept claims the next open ROMD
  player seat. Simultaneous joins are serialized rather than stacked.
- If P1 is open (for example after keyboard entry), the first joined controller
  becomes P1 and inherits the active profile without a guest chooser. Otherwise
  the join targets the next open P2-P4 seat.
- With one local profile, an additional controller joins directly as Guest.
  With multiple profiles, the chooser offers Guest first plus eligible local
  profiles; choosing a profile does not switch the active library.
- Anonymous Guest may occupy multiple seats. A named local profile may occupy
  only one joined seat and is excluded or visibly disabled once assigned.
- Assignment survives reconnect/provider-id replacement exactly like claims.
- Attract reset clears assignments with seating.
- Mapping resolution tests cover assigned-profile, fallback, and mixed seats.
- No durable writes from assignment; schema version unchanged.
- Players, both Change Order entry paths, and pre-launch review agree on joined,
  available, waiting, and ambiguous state.
- Available and identity-attention controllers never contribute
  `LaunchPlayerController` entries, but remain in the complete reviewed
  inventory so hotplug/reorder invalidation and single-controller cardinality
  policies stay conservative.
- Available controllers use a quiet outlined controller icon plus text and no
  player number or profile avatar. The Home cluster renders one generic icon
  per recognized connected controller and never substitutes a `+N` summary.

Primary risks: confusing physical connection with human intent; allowing a
different controller to answer an identity prompt; modal stacking during
simultaneous joins; and accidentally making guest identity look like account,
library, parental, or emulator-port authorization.

Resolved design:

- `ControllerSlotClaim.localProfileId` carries the optional assignment in the
  existing in-memory session store. P1 strips and ignores that field, so the
  active profile remains authoritative;
- connection, seating, identity, and durable controls are distinct states:
  connected controllers are canonically classified as joined, joinable
  available, or connected-but-needing identity attention; L+R creates an
  ephemeral player claim only from the joinable class; the optional profile id
  identifies who is playing; explicit mapping-editor interaction owns durable
  `localProfileId + sdlGuid` controls;
- the starter controller's existing attract L+R flow remains special: it
  becomes P1 only after the active profile is selected;
- profile selection reached by keyboard may leave P1 open. In that case the
  first available controller to join becomes P1 and uses the active profile;
- for P2-P4, L+R is the primary join interaction. When multiple local profiles
  exist, show `Who's using this controller?` with Anonymous Guest focused first,
  profile avatars/names, A to select, and B to cancel. With no alternate local
  profile, join directly as Guest;
- the identity chooser is controller-first, identifies the joining controller
  without technical device jargon, and accepts controller navigation only from
  that controller. Keyboard and pointer input remain available; unrelated
  controllers cannot choose, cancel, or open another surface behind it;
- the chooser does not request a profile PIN because assignment grants no
  library, parental, server, or account access. It must say that the choice
  lasts for this session, affects identity/controls, and does not change the
  current library;
- Players retains `Assign guest profiles` as a secondary inspection/correction
  action, not the primary join path. Change Order remains bulk repair and
  reordering rather than the normal way to add a player;
- Change Order commits only controllers that complete its L+R ceremony. Empty
  Ready clears joined claims and returns to an empty roster, never passive
  automatic connection order;
- available controllers are visible below the joined roster with a quiet
  outlined generic gamepad icon. Slice 3 does not invent controller-family
  recognition; the catalog remains backlog work.
  Iconography is always paired with `Available` or one shared `Hold L + R to
  join` instruction; icon alone never communicates state;
- fallback-ambiguity quarantine remains non-joinable and is exposed read-only
  as `attentionControllers` by the canonical slot resolution and Players
  projection. Home renders these connected pads with an outlined warning
  treatment, a non-color `!` badge, and truthful semantics; Settings includes
  their duplicate-safe names plus the non-color `Check identity` cue; the
  Players join teaching row excludes them;
- canonical correlation resolves every exact claim globally, then live
  provider claims, before evaluating weak display-name fallback ambiguity.
  Strong ownership therefore cannot be preempted by an earlier player slot;
  exact offline claims still reserve their original slots. Joined, joinable
  available, and attention collections are pairwise disjoint and classify
  every connected controller exactly once;
- when no controller is connected or joined, the Home entry shows a neutral
  group/Players affordance rather than an empty clickable region. This icon is
  an entry-point affordance, not a claim that hardware is present;
- profile names appear on Players seats and beside previously assigned guest
  controllers when they are reseated in Change Order, regardless of whether
  Change Order was opened from Players or Settings;
- the reviewed launch snapshot carries the claim values that produced its slot
  resolution. Launch mapping therefore cannot read a newer live assignment;
- assigned-profile rules overlay active-profile rules per action. Missing guest
  rules inherit the active profile's existing mapping, while controllers
  without an SDL GUID remain on the legacy path;
- Change Order preserves an assignment when the same exact controller, or the
  same live provider id, is reseated. Returning to attract clears the claim and
  assignment together.

Foundation evidence before the explicit-join refinement: focused session/
projection/mapping/provider/UI suites passed 117 tests; `mise run analyze`
completed with no issues and the complete console suite passed 568 tests. The
two-test schema migration guard passed on the scoped rerun after the documented
SDK-cache sandbox denial. No Drift declarations or generated files changed,
and schema version remains `9`. This evidence does not accept the newly approved
available-controller, L+R join, identity chooser, or multi-entry-path UX.

Explicit-join automated evidence: `mise run analyze` completed with no issues;
the focused assignment/projection, Players, Controllers Settings, and
profile-selection run passed 105 tests; and `mise run test` passed all 592
console tests. Coverage includes explicit claim-only projection, immutable
available inventory, canonical ambiguity attention without weakened
quarantine, global exact/provider precedence, exhaustive disjoint connected
classification, atomic join rejection, L+R recognition and queuing,
controller-attributed navigation and repeat cancellation, direct P1 join,
guest assignment, disconnect versus inventory-failure handling, both Change
Order paths, snapshot cardinality, provider emission, zero-to-four literal
Home roster geometry, quiet joinable-only teaching, flat diagnostic inventory,
single-announcement semantics, reduced-motion traversal, and measured 2x text
containment at 1280x720. Attention icon composition, warning color, badge, and
semantics are asserted independently. On 2026-07-12 the owner confirmed the
remaining two-controller and couch-distance hardware acceptance gate, closing
Slice 3.

Owner visual acceptance decisions (2026-07-11):

- the Home Players cluster is a literal physical-controller roster, not a seat
  counter: render one generic icon per recognized connected controller, using a
  solid mint treatment for joined pads and an outlined neutral-gray treatment
  for available pads. Connected pads quarantined by identity ambiguity use an
  outlined warning treatment. Do not render P1/P2 number badges or a `+N`
  summary;
- icon shape plus color and semantics distinguish joined, joinable available,
  and identity-attention hardware. Do not infer controller family from names,
  and do not render offline reservations as physical controller icons;
- in Players, available hardware is a quiet inline teaching row beneath P1-P4,
  not a second bordered panel with nested name pills. Keep `Hold L + R to join`
  discoverable without repeating device inventory already visible in the
  header. Settings > Controllers may retain compact duplicate-safe device names
  because diagnosis belongs there, but uses a flat row/wrap rather than a heavy
  nested card;
- the profile-selection carousel reduces avatar and tile scale so roughly seven
  profiles remain legible at 1280x720. Focus uses the existing ring/glow with a
  restrained scale change; remove the floating gamepad caret because it reads as
  controller ownership rather than focus;
- profile selection presents identity first: display name plus one quiet
  `Linked` or `Local` state. Email and full account detail stay in profile
  settings; concise semantics retain linked username plus server context, or
  identify a local profile, without competing visually with the task;
- profile selection and the Players surface remain centered, overflow-free, and
  controller traversable at 1280x720 and enlarged text. The owner confirmed
  visual hardware acceptance on 2026-07-12.

### Slice 4 - Mapping Editor

Status: canonical foundation implemented with automated evidence; the corrected
`Use detected map` intent, RetroArch N64 face-position path, and paired-stick
editor/runtime path were owner hardware accepted on 2026-07-13; remaining SDL
edge cases, multi-controller safety, and listed UX follow-ups remain pending

Purpose: configure a physical controller once, then let ROMD translate that
setup into every supported emulator's native gameplay controls. This replaces
the rejected shortcut-centric editor implemented in the uncommitted working
tree on 2026-07-12.

Approved product contract (2026-07-12 pivot):

- Settings > Controllers opens one controller-detail surface centered on a
  neutral ROMD canonical pad. `Test` and `Map` are explicit modes over the same
  diagram: Test visualizes target-only live input without writes; Map edits a
  local physical-input draft. The canonical pad covers D-pad, four positional
  face buttons, shoulders, triggers, both sticks and stick presses,
  Select/Back, Start/Menu, and optional Guide/Home.
- Physical setup is device-global hardware truth keyed by SDL platform and
  controller-mode GUID. It is not local-profile state. Duplicate connected
  units sharing the key share one setup; serial/provider id only attributes the
  physical unit during live capture.
- Known SDL gamepads seed a testable detected setup without writing. Unknown
  SDL joysticks remain visible as `Needs setup` and can use a short guided
  bootstrap inside the same diagram when trustworthy controller navigation is
  not available.
  Fallback/no-SDL devices remain usable but cannot save a hardware setup.
- One shared SDL input pump owns joystick/gamepad enumeration and the
  process-global event queue. It fans out existing normalized navigation plus
  typed raw button/axis/hat/device events. A capture lease suppresses normal
  gamepad navigation and join actions while only the selected controller may
  answer a prompt.
- Map mode uses an in-memory draft with explicit per-control listening,
  neutral/release gates, dead-zone and stability checks for axes, trigger
  endpoint handling, conflict prevention, Clear, Retry, Cancel, draft testing,
  and explicit Save. Merely testing, navigating, or pressing an input never
  changes a binding. Opening, selection, navigation, cancellation, disconnect,
  and focus loss perform zero writes.
- `Use detected map` is an explicit save operation, not a return to passive
  fallback. When SDL supplies a detected map, activating it atomically persists
  that exact platform/GUID mapping so runtime adapters can distinguish explicit
  intent from passive detection. If no detected map exists, an explicit removal
  remains available; detection failure or screen opening alone never deletes a
  durable row.
- Schema `10` adds a device-global `controller_hardware_mappings` table keyed
  by `(sdlPlatform, sdlGuid)`. A validated versioned SDL mapping codec stores
  raw inputs losslessly and serializes deterministically. Existing schema-9
  label and shortcut rows are preserved and are not reinterpreted or migrated.
- `Button labels` remains presentation-only. Profile/GUID shortcut rules remain
  an optional secondary behavioral layer. Neither defines the physical
  controller setup.
- Runtime adapters translate the canonical gameplay pad into native gameplay
  configuration. DuckStation is the first approved full-gameplay target under
  the existing verified single-controller envelope. Multi-controller mapping
  remains automatic/uncorrelated and unchanged.
- Current macOS RetroArch 1.22.2 Metal uses MFi rather than ROMD's SDL provider.
  The seven shortcut calibration values are not a complete gameplay crosswalk.
  RetroArch custom gameplay emission remains fail-closed until every control is
  calibrated twice and accepted on hardware; ROMD must say this plainly rather
  than pretending current autoconfig is GUID-keyed.
- Per-system and per-game canonical-to-emulated-control overrides are future
  overlays. Slice 4 first establishes physical-to-canonical mapping and proven
  runtime adapters. Deterministic emulator player-port routing remains out of
  scope.

Acceptance criteria:

- Raw joystick enumeration includes known gamepads and unknown joysticks;
  typed button/axis/hat events retain the same provider identity as inventory.
- Target attribution, capture ownership, neutral/debounce/release behavior,
  axis inversion, trigger ranges, hats, conflicts, skipping, reconnect, and
  duplicate-model behavior are automated.
- Passive UI paths write nothing. Explicit Save atomically replaces one
  platform/GUID mapping. `Use detected map` atomically persists the detected
  platform/GUID mapping; only an explicit removal when no detected mapping is
  available may delete the durable row. Malformed or future-format rows fail
  closed and are never repaired or deleted passively.
- Fresh schema 10 and 9→10 migration tests pass with all existing schema-9 rows
  preserved. Drift output is regenerated through the project workflow and the
  derived diff is reviewed.
- Verified single-controller DuckStation config bytes cover all canonical
  gameplay controls, swaps, axes/triggers, omissions, hotkey separation, and
  idempotent rewrite. Saved mapping reaches emitted bytes end to end.
- RetroArch and all multi-controller unsupported envelopes retain automatic
  gameplay and existing shortcut suppression byte-for-byte.
- The unified Test/Map surface is controller/keyboard traversable, focus-safe,
  semantically concise, non-color-dependent, and overflow-free at 1280x720
  with 2x text and reduced motion.
- `mise run analyze`, focused tests, `mise run test`, runtime negative searches,
  and `git diff --check` pass. Live DuckStation hardware acceptance is required;
  RetroArch support remains pending its separately evidenced full calibration.

Runtime decisions:

- `artifact resolver: no - canonical controller mapping uses the existing SDL identity/input dependency and existing resolved emulator artifacts; it adds no executable, core, BIOS, artifact, or provisioning requirement.`
- `config writer: yes - ROMD must translate the canonical controller map into supported emulator-native gameplay and hotkey configuration.`

Runtime decisions for the combined detected-map and face-position correction:

- `artifact resolver: no - the detected-map intent and face-position correction use the existing runtime artifact and add no provisioning requirement.`
- `config writer: yes - the fail-closed RetroArch writer corrects its four face-button SDL-to-MFi translations so canonical positions reach the intended physical buttons.`

Current implementation boundary (2026-07-13; do not read as completed
acceptance evidence):

- duplicate units that share one platform/GUID setup are grouped honestly, but
  hold-to-select attribution is not implemented; setup asks the owner to
  disconnect all but one identical unit before capture;
- the sequential wizard and separate normalized event log have been replaced
  by one neutral, code-native controller diagram with explicit Overview, Test,
  and Map states. Test is exact-target and write-free; Map changes a selected
  canonical control only after explicit `Change` listening. An empty map keeps
  a forward/skip guided bootstrap on the same diagram;
- identity-less raw controllers can open the exact-target Test state but cannot
  Map or write. Duplicate platform/GUID groups remain non-openable until one
  physical unit can be attributed honestly;
- axis capture uses the first observed endpoint as its baseline and a later
  dead-zone-crossing excursion for direction/inversion. Multi-sample stability
  and debounce calibration are not implemented yet;
- paused reconnect/resume and live hardware/runtime verification remain
  unimplemented; the Test/Map diagram must not be treated as hardware
  acceptance;
- Test mode shows mapped active controls plus raw unmapped button/hat/axis
  activity. Continuous analog stick pucks, trigger-fill graphs, family-specific
  controller shells, and rumble diagnostics are intentionally deferred;
- automated tests cover target-only Test and Change capture, controller-only
  Test exit, direct and guided mapping, draft testing, conflicts, zero-write
  paths, reset, focus, dirty dismissal, layout, and session-join suppression.
  They do not constitute live hardware calibration or DuckStation runtime
  acceptance.

Automated evidence (2026-07-13, uncommitted working tree):

- `mise run analyze` completed with no issues.
- The primary focused schema/codec/repository, SDL raw-input, capture ownership,
  controller-setup, session-safety, launch-provider, DuckStation writer, and
  app-routing run passed 183 tests.
- `mise run test` passed all 634 console tests (Slice 3 baseline: 592).
- `git diff --check` passed.
- Schema version is `10`; the 9->10 migration preserves schema-9 data and adds
  only device-global controller hardware mappings. Drift output was regenerated
  through build runner and reviewed as derived output.
- Runtime negative searches found only existing structured `Process.start` /
  `Process.run` implementations and comments, with no shell-string launch. No
  durable save/state/config path is routed under `contentRoot`.
- Independent review initially rejected the unified tree because direct
  `Change` listening lacked a controller-only cancel gesture and diagonal hat
  values could overlap cardinal mappings. Target-only long-hold Cancel,
  cardinal-only hat persistence/capture, overlap-safe validation, recovery
  copy, and regressions resolved both findings. Independent re-review then
  reported no blocking findings and passed the final tree.
- DuckStation config-byte tests prove the saved SDL mapping reaches the managed
  `gamecontrollerdb.txt` only under the verified managed macOS singleton
  policy. RetroArch gameplay and multi-controller custom emission remain
  suppressed and unchanged.
- The owner used Reset and confirmed the detected map's positional truth in the
  UI: printed X was North, Y was West, B was South, and A was East. The then-
  current `Use detected map` flow incorrectly deleted the durable hardware-map
  row. A deeper audit also found that the writer interpreted SDL's legacy
  label-hint indices as positions and reversed both face pairs. The combined
  correction now persists the detected map atomically and translates raw
  `b0/b1/b2/b3` to MFi `0/8/1/9`. The owner then saved the detected map and
  accepted a live *The World Is Not Enough* launch: printed B/South produced
  N64 A, printed Y/West produced N64 B, printed X/North no longer produced B,
  left-stick movement remained correct, and the right stick still drove the C
  cluster. The persisted row retained the exact detected positional map and the
  generated config emitted South/East/West/North as MFi `8/0/9/1`.
- The owner then performed a fresh corrected-build South/East swap and accepted
  it in *Super Mario World*. The durable row changed only the two intended raw
  positions (`a:b0,b:b1`), and the generated config replaced the prior face
  binds with RetroPad South -> MFi `0` and East -> MFi `8`; the prior actions no
  longer remained layered underneath. This supersedes the earlier swap result
  that had accidentally compensated for the old reversed crosswalk.
- The owner explicitly restored the detected map. The primary verified the
  exact `a:b1,b:b0,x:b3,y:b2` row before and after terminating and relaunching
  ROMD, closing the durable process-restart persistence check while leaving the
  controller in its normal positional layout.
- Run C deliberately swapped the physical sticks and proved that the saved
  canonical axes reached RetroArch: the right stick became N64 movement and the
  left stick became the C-button cluster. It also exposed an editor defect:
  selecting `LX`/`LY`/`RX`/`RY` independently let the first arbitrary gesture
  choose axis polarity, so both vertical axes were saved inverted. The approved
  correction presents one `Left stick` and one `Right stick`; Change captures
  one continuous right-held-then-down gesture from the target controller,
  derives both axes and polarity, and applies the pair to the local draft
  atomically. Complete detected SDL stick pairs additionally require the exact
  companion vertical axis; partial/unknown maps use the continuous hold as the
  honest pairing signal. Cancel, disconnect, conflict, release, or partial
  capture changes neither axis. Existing persisted X/Y bindings remain
  compatible, with no schema or config-writer format changes.
- Focused correction evidence passed 39 RetroArch writer/integration tests and
  20 controller-UI tests. The combined controller-UI/provider/launch/writer/
  integration matrix passed 104 tests, the full console suite passed 661 tests,
  and analysis, Dart format, runtime negative searches, and `git diff --check`
  were clean.
- Implementation commits: `789b9df` (`feat(console): add canonical controller
  mapping`), `e07c769` (`fix(console): harden emulator controller mapping`),
  `a39d245` (`feat(console): emit canonical RetroArch gameplay mapping`), and
  `a6eed6f` (`feat(console): add Genesis Plus GX controller policy`). The
  stick-level capture correction remains uncommitted. The accepted N64 and
  Genesis paths do not by themselves complete the remaining Slice 4 hardware
  matrix.

Runtime decisions for stick-level capture:

- `artifact resolver: no - stick-level capture uses the existing SDL input provider and resolved runtime dependencies.`
- `config writer: yes - saved stick axes continue through the existing fail-closed emulator configuration writers without writer changes.`

Automated evidence for stick-level capture (2026-07-13; uncommitted working
tree):

- the focused controller-setup widget suite passed 32 tests and `mise run test`
  passed all 679 console tests;
- `mise run analyze`, Dart format, and `git diff --check` were clean;
- runtime negative searches found only existing structured `Process.start` /
  `Process.run` paths with `runInShell: false`, comments, and the SDL dependency
  helper. No shell-string launch or durable `contentRoot` path was introduced;
- schema remains 10. Drift declarations/generated output, runtime profile ids,
  and emulator config writers are unchanged;
- the owner accepted corrected Run C2 in *The World Is Not Enough*: physical
  right stick drove N64 movement and physical left stick drove the C-button
  cluster, with correct up/down/left/right orientation and no old-role leakage.
  The durable row contained exactly
  `leftx:a2,lefty:a3,rightx:a0,righty:a1` with no inversion markers. The fresh
  N64 config emitted left X `-2/+2`, left Y `+3/-3`, right X `-0/+0`, and right
  Y `+1/-1`, matching the accepted hardware behavior. Paired-stick hardware
  acceptance is complete. The owner then used `Use detected map`; the primary
  verified the durable row returned to exact detected axes
  `leftx:a0,lefty:a1,rightx:a2,righty:a3` with the original buttons and
  triggers intact and no inversion markers. At that point, trigger and
  multi-controller gates remained pending.
- Run D established the normal *Ape Escape* baseline (physical LB snapped the
  camera behind the player; physical LT entered first person), then swapped the
  physical inputs across canonical Left shoulder/Left trigger. The owner
  accepted physical LT snapping the camera and physical LB entering first
  person, with neither prior action leaking through. The durable row and the
  managed DuckStation `gamecontrollerdb.txt` both contained exactly
  `leftshoulder:+a4,lefttrigger:b9`; DuckStation retained its stable canonical
  INI contract `L1 = SDL-0/LeftShoulder` and
  `L2 = SDL-0/+LeftTrigger`. This closes representative cross-type trigger
  capture and managed DuckStation runtime emission. The owner then used
  `Use detected map`; the primary verified exact normal bindings
  `leftshoulder:b9,lefttrigger:a4`, detected stick axes `0/1` and `2/3`, the
  original buttons, and no inversion markers. The multi-controller gate
  remains pending.

Required live acceptance before completion:

1. Exercise raw SDL button, hat, centered stick, endpoint-resting trigger, and
   disconnect behavior with real hardware through the unified Test/Map surface.
2. Restart ROMD and prove the saved platform/GUID setup persists.
3. Launch the managed verified-singleton DuckStation runtime and verify swapped
   gameplay controls, both sticks, triggers, and prior bindings no longer act.
4. Confirm multiple controllers keep automatic emulator order and receive no
   unsupported custom gameplay mapping.
5. Use `Use detected map`, restart/reopen ROMD, and confirm the detected
   platform/GUID row persists and activates the calibrated RetroArch gameplay
   policy on launch. **Accepted 2026-07-13**: the saved row activated the live
   N64 face/stick path and remained byte-identical across an app process
   restart.

Rejected implementation evidence retained for history only: the uncommitted
shortcut-centric editor reached clean analysis, 150 focused tests, 627 full
tests, and independent review before the owner rejected its product direction.
Those signals do not approve or complete this pivot. Reusable transaction,
focus, accessibility, and writer-fixture work may be adapted; the profile/GUID
shortcut editor and template-activation contract must not ship as Slice 4.

### Slice 5 - RetroArch Canonical Gameplay Adapter Research

Status: duplicated full-controller MFi calibration and implementation approval
completed 2026-07-13; production emission and live swapped-control acceptance
in progress; N64 canonical-to-core policy implementation and reproducibility
work also remain in progress

Purpose: establish whether the pinned macOS MFi runtime can consume ROMD's
canonical gameplay mapping. Current evidence does not support GUID-keyed or
multi-pad custom mapping.

Research requirements (frozen-envelope discipline):

- complete raw/SDL-source to MFi button/axis/hat calibration for every canonical
  gameplay control, reproduced twice from clean configs;
- whether a session-scoped MFi profile can be applied safely to the verified
  singleton without claiming GUID-keyed runtime identity;
- interaction with RetroArch autoconfig and the existing Slice 2 port-1/hotkey
  binds;
- explicit rejection or separate research for multi-controller emission.

Exit criteria:

- an updated policy document under `docs/runtime-controller-policies/`;
- writer fixtures proving every approved gameplay byte and fail-closed path;
- complete live singleton hardware acceptance;
- explicit owner approval before any production emission ships.

Initial audit evidence:

- current SDL canonical gameplay data cannot be translated through the seven
  shortcut-only MFi calibration values;
- gameplay requires a new versioned policy and authorization distinct from
  shortcut permission;
- a byte-parity regression proves populated canonical gameplay mappings remain
  inert for every current RetroArch singleton and multi-controller policy;
- Guide, complete axis/trigger polarity, explicit unbinds, and replacement of
  prior autoconfig behavior remain hardware-calibration requirements.

Approved calibration and implementation contract (2026-07-13):

- two isolated runs produced byte-identical 48-key/24-active gameplay bind
  surfaces under the pinned 8BitDo Pro 2 USB/MFi envelope;
- the new policy id is
  `retroarch-macos-single-controller-mfi-8bitdo-pro2-usb-gameplay-v1` and uses
  explicit gameplay authorization independent of shortcut permission;
- the writer owns and replaces the complete port-1 gameplay bind surface;
  missing canonical controls are explicit unbinds, while an unknown present
  raw input rejects the entire custom projection;
- Guide remains frontend-only, and generic singleton, other controllers, and
  every multi-controller envelope remain unchanged and fail closed;
- final acceptance still requires config-byte coverage and a live ROMD launch
  proving representative swaps work while prior bindings stop acting.

Implementation evidence (2026-07-13; uncommitted working tree):

- the launch contract now carries gameplay authorization independently from
  shortcut authorization, and the provider grants the gameplay-v1 policy only
  to the managed reviewed exact singleton with matching saved platform/GUID
  metadata;
- the RetroArch writer owns all 48 port-1 button/axis alternatives and rejects
  an unknown present input as one whole projection rather than mixing custom
  and autoconfig bytes;
- persisted SQLite mapping -> launch provider -> RetroArch config integration
  proves the selected raw inputs reach the calibrated MFi output;
- `mise run analyze` passed with no issues; after the detected-map and
  positional-crosswalk corrections, the focused controller-UI/provider/launch/
  writer/integration matrix passed 104 tests and the full console suite passed
  661 tests. The correction subsets passed 20 controller-UI tests and 39
  RetroArch writer/integration tests. Runtime negative searches, Dart format,
  and `git diff --check` passed;
- schema remains `10` with no Drift declaration, migration, or generated-file
  diff. The independent reviewer reported no blocking findings and passed the
  then-current tree. The first live managed RetroArch face-button case appeared
  to replace the prior MFi autoconfig pair, but the later positional-crosswalk
  diagnosis invalidates that run as face-position acceptance. Super Mario World
  still confirmed unchanged D-pad directions and distinct L/R camera movement.
  A corrected N64 rerun then accepted South -> N64 A, West -> N64 B, North not
  B, left-stick movement, and right-stick C controls. A fresh corrected-build
  SNES South/East swap also proved the prior face bindings stopped acting.
  Representative trigger/stick swaps remain a completion gate.

Approved N64 adapter contract and live diagnosis (2026-07-13):

- physical-controller-to-canonical-RetroPad mapping remains the controller
  setup's responsibility; canonical-RetroPad-to-N64 mapping is a separate
  RetroArch adapter/core policy and must not mutate the saved hardware map;
- the standard modern-controller layout maps South/RetroPad B to N64 A,
  West/RetroPad Y to N64 B, the left stick to the control stick, the right stick
  to the C cluster, L2 to Z, shoulders to N64 L/R, and D-pad/Start directly;
- each N64 title receives ROMD-owned core options with alternate mapping false
  and explicit `C1`/`C2`/`C3`/`C4` directions. Core options and remaps are
  isolated beneath that title's `configRoot`; automatic override loading,
  per-game core-option discovery, and automatic remap load/save are disabled so
  global RetroArch state cannot silently change the policy;
- a live *The World Is Not Enough* run confirmed left-stick movement,
  right-stick C controls, and responding triggers. Follow-up Reset testing
  confirmed printed X is canonical North, Y is West, B is South, and A is East.
  `Use detected map` then deleted the durable row, so launch fell back to the
  global MFi autoconfig: physical MFi `9`/top X became RetroPad Y/West, which
  Mupen correctly interpreted as N64 B. Independently, the old custom writer
  used raw `0/1/2/3` -> MFi `8/0/9/1`, reversing South/East and West/North. SDL3
  had already converted the legacy label-hint map to positional raw controls:
  South `b1`, East `b0`, West `b3`, North `b2`. The corrected writer therefore
  uses `0/1/2/3` -> `0/8/1/9`. The two defects, not the N64 core policy, explain
  the face-button failure. After explicitly saving the detected map, the owner
  accepted the corrected live mapping: printed B/South was N64 A, printed
  Y/West was N64 B, printed X/North was no longer B, and both stick roles
  remained correct;
- the current Mupen64Plus-Next dependency is mutable nightly `/latest` with no
  digest. Pinning/versioning it is required before its behavior can become a
  frozen runtime policy. Slice 5 remains incomplete.

Runtime decisions for the N64 adapter refinement:

- `artifact resolver: yes - the current Mupen64Plus-Next core is fetched from mutable nightly /latest with no digest, so a reproducible N64 control policy requires pinning/versioning the core artifact in the existing resolver/provisioner before claiming frozen behavior.`
- `config writer: yes - the RetroArch writer must emit ROMD-owned N64 core options and fail-closed remap paths so global RetroArch state cannot change the canonical RetroPad-to-N64 policy.`

Approved Genesis Plus GX adapter contract (2026-07-13):

- the shipped default is Genesis Plus GX; PicoDrive remains a separate
  alternate profile. Genesis Plus GX's native mapping is RetroPad West/South/
  East -> Genesis A/B/C and RetroPad L/North/R -> Genesis X/Y/Z, with Select as
  Mode and direct D-pad/Start;
- installed *ClayFighter* advertises six-button support. The owner accepted all
  six controls on the restored 8BitDo layout: printed Y/B/A -> Genesis A/B/C
  and printed LB/X/RB -> Genesis X/Y/Z;
- selected-core identity comes from `RuntimeProfile.coreRequirement` and is
  passed into the writer. Only core id `genesis_plus_gx` receives this policy;
  the PicoDrive alternate remains a separate profile with intentionally
  unchanged controller behavior;
- platform/core identity is necessary but not sufficient. Genesis Plus GX
  policy bytes require the existing approved exact single-controller gameplay
  envelope. Empty setups, fallback controllers, unsupported singleton setups,
  and every multi-controller session retain their prior bytes; in particular,
  ROMD must not inject an asymmetric player-one analog-D-pad policy into an
  uncorrelated session;
- Genesis Plus GX explicitly selects Joypad Auto, mirrors the left analog stick
  to the digital D-pad, disables inherited global remap and override loading,
  and uses an isolated per-title remap directory beneath `configRoot`. It does
  not create a Genesis core-options file;
- after the isolation change, the owner accepted the final reviewed build in
  *ClayFighter*: printed Y/B/A -> Genesis A/B/C, printed LB/X/RB -> Genesis
  X/Y/Z, D-pad and left-stick movement, Mode, and Start all worked as specified.
  The fresh per-title config selected Joypad Auto, enabled analog-D-pad mode,
  emitted restored MFi gameplay positions `a=0,b=8,x=1,y=9,l=10,r=11`, disabled
  inherited remaps/overrides and remap saving, and pointed at the existing empty
  per-title `remaps` directory. This Genesis Plus GX singleton adapter policy is
  hardware accepted; broader Slice 4 and multi-controller gates remain open.
- after the cardinality correction, the focused RetroArch adapter/writer matrix
  passed 53 tests and the full console suite passed 667 tests; analysis and
  Dart format were clean, runtime negative searches found no shell-string
  launch or durable `contentRoot` path, and `git diff --check` passed.

- `artifact resolver: no - Genesis controller policy uses the existing resolved RetroArch core and adds no provisioning requirement.`
- `config writer: yes - the Genesis Plus GX canonical-to-core policy is isolated in the existing fail-closed RetroArch configuration writer.`

### Slice 6 - In-Game Shortcuts Via Runtime-Native Hotkeys

Status: merges the overlay epic's v1 fallback; design decision required

Purpose: give every session a controller path to menu/save/load/quit using
the runtime's own hotkey system — the overlay proposal's accepted fallback
after the two-process overlay proved impossible on macOS.

Scope:

- single-pad sessions: DuckStation shortcuts return via Slice 2 on the proven
  pad; RetroArch remains suppressed until physical hotkey calibration completes;
- multi-pad sessions: decide between a device-independent combo (for example
  menu-toggle gamepad combo, standard console behavior, any pad may invoke)
  or keeping suppression; this amends "P1 alone owns shortcuts" and needs an
  explicit owner decision recorded here;
- configure via existing fail-closed writers; no correlation claims;
- align quit/save-state flows with ROMD session lifecycle (return to ROMD
  cleanly).

Exit criteria: shortcut behavior documented per runtime envelope; hardware
confirmation; copy in In-game shortcuts matches real behavior.

### Slice 7 - Nintendo-Native Action Semantics

Status: carried forward unchanged from management-roadmap Slice 10; may run
independently after Slice 1.

## Backlog (Intentionally Unscheduled)

- controller-family icon catalog behind the existing resolver seam;
- durable controller-to-profile affinity after explicit owner action;
- play-history attribution for assigned guest profiles;
- PCSX2 custom mapping only after PCSX2 exposes a supported deterministic
  external input contract or ROMD can verify behavior inside the actual
  emulator process. Two 2.6.3 macOS/USB Pro 2 experiments are rejected in the
  controller management roadmap, and multi-controller routing remains
  separately deferred;
- best-effort ordinal seating on a kiosk deployment (see the parked routing
  roadmap's future flag; separate owner decision);
- controller-family art beyond the generic positional setup experience.

## Open Decisions

1. Slice 6 multi-pad shortcut policy (any-pad combo vs continued suppression).
2. Slice 5 multi-pad emission ship approval after research.

## Validation

Console slices validate from `clients/romd_console`:

```bash
mise run analyze
mise run test
```

Runtime-touching slices (2, 5, 6) additionally require the console runtime
skill's negative searches and, where noted, manual hardware confirmation.
Docs-only slices require link checks and explicit untested statements.
Unexpected failures route through `docs/known-issues.md` first. The
management roadmap's always-required regression coverage and hardware release
matrix continue to apply to every touched surface.

## Explicit Non-Goals

- deterministic multi-pad port routing on any platform;
- virtual controller devices, brokers, or kernel/driver work;
- emulator patches or forks for input;
- automatic controller-to-profile association;
- a profile prompt blocking every additional controller;
- durable seating;
- renaming runtime profile ids.

## Amendment Protocol

Per landed slice: update status with commit evidence; record exact validation
without overclaiming; list untested boundaries; preserve carried-forward
contracts unless an owner decision amends them here; keep implementation
history in commits.
