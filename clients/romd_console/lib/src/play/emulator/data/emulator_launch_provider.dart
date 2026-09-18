import 'dart:io';

import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping_resolver.dart';
import 'package:romd_console/src/play/controllers/domain/controller_preferences.dart';
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_adapter.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/launch_provider.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

final class EmulatorRuntimeLaunchEntry {
  const EmulatorRuntimeLaunchEntry({
    required this.dependencyResolver,
    required this.adapterFactory,
  });

  final RuntimeDependencyResolver dependencyResolver;
  final RuntimeAdapter Function() adapterFactory;
}

/// Launch provider for emulator-backed runtime profiles.
///
/// Each runtime adapter id has exactly one entry pairing dependency resolution
/// with the adapter factory that consumes those resolved dependencies.
final class EmulatorLaunchProvider implements LaunchProvider {
  EmulatorLaunchProvider({
    required Map<RuntimeAdapterId, EmulatorRuntimeLaunchEntry> runtimes,
    ControllerMappingResolver controllerMappings =
        const ControllerMappingResolver(),
    ControllerPreferencesRepository? controllerPreferences,
    ControllerHardwareMappingRepository? controllerHardwareMappings,
    SessionControllerSlotClaims? controllerSlotClaims,
    GamepadLister gamepadLister = _noGamepads,
    String? operatingSystem,
  }) : _runtimes =
           Map<RuntimeAdapterId, EmulatorRuntimeLaunchEntry>.unmodifiable(
             runtimes,
           ),
       _controllerMappings = controllerMappings,
       _controllerPreferences = controllerPreferences,
       _controllerHardwareMappings = controllerHardwareMappings,
       _controllerSlotClaims = controllerSlotClaims,
       _gamepadLister = gamepadLister,
       _operatingSystem = operatingSystem ?? Platform.operatingSystem;

  final Map<RuntimeAdapterId, EmulatorRuntimeLaunchEntry> _runtimes;
  final ControllerMappingResolver _controllerMappings;
  final ControllerPreferencesRepository? _controllerPreferences;
  final ControllerHardwareMappingRepository? _controllerHardwareMappings;
  final SessionControllerSlotClaims? _controllerSlotClaims;
  final GamepadLister _gamepadLister;
  final String _operatingSystem;

  static Future<List<ConnectedGamepad>> _noGamepads() async =>
      const <ConnectedGamepad>[];

  @override
  bool supports(RuntimeProfile profile) =>
      _runtimes.containsKey(profile.adapterId);

  @override
  Stream<DependencyProgress> prepare(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  ) {
    final runtime = _runtimes[profile.adapterId];
    return runtime == null
        ? const Stream<DependencyProgress>.empty()
        : runtime.dependencyResolver.resolve(profile, target);
  }

  @override
  Future<LaunchStartResult> launch({
    required RuntimeProfile profile,
    required ResolvedPlayTarget target,
    required List<ResolvedRuntimeDependency> dependencies,
    ReviewedLaunchSnapshot? controllerSnapshot,
  }) async {
    final runtime = _runtimes[profile.adapterId];
    if (runtime == null) {
      return LaunchNotStarted(
        LaunchUnsupportedPlatform(target.platformShortName),
      );
    }

    final controllerInput = await _validatedControllerInput(controllerSnapshot);
    if (controllerInput == null) {
      return const LaunchNotStarted(LaunchControllerReviewRequired());
    }
    final controllerSetup = await _resolveControllerSetup(
      target,
      profile: profile,
      dependencies: dependencies,
      controllerInput: controllerInput,
    );
    if (controllerSnapshot != null &&
        await _validatedControllerInput(controllerSnapshot) == null) {
      return const LaunchNotStarted(LaunchControllerReviewRequired());
    }
    return runtime.adapterFactory().launch(
      EmulatorLaunchPlan(
        target: target,
        profile: profile,
        dependencies: dependencies,
        controllerSetup: controllerSetup,
      ),
    );
  }

  /// Resolves session slot claims plus the currently connected pads into
  /// player slots, then independently resolves each connected player's
  /// shortcuts and optional device-global physical gameplay mapping. Merely
  /// observing a controller never creates mapping data.
  ///
  /// Controller preferences, seating, and each player's mapping must never
  /// block a launch. One mapping failure falls back safely without discarding
  /// other players' mappings or the resolved seating.
  Future<LaunchControllerSetup> _resolveControllerSetup(
    ResolvedPlayTarget target, {
    required RuntimeProfile profile,
    required List<ResolvedRuntimeDependency> dependencies,
    required _ValidatedControllerInput controllerInput,
  }) async {
    final slotResolution = controllerInput.slotResolution;
    ControllerPreferences preferences;
    try {
      preferences =
          await _controllerPreferences?.load() ??
          ControllerPreferences.defaults;
    } on Object {
      preferences = ControllerPreferences.defaults;
    }

    final templateId = preferences.templateId;
    final template = templateId == null
        ? BuiltinControllerTemplates.generic
        : BuiltinControllerTemplates.byId(templateId) ??
              BuiltinControllerTemplates.generic;
    final fallbackMapping = _safeTemplateMapping(template);
    final managedExecutable = _hasManagedExecutable(profile, dependencies);

    final playerControllers = <LaunchPlayerController>[];
    for (final slot in slotResolution.controllerSlots) {
      final assignedProfileId =
          slot.playerSlot == 0 ||
              slot.playerSlot >= controllerInput.claims.length
          ? null
          : controllerInput.claims[slot.playerSlot]?.localProfileId;
      ControllerMapping mapping;
      try {
        mapping = await _controllerMappings.resolve(
          template: template,
          titleId: target.titleId,
          localProfileId: assignedProfileId ?? target.localProfileId,
          fallbackLocalProfileId: assignedProfileId == null
              ? null
              : target.localProfileId,
          sdlGuid: slot.controller.identity.sdlGuid,
        );
      } on Object {
        mapping = fallbackMapping;
      }
      ControllerHardwareMapping? gameplayMapping;
      final sdlGuid = slot.controller.identity.sdlGuid;
      if (sdlGuid != null && sdlGuid.isNotEmpty) {
        try {
          gameplayMapping = await _controllerHardwareMappings?.find(
            sdlPlatform: _sdlPlatformForOperatingSystem(_operatingSystem),
            sdlGuid: sdlGuid,
          );
        } on Object {
          // One malformed/unavailable hardware mapping must not discard the
          // player's shortcuts, seating, or any other controller's mapping.
          gameplayMapping = null;
        }
      }
      playerControllers.add(
        LaunchPlayerController(
          resolvedSlot: slot,
          inputReference: _runtimeInputReference(
            profile: profile,
            slot: slot,
            managedExecutable: managedExecutable,
            controllerInput: controllerInput,
            gameplayMapping: gameplayMapping,
          ),
          mapping: mapping,
          templateId: template.id,
          capabilities: template.capabilities,
          gameplayMapping: gameplayMapping,
        ),
      );
    }

    // With no listed controllers, preserve the existing no-SDL/no-claim path:
    // legacy title/global rules can still define the runtime's default P1
    // shortcuts. This is not a player entry and never reads profile/GUID rules.
    var unresolvedMapping = fallbackMapping;
    if (playerControllers.isEmpty &&
        slotResolution.availableControllers.isEmpty &&
        slotResolution.reservedPlayerSlots.isEmpty &&
        slotResolution.blockedPlayerSlots.isEmpty) {
      try {
        unresolvedMapping = await _controllerMappings.resolve(
          template: template,
          titleId: target.titleId,
          localProfileId: target.localProfileId,
        );
      } on Object {
        unresolvedMapping = fallbackMapping;
      }
    }

    return LaunchControllerSetup(
      mapping: unresolvedMapping,
      playerControllers: playerControllers,
      reservedPlayerSlots: slotResolution.reservedPlayerSlots,
      blockedPlayerSlots: slotResolution.blockedPlayerSlots,
    );
  }

  Future<_ValidatedControllerInput?> _validatedControllerInput(
    ReviewedLaunchSnapshot? snapshot,
  ) async {
    final claims = _controllerSlotClaims;
    if (snapshot != null) {
      if (claims == null || claims.revision != snapshot.claimRevision) {
        return null;
      }
      try {
        final devices = await _gamepadLister();
        if (!snapshot.inventoryAvailable) {
          return null;
        }
        return snapshot.matches(
              currentClaimRevision: claims.revision,
              currentDevices: devices,
            )
            ? _ValidatedControllerInput.reviewed(snapshot)
            : null;
      } on Object {
        return !snapshot.inventoryAvailable &&
                claims.revision == snapshot.claimRevision
            ? _ValidatedControllerInput.reviewed(snapshot)
            : null;
      }
    }

    if (claims == null) {
      return const _ValidatedControllerInput.live(
        ControllerSlotResolution.empty(),
        <ControllerSlotClaim?>[],
      );
    }
    try {
      final loadedClaims = await claims.loadClaims();
      return _ValidatedControllerInput.live(
        ControllerAssignments.resolveSlotResolution(
          claims: loadedClaims,
          devices: await _gamepadLister(),
        ),
        loadedClaims,
      );
    } on Object {
      return const _ValidatedControllerInput.live(
        ControllerSlotResolution.empty(),
        <ControllerSlotClaim?>[],
      );
    }
  }

  RuntimeControllerInputReference _runtimeInputReference({
    required RuntimeProfile profile,
    required ResolvedControllerSlot slot,
    required bool managedExecutable,
    required _ValidatedControllerInput controllerInput,
    required ControllerHardwareMapping? gameplayMapping,
  }) {
    final provider = slot.controller.identity.sdlGuid == null
        ? RuntimeControllerInputProvider.other
        : RuntimeControllerInputProvider.sdl3;
    final uncorrelated = RuntimeControllerInputReference.uncorrelated(
      provider: provider,
      providerDeviceId: slot.controller.id,
      providerOrdinal: slot.runtimeIndex,
    );
    final snapshot = controllerInput.reviewedSnapshot;
    if (_operatingSystem != 'macos' ||
        snapshot == null ||
        !snapshot.inventoryAvailable ||
        snapshot.devices.length != 1 ||
        snapshot.devices.single != slot.controller ||
        provider != RuntimeControllerInputProvider.sdl3 ||
        !slot.controller.identity.hasExactIdentity) {
      return uncorrelated;
    }

    if (profile.adapterId == BuiltinRuntimeProfiles.retroArchAdapterId) {
      final calibratedShortcuts =
          managedExecutable &&
          slot.controller.identity.displayName ==
              RuntimeControllerInputPolicies
                  .retroArchMfi8BitDoPro2UsbDisplayName &&
          slot.controller.identity.sdlGuid?.toLowerCase() ==
              RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid;
      final calibratedGameplay =
          calibratedShortcuts &&
          _matchesGameplayMapping(
            gameplayMapping,
            slot.controller.identity,
            operatingSystem: _operatingSystem,
          );
      return RuntimeControllerInputReference(
        provider: provider,
        providerDeviceId: slot.controller.id,
        providerOrdinal: slot.runtimeIndex,
        runtimeReference: 0,
        correlation: RuntimeControllerInputCorrelation.verified,
        adapterPolicyId: calibratedGameplay
            ? RuntimeControllerInputPolicies
                  .retroArchMacosSingleControllerMfi8BitDoPro2UsbGameplayV1
            : calibratedShortcuts
            ? RuntimeControllerInputPolicies
                  .retroArchMacosSingleControllerMfi8BitDoPro2Usb
            : RuntimeControllerInputPolicies.retroArchMacosSingleController,
        controllerShortcutsAllowed: calibratedShortcuts,
        controllerGameplayAllowed: calibratedGameplay,
      );
    }
    if (profile.adapterId == BuiltinRuntimeProfiles.duckStationAdapterId) {
      return RuntimeControllerInputReference(
        provider: provider,
        providerDeviceId: slot.controller.id,
        providerOrdinal: slot.runtimeIndex,
        runtimeReference: 0,
        correlation: RuntimeControllerInputCorrelation.verified,
        adapterPolicyId:
            RuntimeControllerInputPolicies.duckStationMacosSingleController,
        controllerShortcutsAllowed: managedExecutable,
      );
    }
    if (profile.adapterId == BuiltinRuntimeProfiles.dolphinAdapterId &&
        managedExecutable &&
        _matchesGameplayMapping(
          gameplayMapping,
          slot.controller.identity,
          operatingSystem: _operatingSystem,
        )) {
      return RuntimeControllerInputReference(
        provider: provider,
        providerDeviceId: slot.controller.id,
        providerOrdinal: slot.runtimeIndex,
        runtimeReference: 0,
        correlation: RuntimeControllerInputCorrelation.verified,
        adapterPolicyId: RuntimeControllerInputPolicies
            .dolphinMacos2606SingleControllerSdl3GameplayV1,
        controllerShortcutsAllowed: true,
        controllerGameplayAllowed: true,
      );
    }
    return uncorrelated;
  }

  static ControllerMapping _safeTemplateMapping(ControllerTemplate template) =>
      BuiltinControllerTemplates.defaultMapping
          .filteredFor(template.capabilities)
          .overlaidWith(template.overrides.filteredFor(template.capabilities));

  static bool _hasManagedExecutable(
    RuntimeProfile profile,
    List<ResolvedRuntimeDependency> dependencies,
  ) {
    final requirementId = profile.executableRequirement?.id;
    if (requirementId == null) {
      return false;
    }
    final executableDependencies = dependencies
        .where((dependency) => dependency.requirementId == requirementId)
        .toList(growable: false);
    return executableDependencies.length == 1 &&
        executableDependencies.single.provenance ==
            RuntimeDependencyProvenance.managed;
  }

  static String _sdlPlatformForOperatingSystem(String operatingSystem) =>
      switch (operatingSystem) {
        'macos' => 'macOS',
        'windows' => 'Windows',
        'linux' => 'Linux',
        _ => operatingSystem,
      };

  static bool _matchesGameplayMapping(
    ControllerHardwareMapping? mapping,
    ControllerIdentity identity, {
    required String operatingSystem,
  }) =>
      mapping != null &&
      mapping.sdlPlatform == _sdlPlatformForOperatingSystem(operatingSystem) &&
      mapping.sdlGuid.toLowerCase() == identity.sdlGuid?.toLowerCase();
}

final class _ValidatedControllerInput {
  const _ValidatedControllerInput.live(this.slotResolution, this.claims)
    : reviewedSnapshot = null;

  _ValidatedControllerInput.reviewed(ReviewedLaunchSnapshot snapshot)
    : slotResolution = snapshot.slotResolution,
      claims = snapshot.claims,
      reviewedSnapshot = snapshot;

  final ControllerSlotResolution slotResolution;
  final List<ControllerSlotClaim?> claims;
  final ReviewedLaunchSnapshot? reviewedSnapshot;
}
