# macOS 26.5.2 Build 25F84 Virtual Controller Isolation Research

Status: current; classification `notIsolated`; Gate 0 failed for IOHID seizure
under both the MFi and iohidmanager RetroArch joypad envelopes

Validated: 2026-07-10; extended 2026-07-11

## Frozen Environment

- Host: macOS 26.5.2 build 25F84, arm64, Apple silicon.
- Xcode SDK: installed macOS SDK with Swift 6.3.3 toolchain.
- ROMD revision: `3768d8d` at observation start; worktree was clean.
- Probe: development-only Swift package under
  `tools/macos_controller_isolation_probe`; no broker or virtual descriptor.
- Candidate virtual backend: CoreHID `HIDVirtualDevice`, not activated.
- Candidate acquisition: IOHID exclusive seizure, not exercised.
- Required managed envelopes: DuckStation v0.1-10998 and RetroArch 1.22.2 Metal
  as frozen in the adapter policy reports. Local discovery later found all
  three app bundles under the macOS application-support data root, but the
  installed DuckStation and RetroArch provenance is not currently acceptable
  for Gate 0 runtime trials; details are recorded below.
- Controlled gamepad hardware: none detected by the bounded USB/Bluetooth
  controller-name search. This is not a complete HID inventory conclusion.
- Permission, prompt, provisioning-profile, signing, and entitlement state:
  untested. No entitlement-bearing binary was built or run.
- First controlled hardware: 8BitDo Pro 2 over USB. The device mode was not
  exposed by captured records and remains `unknown`; it must not be labeled
  D-input or X-input by inference.

Host serial number, hardware UUID, provisioning identifier, and unrelated paths
were observed by a host-information command and intentionally omitted.

## Primary-Source Analysis

Primary-source facts:

- Apple documents `IOHIDDeviceOpen` with `kIOHIDOptionsTypeSeizeDevice` as an
  exclusive link that prevents the system and other clients from receiving
  events. This does not prove behavior for SDL HIDAPI, GameController, a
  synthetic compatibility device, or either managed runtime.
- Apple documents that macOS 14+ can create one GameController synthetic HID
  device per connected controller. A HID-compatible physical controller may
  therefore have physical and synthetic registry representations. The only
  supported synthetic marker is `GCSyntheticDevice` /
  `kIOHIDGCSyntheticDeviceKey`; it must be captured rather than inferred.
- The installed SDK exposes CoreHID `HIDVirtualDevice`, including activation,
  report dispatch, and get/set-report delegate surfaces. The installed IOKit
  header states that virtual-device creation requires
  `com.apple.developer.hid.virtual.device`.
- Apple documents DriverKit/System Extension entitlement, signing, activation,
  notarization, and distribution requirements for a DriverKit fallback. That
  fallback is a separate envelope and is not assumed equivalent to CoreHID.

Direct observations:

- The installed SDK contains `HIDVirtualDevice`, seizure, the virtual-device
  entitlement string, and `kIOHIDGCSyntheticDeviceKey` declarations.
- A read-only IORegistry HID query completed, but its raw output was not retained.
- The expanded redaction-safe probe observed empty IORegistry controller,
  CoreHID controller, IOHID joystick/gamepad, and GameController inventories
  before and after a one-second window. The IORegistry observer deliberately
  includes only joystick/gamepad/multi-axis usages and entries carrying Apple's
  synthetic-device marker; unrelated HID services are excluded.
- The existing ROMD SDL 3 probe, explicitly pointed at the already managed
  local artifact, independently observed zero SDL gamepads.
- Local runtime discovery corrected the earlier repository-local search:
  DuckStation, RetroArch, and PCSX2 app bundles are installed under ROMD's
  macOS application-support root. Direct bundle inspection found DuckStation
  `0.1-11515-gf1e089374`, while its install marker records the former rolling
  `latest` URL. That is not the frozen v0.1-10998 envelope. RetroArch reports
  version 1.22.2, but no runtime marker was present at the expected root, so
  installed provenance was not established. PCSX2 2.6.3 is present but remains
  outside mapping emission and was not launched.
- Provisioner review found that DuckStation already rejects its stale marker on
  the next `ensure()`, but RetroArch previously accepted any existing app
  bundle. The RetroArch provisioner is now fail-closed on a frontend fingerprint
  marker and archive SHA-256. Existing application-support installs were not
  mutated during this research; exact reprovisioning remains required before a
  runtime trial.
- After ROMD reprovisioning, direct checks confirmed DuckStation
  `0.1-10998-g9b0a4ec55`, the expected bundled SDL digest, and the complete
  pinned install marker. RetroArch 1.22.2 likewise had the complete pinned
  frontend marker. These checks establish artifact eligibility, not runtime
  isolation.
- A menu-only verbose RetroArch baseline directly confirmed joypad driver
  `mfi`. It emitted no controller connection or autoconfiguration records while
  the independent probe reported empty IORegistry, CoreHID, IOHID, and
  GameController inventories. This is an empty-controller orchestration result,
  not a hardware case.
- `systemextensionsctl list` failed with `OSSystemExtensionErrorDomain error 1`.
  No retry was made and no conclusion about installed extensions follows.

Inference requiring experiment:

- Seizing one physical HID representation may leave GameController or a
  synthetic/alternate HID representation capable of feeding RetroArch or
  DuckStation.
- Even when input is blocked, enumerated physical/synthetic representations may
  alter DuckStation player ids or runtime selection.
- Entitlement grantability and direct-distribution viability may prevent a
  shippable CoreHID backend even if a development probe works.

Primary sources:

- <https://developer.apple.com/documentation/iokit/1588670-iohiddeviceopen>
- <https://developer.apple.com/documentation/iokit/1556660-anonymous/kiohidoptionstypeseizedevice>
- <https://developer.apple.com/documentation/gamecontroller/understanding-game-controller-backward-compatibility>
- <https://developer.apple.com/documentation/bundleresources/system-extensions>
- <https://developer.apple.com/documentation/driverkit/requesting_entitlements_for_driverkit_development>
- <https://developer.apple.com/documentation/systemextensions/installing-system-extensions-and-drivers>

## Probe Design And Commands

Phase A now has a platform-native diagnostic package. Its explicit development
command captures redacted JSON Lines from IORegistry, CoreHID, IOHIDManager,
and GameController. ROMD SDL 3 is currently observed through its separate
existing read-only Dart probe; a shared evidence envelope, DuckStation,
RetroArch MFi, and PCSX2 observers remain to be added. Each observer gets its
own record; the collector does not correlate or classify.

Phase B adds opt-in acquisition of one explicitly selected physical candidate.
It repeats every observer before, during, and after seizure and records open,
input, hotplug, output, numbering, selection, permissions, and recovery.

Phase C is not yet authorized. If A/B evidence and entitlement availability
permit it, a separately reviewable target may create one fixed diagnostic
virtual gamepad, start neutral, forward proof controls, neutralize before exit,
and handle signals/failures. It must remain outside Flutter, normal launch,
persistence, and config writers, and must never classify results.

Read-only commands executed:

```text
sw_vers
uname -m
xcrun --show-sdk-path
xcrun swift --version
SDK declaration searches for CoreHID, seizure, synthetic HID, and entitlement
ioreg -r -c IOHIDDevice -l
systemextensionsctl list
bounded USB/Bluetooth controller-name inventory search
swift build
swift test
swift run romd-macos-controller-probe inventory --duration-seconds 1
mise exec -- dart run tool/sdl_gamepad_probe.dart --sdl3-path <managed-library>
RetroArch --verbose --menu --max-frames=600 --config=<temporary-config>
```

## Independent Inventories

| Observer | Baseline result | Seized result | Virtual result |
| --- | --- | --- | --- |
| IORegistry HID | empty baseline; later physical 8BitDo plus marked synthetic representation | both remained enumerated | no virtual device |
| CoreHID | empty baseline; later physical 8BitDo with shared fingerprints | physical remained enumerated | no virtual device |
| IOHIDManager | empty baseline; later one physical candidate | physical remained enumerated and seized | no virtual device |
| GameController | empty baseline; later 8BitDo appeared after initialization delay | remained exposed during seizure | no virtual device |
| ROMD SDL 3 | empty baseline; later one exact GUID+serial 8BitDo | post-release recovery confirmed | no virtual device |
| DuckStation SDL | exact artifact now eligible; runtime trial not run | untested | no virtual device |
| RetroArch MFi | exact frontend, `mfi`, 8BitDo index 0 / port 1 | received physical input during seizure | no virtual device |
| PCSX2 | 2.6.3 app present; inventory trial not authorized or run | untested | untested |

No device identities, correlations, transport values, or inventory rows are
synthesized from these missing captures.

## Acquisition And Isolation Semantics

The reviewed physical 8BitDo candidate opened successfully with exclusive
IOHID seizure. During a 10-second control exercise ROMD received 329 attributed
physical callbacks with zero callback errors. Physical IORegistry/CoreHID/
IOHID records remained enumerated, as did a separate
`GCSyntheticDevice=true` compatibility representation and the GameController
object. Close succeeded and all observers plus ROMD SDL recovered normally.

With exact RetroArch 1.22.2 MFi active, a second 10-second seizure succeeded
and ROMD received 42 attributed physical callbacks. RetroArch logged the
8BitDo connection, assigned desired MFi index 0, and autoconfigured port 1. The
owner directly observed the RetroArch menu move in response to button presses
during seizure. This is isolation state 4: the managed runtime received
physical input. Which remaining representation carried that input is not yet
attributed; synthetic/GameController bypass is an inference, not a proven path.

## Adversarial Matrix

One partial case ran: 8BitDo Pro 2 over USB with device mode unknown, one
controller, connected before the probe, exact RetroArch 1.22.2 active. It
failed because physical input reached RetroArch during seizure.

Still untested: explicit 8BitDo Pro 2 D-input and X-input modes; Xbox-style;
DualSense USB and Bluetooth; families A/B and B/A; identical devices with and
without distinguishable serials; mixed transport; pre/post-probe connection;
disconnect/reconnect acquired; changed ROMD provider id; runtime/probe restart;
probe, Flutter/ROMD, and emulator crashes; and fallback/no-SDL.

## Duplicate Input, Numbering, Latency, And Output

Attributed ROMD physical counts were 329 in the standalone seizure trial and 42
in the RetroArch trial. RetroArch selected MFi index 0 / port 1 and visibly
responded during seizure. No virtual device existed, so virtual/duplicate event
counts, forwarding latency, report loss, rumble, and output feedback remain
untested. The one-controller numbering observation cannot establish stability.

## Permissions, Entitlements, Signing, And Distribution

The entitlement requirement is a documented prerequisite, not a granted
capability. Team entitlement availability, provisioning, direct-distribution
signing/notarization, user prompts, sandbox behavior, Input Monitoring or other
privacy requirements, and denial behavior remain untested. Production may not
depend on disabled macOS security or an unsafe root workflow.

## Cleanup And Crash Behavior

Untested because no probe acquired hardware or created a virtual device. A
future probe must automate normal shutdown, handled failure, repeated
activation, neutralization, and watchdog loss, then manually verify forced
termination, ordinary-client recovery, and absence of stuck runtime input.

## Mismatches And Stop Conditions

- The system-extension inventory command was denied; this is an observation,
  not a durable known issue or an isolation mismatch.
- Required hardware, remaining collectors, entitlement evidence, virtual
  device, and runtime input attribution are absent.
- At initial discovery, installed DuckStation was not the frozen policy
  artifact and RetroArch provenance was incomplete. Those installations were
  correctly excluded until ROMD reprovisioned them.
- Historical installed-artifact blockers above were corrected by ROMD
  reprovisioning before the empty RetroArch baseline. They remain recorded to
  explain why earlier observations were not accepted.
- RetroArch `--max-frames` did not terminate menu mode. The process required a
  manual interrupt and therefore does not satisfy the future automated
  lifecycle contract.
- An explicit missing temporary config still allowed RetroArch to consult
  default user playlist locations and verbose logs contained unrelated device
  identifiers. Raw output was not retained. A future runner must pre-create a
  complete isolated config/root, redact streaming output, and own a watchdog.
- Any future physical input, duplicate input, unexplained port shift, alternate
  representation bypass, inconsistent USB/Bluetooth acquisition, unsafe
  cleanup, unavailable distribution entitlement, root-only requirement, or
  emulator-patch dependency triggers the roadmap stop condition.
- The physical-input stop condition occurred: exact RetroArch received button
  input while the physical IOHID representation was successfully seized.

## Classification And Safe Fallback

Classification: `notIsolated` for macOS 26.5.2 build 25F84 using IOHID seizure
with exact RetroArch 1.22.2 MFi and the tested 8BitDo USB representation.
Successful exclusive open did not prevent runtime input. This single mismatch
blocks platform-wide `isolated` and makes Phase C virtual-device creation
insufficient until a stronger physical-hiding boundary is proposed and proven.

The 2026-07-11 retest extends `notIsolated` to the `hid` (iohidmanager) joypad
envelope: with the physical candidate exclusively seized, the exact frontend
launched with `input_joypad_driver = "hid"` still enumerated the pad
(`[IOHID] Port 0`), autoconfigured port 1, and visibly responded to button
input while the probe attributed 225 physical callbacks. Seizure is therefore
not a physical-hiding boundary against either compiled macOS joypad path.

ROMD must retain the fail-closed fallback: no production virtual backend, no
normal launch routing through virtual devices, no forced DuckStation/RetroArch
references, no PCSX2 mapping, automatic emulator order, and no promise of
custom runtime seating.

## Invalidation And Revalidation

Any OS/build, SDK/API, entitlement/signing, probe/broker, descriptor, ROMD SDL
provider, managed runtime artifact/backend/config, or hardware firmware/mode/
transport change invalidates affected future evidence. This planning report is
superseded when a probe revision and complete redacted Phase A inventory exist.

## Automated Tests And Manual Hardware Evidence

`swift build` passed. `swift test` passed 6 Swift Testing tests covering
inventory defaults, explicit seizure arguments, 7 malformed argument cases,
deterministic identity redaction, IORegistry controller filtering, and
attributed physical-event counting without raw control values. Swift format
lint passed. The one-second inventory command passed and produced valid JSONL
with empty IORegistry, CoreHID, IOHID, and GameController inventories.
The ROMD SDL probe first hit the documented sandbox SDK-cache denial, then
passed with scoped permission and reported zero controllers in the empty-host
baseline. Manual 8BitDo USB evidence now covers physical inventory, successful
seizure, attributed ROMD input, RetroArch bypass, normal close, forced probe
termination recovery, and reconnect/provider-id replacement. Virtual-device
behavior remains untested and is stopped by the failed isolation gate.

The exact RetroArch baseline started and confirmed `mfi`; the independent
one-second inventory passed with all controller sets empty. Menu mode ignored
the requested frame bound, so RetroArch was manually interrupted and the
temporary config was removed. That earlier empty baseline observed no physical
input, port, numbering, duplicate, or isolation behavior.

The probe's first attempted seizure run exposed a CoreHID notification-stream
cancellation hang and was forcibly terminated. Immediate IORegistry/CoreHID/
IOHID/GameController and ROMD SDL checks proved device recovery. The observer
was changed to cancel without awaiting the stream; Swift build, 6 tests, and
format lint passed. A two-second lifecycle retry completed, exclusive close
succeeded, and the subsequent two 10-second seizure trials completed normally.

Console runtime validation for the provenance fix: the focused RetroArch
provisioner suite passed 7 tests; `mise run analyze` passed with no issues; and
`mise run test` passed all 509 tests. No emulator process was launched.

## Owner Decisions

Production implementation is stopped. Owner review is required before pursuing
an alternate macOS hiding/filter mechanism, an emulator patch, or abandoning
deterministic macOS routing in favor of Linux-first work. `runtimeScoped` is not
proposed because the tested exact RetroArch runtime failed.

2026-07-11: after the iohidmanager retest and the failed playerIndex
propagation research, the owner decided to stop pursuing deterministic
controller order on macOS. ROMD retains the automatic-order fallback and its
existing fail-closed writer behavior. Linux virtual-routing slices remain
defined but unscheduled.

Retest evidence (2026-07-11, one 8BitDo Pro 2 USB, mode unknown): seizure of
the fresh IOHID candidate succeeded for a 300-second window; RetroArch was
launched mid-window with an isolated config selecting `input_joypad_driver =
"hid"`; it logged `Found HID driver: "iohidmanager"`, `[IOHID] Port 0: 8BitDo
Pro 2.`, and `[Autoconf] 8BitDo Pro 2 configured in port 1.`; the owner
directly observed menu movement from button presses; the probe recorded 225
attributed physical callbacks with zero errors and a clean exclusive close.
Which representation carried input to the runtime remains unattributed.

- `artifact resolver: yes - isolation conclusions are valid only for exact managed emulator artifacts, native probe/broker binaries, OS builds, virtual descriptors, and platform backends.`
- `config writer: yes - runtime configuration must eventually select only approved ROMD virtual devices, but no writer change is authorized until isolation evidence is reviewed.`
