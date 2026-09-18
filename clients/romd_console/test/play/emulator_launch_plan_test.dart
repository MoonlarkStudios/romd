import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

const _target = ResolvedPlayTarget(
  releaseId: 'rel-1',
  titleId: 'title-1',
  platformShortName: 'snes',
  displayName: 'Chrono Trigger',
  localProfileId: 'profile-1',
  contentRoot: '/content',
  launchAbsolutePath: '/content/chrono.sfc',
  saveRoot: '/saves',
  stateRoot: '/states',
  configRoot: '/config',
);

const _profile = RuntimeProfile(
  id: RuntimeProfileId('retroarch:snes:snes9x'),
  adapterId: RuntimeAdapterId('retroarch'),
  displayName: 'RetroArch (Snes9x)',
  supportedPlatforms: <String>{'snes'},
  requirements: <RuntimeDependencyRequirement>[],
);

ResolvedControllerSlot _slot({
  required int playerSlot,
  required int runtimeIndex,
}) => ResolvedControllerSlot(
  playerSlot: playerSlot,
  controller: ConnectedGamepad.fallback(
    id: 'pad-$runtimeIndex',
    name: 'Pad $runtimeIndex',
    order: runtimeIndex,
  ),
  runtimeIndex: runtimeIndex,
);

void main() {
  test('runtime input references separate provider order from runtime ids', () {
    final first = RuntimeControllerInputReference.uncorrelated(
      provider: RuntimeControllerInputProvider.sdl3,
      providerDeviceId: 'sdl-instance-9',
      providerOrdinal: 2,
    );
    final equal = RuntimeControllerInputReference.uncorrelated(
      provider: RuntimeControllerInputProvider.sdl3,
      providerDeviceId: 'sdl-instance-9',
      providerOrdinal: 2,
    );
    const verified = RuntimeControllerInputReference(
      provider: RuntimeControllerInputProvider.sdl3,
      providerDeviceId: 'sdl-instance-9',
      providerOrdinal: 2,
      correlation: RuntimeControllerInputCorrelation.verified,
      runtimeReference: 7,
      adapterPolicyId: 'test.runtime.v1',
    );

    expect(first, equal);
    expect(first.hashCode, equal.hashCode);
    expect(first.approvedRuntimeIndex, isNull);
    expect(verified.approvedRuntimeIndex, 7);
    expect(verified.controllerShortcutsAllowed, isFalse);
    expect(verified.controllerGameplayAllowed, isFalse);
    expect(verified.canEmitControllerShortcuts, isFalse);
    expect(verified.canEmitControllerGameplay, isFalse);
    expect(verified, isNot(first));
  });

  test('shortcut authorization participates in reference identity', () {
    const disallowed = RuntimeControllerInputReference(
      provider: RuntimeControllerInputProvider.sdl3,
      providerDeviceId: 'pad',
      providerOrdinal: 0,
      correlation: RuntimeControllerInputCorrelation.verified,
      runtimeReference: 0,
      adapterPolicyId: 'test-policy',
    );
    const allowed = RuntimeControllerInputReference(
      provider: RuntimeControllerInputProvider.sdl3,
      providerDeviceId: 'pad',
      providerOrdinal: 0,
      correlation: RuntimeControllerInputCorrelation.verified,
      runtimeReference: 0,
      adapterPolicyId: 'test-policy',
      controllerShortcutsAllowed: true,
    );

    expect(allowed, isNot(disallowed));
    expect(allowed.hashCode, isNot(disallowed.hashCode));
    expect(allowed.diagnosticFields['controllerShortcutsAllowed'], isTrue);
    expect(allowed.canEmitControllerShortcuts, isTrue);
    expect(allowed.canEmitControllerGameplay, isFalse);
  });

  test(
    'gameplay authorization is independent and participates in identity',
    () {
      const shortcutsOnly = RuntimeControllerInputReference(
        provider: RuntimeControllerInputProvider.sdl3,
        providerDeviceId: 'pad',
        providerOrdinal: 0,
        correlation: RuntimeControllerInputCorrelation.verified,
        runtimeReference: 0,
        adapterPolicyId: 'test-policy',
        controllerShortcutsAllowed: true,
      );
      const gameplayOnly = RuntimeControllerInputReference(
        provider: RuntimeControllerInputProvider.sdl3,
        providerDeviceId: 'pad',
        providerOrdinal: 0,
        correlation: RuntimeControllerInputCorrelation.verified,
        runtimeReference: 0,
        adapterPolicyId: 'test-policy',
        controllerGameplayAllowed: true,
      );

      expect(gameplayOnly, isNot(shortcutsOnly));
      expect(gameplayOnly.hashCode, isNot(shortcutsOnly.hashCode));
      expect(
        gameplayOnly.diagnosticFields['controllerGameplayAllowed'],
        isTrue,
      );
      expect(gameplayOnly.canEmitControllerGameplay, isTrue);
      expect(gameplayOnly.canEmitControllerShortcuts, isFalse);
    },
  );

  test('empty adapter policy cannot authorize a runtime reference', () {
    expect(
      () => RuntimeControllerInputReference(
        provider: RuntimeControllerInputProvider.sdl3,
        providerDeviceId: 'pad',
        providerOrdinal: 0,
        correlation: RuntimeControllerInputCorrelation.verified,
        runtimeReference: 0,
        adapterPolicyId: '',
      ),
      throwsAssertionError,
    );
  });

  test('uncorrelated input cannot authorize gameplay emission', () {
    expect(
      () => RuntimeControllerInputReference(
        provider: RuntimeControllerInputProvider.sdl3,
        providerDeviceId: 'pad',
        providerOrdinal: 0,
        correlation: RuntimeControllerInputCorrelation.uncorrelated,
        controllerGameplayAllowed: true,
      ),
      throwsAssertionError,
    );
  });

  test('blank adapter policy fails closed at runtime', () {
    const reference = RuntimeControllerInputReference(
      provider: RuntimeControllerInputProvider.sdl3,
      providerDeviceId: 'pad',
      providerOrdinal: 0,
      correlation: RuntimeControllerInputCorrelation.verified,
      runtimeReference: 0,
      adapterPolicyId: '   ',
      controllerShortcutsAllowed: true,
      controllerGameplayAllowed: true,
    );

    expect(reference.canForceRuntimeReference, isFalse);
    expect(reference.approvedRuntimeIndex, isNull);
    expect(reference.canEmitControllerShortcuts, isFalse);
    expect(reference.canEmitControllerGameplay, isFalse);
  });

  test('controllerSetup is the source for compatibility getters', () {
    final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
      const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
        RomdAction.saveState: GamepadButtonPosition.faceEast,
      }),
    );
    final setup = LaunchControllerSetup(
      mapping: mapping,
      controllerSlots: <ResolvedControllerSlot>[
        _slot(playerSlot: 0, runtimeIndex: 1),
        _slot(playerSlot: 1, runtimeIndex: 0),
      ],
      reservedPlayerSlots: const <int>{2},
    );

    final plan = EmulatorLaunchPlan(
      target: _target,
      profile: _profile,
      dependencies: const <ResolvedRuntimeDependency>[],
      controllerSetup: setup,
    );

    expect(plan.controllerSetup, same(setup));
    expect(plan.playerControllers, setup.playerControllers);
    expect(plan.controllerMapping, same(mapping));
    expect(plan.controllerSlots, setup.controllerSlots);
    expect(plan.reservedPlayerSlots, <int>{2});
    expect(plan.blockedPlayerSlots, isEmpty);
    expect(plan.padDeviceIndices, <int>[1, 0]);
    expect(
      setup.playerControllers.first.inputReference.diagnosticFields,
      <String, Object?>{
        'provider': 'other',
        'providerDeviceId': 'pad-1',
        'providerOrdinal': 1,
        'runtimeReference': null,
        'correlation': 'uncorrelated',
        'adapterPolicyId': null,
        'controllerShortcutsAllowed': false,
        'controllerGameplayAllowed': false,
      },
    );
    expect(setup.isPlayerSlotReserved(2), isTrue);
    expect(setup.isPlayerSlotBlocked(2), isTrue);
  });

  test('legacy constructor arguments build the same setup shape', () {
    final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
      const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
        RomdAction.menu: GamepadButtonPosition.faceSouth,
      }),
    );
    final slots = <ResolvedControllerSlot>[
      _slot(playerSlot: 0, runtimeIndex: 2),
    ];

    final plan = EmulatorLaunchPlan(
      target: _target,
      profile: _profile,
      dependencies: const <ResolvedRuntimeDependency>[],
      controllerMapping: mapping,
      controllerSlots: slots,
      reservedPlayerSlots: const <int>{0},
      blockedPlayerSlots: const <int>{1},
    );

    expect(plan.controllerSetup.mapping, same(mapping));
    expect(plan.controllerSetup.controllerSlots, slots);
    expect(plan.controllerSetup.reservedPlayerSlots, <int>{0});
    expect(plan.controllerSetup.blockedPlayerSlots, <int>{1});
    expect(plan.controllerSetup.isPlayerSlotBlocked(1), isTrue);
    expect(plan.padDeviceIndices, <int>[2]);
  });
}
