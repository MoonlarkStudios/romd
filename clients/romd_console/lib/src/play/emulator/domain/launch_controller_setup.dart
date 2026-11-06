import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';

/// How strongly ROMD can correlate a listed controller with a runtime input.
///
/// No current ROMD provider ordinal is proven to equal an emulator's
/// cross-process input index. Keeping that uncertainty in the contract avoids
/// presenting connection order as stable physical identity.
enum RuntimeControllerInputProvider { sdl3, appleGameController, other }

enum RuntimeControllerInputCorrelation {
  verified,
  acceptedBestEffort,
  uncorrelated,
}

/// Named, evidence-backed policies that may establish runtime input references.
final class RuntimeControllerInputPolicies {
  const RuntimeControllerInputPolicies._();

  static const String retroArchMacosSingleController =
      'retroarch-macos-single-controller';
  static const String retroArchMacosSingleControllerMfi8BitDoPro2Usb =
      'retroarch-macos-single-controller-mfi-8bitdo-pro2-usb';
  static const String retroArchMacosSingleControllerMfi8BitDoPro2UsbGameplayV1 =
      'retroarch-macos-single-controller-mfi-8bitdo-pro2-usb-gameplay-v1';
  static const String retroArchMfi8BitDoPro2UsbDisplayName = '8BitDo Pro 2';
  static const String retroArchMfi8BitDoPro2UsbSdlGuid =
      '030001f2c82d00000660000000026800';
  static const String duckStationMacosSingleController =
      'duckstation-macos-single-controller';
  static const String dolphinMacos2606SingleControllerSdl3GameplayV1 =
      'dolphin-macos-2606-single-controller-sdl3-gameplay-v1';
}

/// Runtime-facing input metadata for one connected player controller.
final class RuntimeControllerInputReference {
  const RuntimeControllerInputReference({
    required this.provider,
    required this.providerDeviceId,
    required this.providerOrdinal,
    required this.correlation,
    this.runtimeReference,
    this.adapterPolicyId,
    this.controllerShortcutsAllowed = false,
    this.controllerGameplayAllowed = false,
  }) : assert(
         correlation == RuntimeControllerInputCorrelation.uncorrelated ||
             (runtimeReference != null &&
                 adapterPolicyId != null &&
                 adapterPolicyId != ''),
       ),
       assert(
         correlation != RuntimeControllerInputCorrelation.uncorrelated ||
             runtimeReference == null,
       ),
       assert(
         !controllerShortcutsAllowed ||
             (correlation != RuntimeControllerInputCorrelation.uncorrelated &&
                 runtimeReference != null &&
                 adapterPolicyId != null &&
                 adapterPolicyId != ''),
       ),
       assert(
         !controllerGameplayAllowed ||
             (correlation != RuntimeControllerInputCorrelation.uncorrelated &&
                 runtimeReference != null &&
                 adapterPolicyId != null &&
                 adapterPolicyId != ''),
       );

  factory RuntimeControllerInputReference.uncorrelated({
    required RuntimeControllerInputProvider provider,
    required String providerDeviceId,
    required int? providerOrdinal,
  }) => RuntimeControllerInputReference(
    provider: provider,
    providerDeviceId: providerDeviceId,
    providerOrdinal: providerOrdinal,
    correlation: RuntimeControllerInputCorrelation.uncorrelated,
  );

  final RuntimeControllerInputProvider provider;

  /// Ephemeral provider id retained for future runtime/device correlation.
  final String providerDeviceId;

  /// Observation-only order from ROMD's provider. It is never a runtime id.
  final int? providerOrdinal;

  /// Adapter-native reference, such as a verified joypad index or `SDL-N` id.
  final Object? runtimeReference;

  final RuntimeControllerInputCorrelation correlation;

  /// Named, pinned adapter policy that established [runtimeReference].
  final String? adapterPolicyId;

  /// Whether the named policy also authorizes controller shortcut emission.
  ///
  /// Runtime-reference verification alone does not establish shortcut safety.
  /// RetroArch remains false pending physical button/hat calibration; true is
  /// policy authorization and does not claim owner hardware validation.
  final bool controllerShortcutsAllowed;

  /// Whether the named policy authorizes canonical gameplay-bind emission.
  ///
  /// Gameplay authorization is intentionally independent from shortcut
  /// authorization. A runtime writer must check the permission for the kind
  /// of bindings it emits rather than inferring one from the other.
  final bool controllerGameplayAllowed;

  bool get canForceRuntimeReference =>
      runtimeReference != null &&
      adapterPolicyId?.trim().isNotEmpty == true &&
      correlation != RuntimeControllerInputCorrelation.uncorrelated;

  bool get canEmitControllerShortcuts =>
      controllerShortcutsAllowed && canForceRuntimeReference;

  bool get canEmitControllerGameplay =>
      controllerGameplayAllowed && canForceRuntimeReference;

  int? get approvedRuntimeIndex =>
      canForceRuntimeReference && runtimeReference is int
      ? runtimeReference! as int
      : null;

  Map<String, Object?> get diagnosticFields =>
      Map<String, Object?>.unmodifiable(<String, Object?>{
        'provider': provider.name,
        'providerDeviceId': providerDeviceId,
        'providerOrdinal': providerOrdinal,
        'runtimeReference': runtimeReference,
        'correlation': correlation.name,
        'adapterPolicyId': adapterPolicyId,
        'controllerShortcutsAllowed': controllerShortcutsAllowed,
        'controllerGameplayAllowed': controllerGameplayAllowed,
      });

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      other is RuntimeControllerInputReference &&
          other.provider == provider &&
          other.providerDeviceId == providerDeviceId &&
          other.providerOrdinal == providerOrdinal &&
          other.runtimeReference == runtimeReference &&
          other.correlation == correlation &&
          other.adapterPolicyId == adapterPolicyId &&
          other.controllerShortcutsAllowed == controllerShortcutsAllowed &&
          other.controllerGameplayAllowed == controllerGameplayAllowed;

  @override
  int get hashCode => Object.hash(
    provider,
    providerDeviceId,
    providerOrdinal,
    runtimeReference,
    correlation,
    adapterPolicyId,
    controllerShortcutsAllowed,
    controllerGameplayAllowed,
  );
}

/// Immutable launch-time controller data for one player slot.
///
/// Mapping resolution is per connected controller. Template metadata travels
/// with the effective, capability-filtered mapping so runtime writers never
/// need to repeat preference or mapping resolution.
final class LaunchPlayerController {
  const LaunchPlayerController({
    required this.resolvedSlot,
    required this.inputReference,
    required this.mapping,
    required this.templateId,
    required this.capabilities,
    this.gameplayMapping,
  });

  factory LaunchPlayerController.compatibility({
    required ResolvedControllerSlot resolvedSlot,
    required ControllerMapping mapping,
  }) => LaunchPlayerController(
    resolvedSlot: resolvedSlot,
    inputReference: RuntimeControllerInputReference.uncorrelated(
      provider: resolvedSlot.controller.identity.sdlGuid == null
          ? RuntimeControllerInputProvider.other
          : RuntimeControllerInputProvider.sdl3,
      providerDeviceId: resolvedSlot.controller.id,
      providerOrdinal: resolvedSlot.runtimeIndex,
    ),
    mapping: mapping,
    templateId: BuiltinControllerTemplates.generic.id,
    capabilities: BuiltinControllerTemplates.generic.capabilities,
  );

  final ResolvedControllerSlot resolvedSlot;
  final RuntimeControllerInputReference inputReference;
  final ControllerMapping mapping;
  final ControllerTemplateId templateId;
  final ControllerCapabilities capabilities;

  /// Explicit physical-input to canonical-gamepad mapping for gameplay.
  ///
  /// This is intentionally separate from [mapping], which contains ROMD
  /// in-game shortcut actions. Runtime writers may consume this only inside
  /// an independently verified controller-correlation policy.
  final ControllerHardwareMapping? gameplayMapping;

  int get playerSlot => resolvedSlot.playerSlot;
  ControllerIdentity get identity => resolvedSlot.controller.identity;
}

/// Runtime-neutral controller setup resolved for one emulator launch.
///
/// Config writers consume [playerControllers], translating runtime input
/// references only at their boundary. Legacy singleton/slot views remain thin
/// compatibility bridges while callers migrate to the per-player contract.
final class LaunchControllerSetup {
  factory LaunchControllerSetup({
    ControllerMapping mapping = BuiltinControllerTemplates.defaultMapping,
    List<LaunchPlayerController>? playerControllers,
    List<ResolvedControllerSlot> controllerSlots =
        const <ResolvedControllerSlot>[],
    Set<int> reservedPlayerSlots = const <int>{},
    Set<int> blockedPlayerSlots = const <int>{},
  }) {
    final players =
        playerControllers ??
        <LaunchPlayerController>[
          for (final slot in controllerSlots)
            LaunchPlayerController.compatibility(
              resolvedSlot: slot,
              mapping: mapping,
            ),
        ];
    return LaunchControllerSetup._(
      fallbackMapping: mapping,
      playerControllers: players,
      reservedPlayerSlots: reservedPlayerSlots,
      blockedPlayerSlots: blockedPlayerSlots,
    );
  }

  LaunchControllerSetup._({
    required this.fallbackMapping,
    required List<LaunchPlayerController> playerControllers,
    required Set<int> reservedPlayerSlots,
    required Set<int> blockedPlayerSlots,
  }) : playerControllers = List<LaunchPlayerController>.unmodifiable(
         playerControllers,
       ),
       reservedPlayerSlots = Set<int>.unmodifiable(reservedPlayerSlots),
       blockedPlayerSlots = Set<int>.unmodifiable(blockedPlayerSlots);

  const LaunchControllerSetup._defaults()
    : fallbackMapping = BuiltinControllerTemplates.defaultMapping,
      playerControllers = const <LaunchPlayerController>[],
      reservedPlayerSlots = const <int>{},
      blockedPlayerSlots = const <int>{};

  static const LaunchControllerSetup defaults =
      LaunchControllerSetup._defaults();

  /// Mapping used only when no concrete player controller is known.
  final ControllerMapping fallbackMapping;

  final List<LaunchPlayerController> playerControllers;

  /// Compatibility P1/default view. Writers should use [playerControllers]
  /// and [playerOneMapping] instead.
  ControllerMapping get mapping =>
      playerControllerForSlot(0)?.mapping ?? fallbackMapping;

  /// P1 owns in-game shortcuts. A known setup with no connected P1 (for
  /// example a reserved or blocked P1 and connected P2) returns null so P2 is
  /// never promoted. A wholly unresolved setup preserves runtime defaults.
  ControllerMapping? get playerOneMapping {
    final p1 = playerControllerForSlot(0);
    if (p1 != null) {
      return p1.mapping;
    }
    return playerControllers.isEmpty &&
            reservedPlayerSlots.isEmpty &&
            blockedPlayerSlots.isEmpty
        ? fallbackMapping
        : null;
  }

  /// Compatibility slot view derived from the per-player entries.
  List<ResolvedControllerSlot> get controllerSlots =>
      List<ResolvedControllerSlot>.unmodifiable(<ResolvedControllerSlot>[
        for (final player in playerControllers) player.resolvedSlot,
      ]);

  /// Exact player-slot claims whose controller is offline for this launch.
  final Set<int> reservedPlayerSlots;

  /// Non-exact player slots that cannot be safely connection-order filled.
  final Set<int> blockedPlayerSlots;

  /// Observation-only provider order retained for diagnostics and migration.
  List<int> get padDeviceIndices => List<int>.unmodifiable(<int>[
    for (final player in playerControllers)
      if (player.inputReference.providerOrdinal case final ordinal?) ordinal,
  ]);

  LaunchPlayerController? playerControllerForSlot(int playerSlot) {
    for (final player in playerControllers) {
      if (player.playerSlot == playerSlot) {
        return player;
      }
    }
    return null;
  }

  bool isPlayerSlotReserved(int playerSlot) =>
      reservedPlayerSlots.contains(playerSlot);

  bool isPlayerSlotBlocked(int playerSlot) =>
      reservedPlayerSlots.contains(playerSlot) ||
      blockedPlayerSlots.contains(playerSlot);
}
