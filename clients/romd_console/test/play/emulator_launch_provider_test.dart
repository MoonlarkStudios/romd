import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_binding_rules.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping_resolver.dart';
import 'package:romd_console/src/play/controllers/domain/controller_preferences.dart';
import 'package:romd_console/src/play/controllers/domain/controller_profile_mappings.dart';
import 'package:romd_console/src/play/controllers/domain/reviewed_launch_snapshot.dart';
import 'package:romd_console/src/play/emulator/data/emulator_launch_provider.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_adapter.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency_resolver.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

final class _FakeAdapter implements RuntimeAdapter {
  _FakeAdapter(this.result);

  _FakeAdapter.started(LaunchResult result)
    : result = LaunchStarted(_FakeLaunchSession(result));

  final LaunchStartResult result;
  final List<EmulatorLaunchPlan> plans = <EmulatorLaunchPlan>[];

  @override
  Future<void> prepare(EmulatorLaunchPlan plan) async {}

  @override
  CommandPlan buildCommand(EmulatorLaunchPlan plan, String executablePath) =>
      CommandPlan(
        executable: executablePath,
        arguments: const <String>[],
        workingDirectory: '',
        environment: const <String, String>{},
      );

  @override
  Future<LaunchStartResult> launch(EmulatorLaunchPlan plan) async {
    plans.add(plan);
    return result;
  }
}

final class _FakeLaunchSession implements LaunchSession {
  const _FakeLaunchSession(this.result);

  final LaunchResult result;

  @override
  Future<LaunchResult> get completed async => result;

  @override
  LaunchTermination? get termination => null;

  @override
  LaunchForegroundControl? get foreground => null;
}

final class _RecordingDependencyResolver implements RuntimeDependencyResolver {
  final List<RuntimeProfile> profiles = <RuntimeProfile>[];

  @override
  Stream<DependencyProgress> resolve(
    RuntimeProfile profile,
    ResolvedPlayTarget target,
  ) {
    profiles.add(profile);
    return Stream<DependencyProgress>.value(
      const DependenciesReady(<ResolvedRuntimeDependency>[]),
    );
  }
}

final class _StubBindingRules implements ControllerBindingRuleRepository {
  _StubBindingRules(this.rules);
  final List<ControllerBindingRule> rules;

  @override
  Future<void> setBinding({
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  }) => throw UnimplementedError('provider never writes');

  @override
  Future<List<ControllerBindingRule>> bindingsMatching({
    required String titleId,
  }) async => rules
      .where(
        (rule) =>
            rule.scope == ControllerBindingScope.global ||
            rule.scopeValue == titleId,
      )
      .toList(growable: false);
}

final class _ThrowingPreferences implements ControllerPreferencesRepository {
  @override
  Future<ControllerPreferences> load() => throw StateError('store gone');

  @override
  Future<void> saveTemplate(ControllerTemplateId templateId) =>
      throw UnimplementedError('provider never writes');
}

final class _ThrowingBindingRules implements ControllerBindingRuleRepository {
  @override
  Future<void> setBinding({
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  }) => throw UnimplementedError('provider never writes');

  @override
  Future<List<ControllerBindingRule>> bindingsMatching({
    required String titleId,
  }) => throw StateError('rule store unavailable');
}

final class _StubProfileBindingRules
    implements ControllerProfileMappingRepository {
  _StubProfileBindingRules(this.rules);

  final List<ControllerBindingRule> rules;

  @override
  Future<void> saveProfile({
    required String localProfileId,
    required String sdlGuid,
    required String displayName,
    required ControllerTemplateId? templateId,
  }) => throw UnimplementedError('provider never writes');

  @override
  Future<ControllerMappingProfile?> findProfile({
    required String localProfileId,
    required String sdlGuid,
  }) => throw UnimplementedError('provider never reads profiles');

  @override
  Future<void> setBinding({
    required String localProfileId,
    required String sdlGuid,
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  }) => throw UnimplementedError('provider never writes');

  @override
  Future<List<ControllerBindingRule>> bindingsMatching({
    required String localProfileId,
    required String sdlGuid,
    required String titleId,
  }) async => rules
      .where(
        (rule) =>
            rule.scope == ControllerBindingScope.global ||
            rule.scopeValue == titleId,
      )
      .toList(growable: false);
}

final class _ThrowingProfileBindingRules
    implements ControllerProfileMappingRepository {
  @override
  Future<void> saveProfile({
    required String localProfileId,
    required String sdlGuid,
    required String displayName,
    required ControllerTemplateId? templateId,
  }) => throw UnimplementedError('provider never writes');

  @override
  Future<ControllerMappingProfile?> findProfile({
    required String localProfileId,
    required String sdlGuid,
  }) => throw UnimplementedError('provider never reads profiles');

  @override
  Future<void> setBinding({
    required String localProfileId,
    required String sdlGuid,
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  }) => throw UnimplementedError('provider never writes');

  @override
  Future<List<ControllerBindingRule>> bindingsMatching({
    required String localProfileId,
    required String sdlGuid,
    required String titleId,
  }) => throw StateError('profile rules should not be read');
}

final class _PerGuidProfileBindingRules
    implements ControllerProfileMappingRepository {
  _PerGuidProfileBindingRules(this.rulesByGuid, {this.throwingGuid});

  final Map<String, List<ControllerBindingRule>> rulesByGuid;
  final String? throwingGuid;
  final List<String> requestedGuids = <String>[];

  @override
  Future<void> saveProfile({
    required String localProfileId,
    required String sdlGuid,
    required String displayName,
    required ControllerTemplateId? templateId,
  }) => throw UnimplementedError('provider never writes');

  @override
  Future<ControllerMappingProfile?> findProfile({
    required String localProfileId,
    required String sdlGuid,
  }) => throw UnimplementedError('provider never reads profiles');

  @override
  Future<void> setBinding({
    required String localProfileId,
    required String sdlGuid,
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  }) => throw UnimplementedError('provider never writes');

  @override
  Future<List<ControllerBindingRule>> bindingsMatching({
    required String localProfileId,
    required String sdlGuid,
    required String titleId,
  }) async {
    requestedGuids.add(sdlGuid);
    if (sdlGuid == throwingGuid) {
      throw StateError('mapping unavailable for $sdlGuid');
    }
    return rulesByGuid[sdlGuid] ?? const <ControllerBindingRule>[];
  }
}

final class _PerProfileGuidBindingRules
    implements ControllerProfileMappingRepository {
  _PerProfileGuidBindingRules(this.rules);

  final Map<(String, String), List<ControllerBindingRule>> rules;
  final List<(String, String)> requests = <(String, String)>[];

  @override
  Future<void> saveProfile({
    required String localProfileId,
    required String sdlGuid,
    required String displayName,
    required ControllerTemplateId? templateId,
  }) => throw UnimplementedError('provider never writes');

  @override
  Future<ControllerMappingProfile?> findProfile({
    required String localProfileId,
    required String sdlGuid,
  }) => throw UnimplementedError('provider never reads profiles');

  @override
  Future<void> setBinding({
    required String localProfileId,
    required String sdlGuid,
    required ControllerBindingScope scope,
    required String scopeValue,
    required RomdAction action,
    required GamepadButtonPosition? button,
  }) => throw UnimplementedError('provider never writes');

  @override
  Future<List<ControllerBindingRule>> bindingsMatching({
    required String localProfileId,
    required String sdlGuid,
    required String titleId,
  }) async {
    requests.add((localProfileId, sdlGuid));
    return rules[(localProfileId, sdlGuid)] ?? const <ControllerBindingRule>[];
  }
}

final class _HardwareMappings implements ControllerHardwareMappingRepository {
  _HardwareMappings(this.mappings, {this.throwingGuids = const <String>{}});

  final Map<(String, String), ControllerHardwareMapping> mappings;
  final Set<String> throwingGuids;
  final List<(String, String)> requests = <(String, String)>[];

  @override
  Future<ControllerHardwareMapping?> find({
    required String sdlPlatform,
    required String sdlGuid,
  }) async {
    requests.add((sdlPlatform, sdlGuid));
    if (throwingGuids.contains(sdlGuid)) {
      throw StateError('hardware mapping unavailable');
    }
    return mappings[(sdlPlatform, sdlGuid)];
  }

  @override
  Future<void> reset({required String sdlPlatform, required String sdlGuid}) =>
      throw UnimplementedError('provider never writes');

  @override
  Future<void> save({
    required String sdlPlatform,
    required String sdlGuid,
    required String displayName,
    required CanonicalControllerMapping mapping,
  }) => throw UnimplementedError('provider never writes');
}

const _adapterId = RuntimeAdapterId('retroarch');

const _target = ResolvedPlayTarget(
  releaseId: 'rel-1',
  titleId: 'title-1',
  platformShortName: 'snes',
  displayName: 'Chrono Trigger',
  localProfileId: 'profile-1',
  contentRoot: '/c',
  launchAbsolutePath: '/c/chrono.sfc',
  saveRoot: '/s',
  stateRoot: '/st',
  configRoot: '/cfg',
);

ResolvedPlayTarget _targetForProfile(String localProfileId) =>
    ResolvedPlayTarget(
      releaseId: _target.releaseId,
      titleId: _target.titleId,
      platformShortName: _target.platformShortName,
      displayName: _target.displayName,
      localProfileId: localProfileId,
      contentRoot: _target.contentRoot,
      launchAbsolutePath: _target.launchAbsolutePath,
      saveRoot: '/profiles/$localProfileId/games/snes/title-1/saves',
      stateRoot: '/profiles/$localProfileId/games/snes/title-1/states',
      configRoot: _target.configRoot,
    );

const _profile = RuntimeProfile(
  id: RuntimeProfileId('retroarch:snes:snes9x'),
  adapterId: _adapterId,
  displayName: 'RetroArch (Snes9x)',
  supportedPlatforms: <String>{'snes'},
  requirements: <RuntimeDependencyRequirement>[],
);

const _exactPad = ConnectedGamepad(
  id: 'exact-pad',
  order: 7,
  identity: ControllerIdentity(
    displayName:
        RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbDisplayName,
    sdlGuid: RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
    serial: 'pro2-serial',
  ),
);

const _genericExactPad = ConnectedGamepad(
  id: 'generic-exact-pad',
  order: 0,
  identity: ControllerIdentity(
    displayName: 'Other Exact Pad',
    sdlGuid: 'generic-exact-guid',
    serial: 'generic-exact-serial',
  ),
);

RuntimeProfile _profileFor(RuntimeAdapterId adapterId) => RuntimeProfile(
  id: RuntimeProfileId('${adapterId.value}:test'),
  adapterId: adapterId,
  displayName: 'Test runtime',
  supportedPlatforms: const <String>{'snes'},
  requirements: const <RuntimeDependencyRequirement>[
    ExecutableRequirement(id: 'executable', displayName: 'Test runtime'),
  ],
);

Future<EmulatorLaunchPlan> _launchReviewed({
  required RuntimeProfile profile,
  required SessionControllerSlotClaims claims,
  required List<ConnectedGamepad> devices,
  ControllerHardwareMappingRepository? hardwareMappings,
  String operatingSystem = 'macos',
  RuntimeDependencyProvenance executableProvenance =
      RuntimeDependencyProvenance.managed,
  List<RuntimeDependencyProvenance>? executableProvenances,
}) async {
  final adapter = _FakeAdapter.started(const LaunchExited(0));
  if (claims.claimDetails.every((claim) => claim == null) &&
      devices.isNotEmpty) {
    await claims.replaceAllClaims(<ControllerSlotClaim?>[
      for (final device in devices) ControllerSlotClaim.fromGamepad(device),
    ]);
  }
  final snapshot = ReviewedLaunchSnapshot.resolve(
    claimRevision: claims.revision,
    claims: claims.claimDetails,
    devices: devices,
  );
  final provider = EmulatorLaunchProvider(
    runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
      profile.adapterId: EmulatorRuntimeLaunchEntry(
        dependencyResolver: _RecordingDependencyResolver(),
        adapterFactory: () => adapter,
      ),
    },
    controllerHardwareMappings: hardwareMappings,
    controllerSlotClaims: claims,
    gamepadLister: () async => devices,
    operatingSystem: operatingSystem,
  );

  final result = await provider.launch(
    profile: profile,
    target: _target,
    dependencies: <ResolvedRuntimeDependency>[
      for (final (index, provenance)
          in (executableProvenances ??
                  <RuntimeDependencyProvenance>[executableProvenance])
              .indexed)
        ResolvedRuntimeDependency(
          requirementId: profile.executableRequirement!.id,
          path: '/runtime-$index/TestRuntime.app',
          provenance: provenance,
        ),
    ],
    controllerSnapshot: snapshot,
  );
  if (result is! LaunchStarted) {
    throw StateError('Expected launch to start, got $result');
  }
  return adapter.plans.single;
}

void main() {
  test(
    'prepare delegates to the matching runtime dependency resolver',
    () async {
      final dependencyResolver = _RecordingDependencyResolver();
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          _adapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: dependencyResolver,
            adapterFactory: () => _FakeAdapter.started(const LaunchExited(0)),
          ),
        },
      );

      final events = await provider.prepare(_profile, _target).toList();

      expect(events.single, isA<DependenciesReady>());
      expect(dependencyResolver.profiles, <RuntimeProfile>[_profile]);
    },
  );

  test(
    'launch delegates to the matching adapter and returns its result',
    () async {
      final adapter = _FakeAdapter.started(const LaunchExited(0));
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          _adapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _RecordingDependencyResolver(),
            adapterFactory: () => adapter,
          ),
        },
      );

      final result = await provider.launch(
        profile: _profile,
        target: _target,
        dependencies: const <ResolvedRuntimeDependency>[],
      );

      expect(result, isA<LaunchStarted>());
      expect(
        await (result as LaunchStarted).session.completed,
        isA<LaunchExited>(),
      );
      final plan = adapter.plans.single;
      expect(plan.target, _target);
      expect(plan.profile, _profile);
      expect(plan.dependencies, isEmpty);
    },
  );

  test('single exact reviewed SDL pad gets each runtime policy', () async {
    for (final testCase
        in <
          ({RuntimeAdapterId adapterId, String policyId, bool shortcutsAllowed})
        >[
          (
            adapterId: BuiltinRuntimeProfiles.retroArchAdapterId,
            policyId: RuntimeControllerInputPolicies
                .retroArchMacosSingleControllerMfi8BitDoPro2Usb,
            shortcutsAllowed: true,
          ),
          (
            adapterId: BuiltinRuntimeProfiles.duckStationAdapterId,
            policyId:
                RuntimeControllerInputPolicies.duckStationMacosSingleController,
            shortcutsAllowed: true,
          ),
        ]) {
      final plan = await _launchReviewed(
        profile: _profileFor(testCase.adapterId),
        claims: SessionControllerSlotClaims(),
        devices: const <ConnectedGamepad>[_exactPad],
      );

      final reference = plan.playerControllers.single.inputReference;
      expect(reference.diagnosticFields, <String, Object?>{
        'provider': 'sdl3',
        'providerDeviceId': 'exact-pad',
        'providerOrdinal': 7,
        'runtimeReference': 0,
        'correlation': 'verified',
        'adapterPolicyId': testCase.policyId,
        'controllerShortcutsAllowed': testCase.shortcutsAllowed,
        'controllerGameplayAllowed': false,
      });
      expect(reference.canForceRuntimeReference, isTrue);
      expect(reference.approvedRuntimeIndex, 0);
      expect(reference.canEmitControllerShortcuts, testCase.shortcutsAllowed);
      expect(reference.canEmitControllerGameplay, isFalse);
    }
  });

  test(
    'matching saved mapping authorizes the stronger RetroArch gameplay policy',
    () async {
      final now = DateTime.utc(2026, 7, 13);
      final saved = ControllerHardwareMapping(
        sdlPlatform: 'macOS',
        sdlGuid:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
        displayName:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbDisplayName,
        mapping: CanonicalControllerMapping(
          const <CanonicalGamepadControl, RawGamepadInput>{
            CanonicalGamepadControl.faceSouth: RawButtonInput(0),
          },
        ),
        createdAt: now,
        updatedAt: now,
      );
      final plan = await _launchReviewed(
        profile: _profileFor(BuiltinRuntimeProfiles.retroArchAdapterId),
        claims: SessionControllerSlotClaims(),
        devices: const <ConnectedGamepad>[_exactPad],
        hardwareMappings:
            _HardwareMappings(<(String, String), ControllerHardwareMapping>{
              (
                'macOS',
                RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
              ): saved,
            }),
      );

      final player = plan.playerControllers.single;
      final reference = player.inputReference;
      expect(player.gameplayMapping, same(saved));
      expect(
        reference.adapterPolicyId,
        RuntimeControllerInputPolicies
            .retroArchMacosSingleControllerMfi8BitDoPro2UsbGameplayV1,
      );
      expect(reference.approvedRuntimeIndex, 0);
      expect(reference.controllerShortcutsAllowed, isTrue);
      expect(reference.controllerGameplayAllowed, isTrue);
      expect(reference.canEmitControllerShortcuts, isTrue);
      expect(reference.canEmitControllerGameplay, isTrue);
    },
  );

  test(
    'mismatched saved mapping cannot authorize RetroArch gameplay',
    () async {
      final now = DateTime.utc(2026, 7, 13);
      final mismatched = ControllerHardwareMapping(
        sdlPlatform: 'macOS',
        sdlGuid: 'different-guid',
        displayName:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbDisplayName,
        mapping: CanonicalControllerMapping(
          const <CanonicalGamepadControl, RawGamepadInput>{
            CanonicalGamepadControl.faceSouth: RawButtonInput(0),
          },
        ),
        createdAt: now,
        updatedAt: now,
      );
      final plan = await _launchReviewed(
        profile: _profileFor(BuiltinRuntimeProfiles.retroArchAdapterId),
        claims: SessionControllerSlotClaims(),
        devices: const <ConnectedGamepad>[_exactPad],
        hardwareMappings:
            _HardwareMappings(<(String, String), ControllerHardwareMapping>{
              (
                'macOS',
                RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
              ): mismatched,
            }),
      );

      final reference = plan.playerControllers.single.inputReference;
      expect(
        reference.adapterPolicyId,
        RuntimeControllerInputPolicies
            .retroArchMacosSingleControllerMfi8BitDoPro2Usb,
      );
      expect(reference.canEmitControllerShortcuts, isTrue);
      expect(reference.canEmitControllerGameplay, isFalse);
    },
  );

  test(
    'managed Dolphin 2606 authorizes matching singleton SDL gameplay',
    () async {
      final now = DateTime.utc(2026, 7, 14);
      final saved = ControllerHardwareMapping(
        sdlPlatform: 'macOS',
        sdlGuid:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
        displayName:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbDisplayName,
        mapping: CanonicalControllerMapping(
          const <CanonicalGamepadControl, RawGamepadInput>{
            CanonicalGamepadControl.faceSouth: RawButtonInput(1),
          },
        ),
        createdAt: now,
        updatedAt: now,
      );
      final mappings =
          _HardwareMappings(<(String, String), ControllerHardwareMapping>{
            (
              'macOS',
              RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
            ): saved,
          });

      final plan = await _launchReviewed(
        profile: _profileFor(BuiltinRuntimeProfiles.dolphinAdapterId),
        claims: SessionControllerSlotClaims(),
        devices: const <ConnectedGamepad>[_exactPad],
        hardwareMappings: mappings,
      );

      final player = plan.playerControllers.single;
      expect(player.gameplayMapping, same(saved));
      expect(
        player.inputReference.adapterPolicyId,
        RuntimeControllerInputPolicies
            .dolphinMacos2606SingleControllerSdl3GameplayV1,
      );
      expect(player.inputReference.approvedRuntimeIndex, 0);
      expect(player.inputReference.canEmitControllerGameplay, isTrue);
      expect(player.inputReference.canEmitControllerShortcuts, isTrue);
    },
  );

  test(
    'external Dolphin remains uncorrelated despite a matching map',
    () async {
      final now = DateTime.utc(2026, 7, 14);
      final saved = ControllerHardwareMapping(
        sdlPlatform: 'macOS',
        sdlGuid:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
        displayName:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbDisplayName,
        mapping: CanonicalControllerMapping(
          const <CanonicalGamepadControl, RawGamepadInput>{
            CanonicalGamepadControl.faceSouth: RawButtonInput(1),
          },
        ),
        createdAt: now,
        updatedAt: now,
      );
      final plan = await _launchReviewed(
        profile: _profileFor(BuiltinRuntimeProfiles.dolphinAdapterId),
        claims: SessionControllerSlotClaims(),
        devices: const <ConnectedGamepad>[_exactPad],
        hardwareMappings:
            _HardwareMappings(<(String, String), ControllerHardwareMapping>{
              (
                'macOS',
                RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
              ): saved,
            }),
        executableProvenance: RuntimeDependencyProvenance.external,
      );

      final reference = plan.playerControllers.single.inputReference;
      expect(
        reference.correlation,
        RuntimeControllerInputCorrelation.uncorrelated,
      );
      expect(reference.adapterPolicyId, isNull);
      expect(reference.canEmitControllerGameplay, isFalse);
    },
  );

  test(
    'matching saved mapping remains uncorrelated for managed PCSX2',
    () async {
      final now = DateTime.utc(2026, 7, 14);
      final saved = ControllerHardwareMapping(
        sdlPlatform: 'macOS',
        sdlGuid:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
        displayName:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbDisplayName,
        mapping: CanonicalControllerMapping(
          const <CanonicalGamepadControl, RawGamepadInput>{
            CanonicalGamepadControl.faceSouth: RawButtonInput(1),
          },
        ),
        createdAt: now,
        updatedAt: now,
      );
      final mappings =
          _HardwareMappings(<(String, String), ControllerHardwareMapping>{
            (
              'macOS',
              RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
            ): saved,
          });

      final managed = await _launchReviewed(
        profile: _profileFor(BuiltinRuntimeProfiles.pcsx2AdapterId),
        claims: SessionControllerSlotClaims(),
        devices: const <ConnectedGamepad>[_exactPad],
        hardwareMappings: mappings,
      );
      final suppressed = managed.playerControllers.single.inputReference;
      expect(
        suppressed.correlation,
        RuntimeControllerInputCorrelation.uncorrelated,
      );
      expect(suppressed.adapterPolicyId, isNull);
      expect(suppressed.runtimeReference, isNull);
      expect(suppressed.canEmitControllerGameplay, isFalse);
      expect(suppressed.canEmitControllerShortcuts, isFalse);
    },
  );

  test('other exact RetroArch singleton keeps the port-only policy', () async {
    final plan = await _launchReviewed(
      profile: _profileFor(BuiltinRuntimeProfiles.retroArchAdapterId),
      claims: SessionControllerSlotClaims(),
      devices: const <ConnectedGamepad>[_genericExactPad],
    );

    final reference = plan.playerControllers.single.inputReference;
    expect(
      reference.adapterPolicyId,
      RuntimeControllerInputPolicies.retroArchMacosSingleController,
    );
    expect(reference.approvedRuntimeIndex, 0);
    expect(reference.canEmitControllerShortcuts, isFalse);
  });

  test('external runtime provenance suppresses versioned shortcuts', () async {
    for (final adapterId in <RuntimeAdapterId>[
      BuiltinRuntimeProfiles.retroArchAdapterId,
      BuiltinRuntimeProfiles.duckStationAdapterId,
    ]) {
      final plan = await _launchReviewed(
        profile: _profileFor(adapterId),
        claims: SessionControllerSlotClaims(),
        devices: const <ConnectedGamepad>[_exactPad],
        executableProvenance: RuntimeDependencyProvenance.external,
      );

      final reference = plan.playerControllers.single.inputReference;
      expect(reference.approvedRuntimeIndex, 0);
      expect(reference.canEmitControllerShortcuts, isFalse);
      expect(reference.canEmitControllerGameplay, isFalse);
      if (adapterId == BuiltinRuntimeProfiles.retroArchAdapterId) {
        expect(
          reference.adapterPolicyId,
          RuntimeControllerInputPolicies.retroArchMacosSingleController,
        );
      }
    }
  });

  test(
    'duplicate executable provenance fails shortcut authorization',
    () async {
      for (final provenances in <List<RuntimeDependencyProvenance>>[
        const <RuntimeDependencyProvenance>[
          RuntimeDependencyProvenance.external,
          RuntimeDependencyProvenance.managed,
        ],
        const <RuntimeDependencyProvenance>[
          RuntimeDependencyProvenance.managed,
          RuntimeDependencyProvenance.external,
        ],
      ]) {
        final plan = await _launchReviewed(
          profile: _profileFor(BuiltinRuntimeProfiles.retroArchAdapterId),
          claims: SessionControllerSlotClaims(),
          devices: const <ConnectedGamepad>[_exactPad],
          executableProvenances: provenances,
        );

        final reference = plan.playerControllers.single.inputReference;
        expect(
          reference.adapterPolicyId,
          RuntimeControllerInputPolicies.retroArchMacosSingleController,
        );
        expect(reference.approvedRuntimeIndex, 0);
        expect(reference.canEmitControllerShortcuts, isFalse);
      }
    },
  );

  test('single exact reviewed SDL pad stays uncorrelated on Linux', () async {
    for (final adapterId in <RuntimeAdapterId>[
      BuiltinRuntimeProfiles.retroArchAdapterId,
      BuiltinRuntimeProfiles.duckStationAdapterId,
    ]) {
      final plan = await _launchReviewed(
        profile: _profileFor(adapterId),
        claims: SessionControllerSlotClaims(),
        devices: const <ConnectedGamepad>[_exactPad],
        operatingSystem: 'linux',
      );

      final reference = plan.playerControllers.single.inputReference;
      expect(
        reference.correlation,
        RuntimeControllerInputCorrelation.uncorrelated,
      );
      expect(reference.runtimeReference, isNull);
      expect(reference.adapterPolicyId, isNull);
      expect(reference.canEmitControllerShortcuts, isFalse);
    }
  });

  test('zero-pad reviewed snapshot has no classified players', () async {
    final plan = await _launchReviewed(
      profile: _profileFor(BuiltinRuntimeProfiles.retroArchAdapterId),
      claims: SessionControllerSlotClaims(),
      devices: const <ConnectedGamepad>[],
    );

    expect(plan.playerControllers, isEmpty);
  });

  test('offline exact hole does not block lone exact pad policy', () async {
    final plan = await _launchReviewed(
      profile: _profileFor(BuiltinRuntimeProfiles.retroArchAdapterId),
      claims: SessionControllerSlotClaims.fromClaims(
        const <ControllerSlotClaim?>[
          ControllerSlotClaim(
            displayName: 'Offline Pad',
            sdlGuid: 'offline-guid',
            serial: 'offline-serial',
          ),
          ControllerSlotClaim(
            displayName: RuntimeControllerInputPolicies
                .retroArchMfi8BitDoPro2UsbDisplayName,
            providerId: 'exact-pad',
            sdlGuid:
                RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
            serial: 'pro2-serial',
          ),
        ],
      ),
      devices: const <ConnectedGamepad>[_exactPad],
    );

    expect(plan.reservedPlayerSlots, <int>{0});
    expect(plan.blockedPlayerSlots, isEmpty);
    expect(plan.playerControllers.single.playerSlot, 1);
    expect(
      plan.playerControllers.single.inputReference.adapterPolicyId,
      RuntimeControllerInputPolicies
          .retroArchMacosSingleControllerMfi8BitDoPro2Usb,
    );
    expect(
      plan.playerControllers.single.inputReference.approvedRuntimeIndex,
      0,
    );
    expect(
      plan.playerControllers.single.inputReference.canEmitControllerShortcuts,
      isTrue,
    );
  });

  test('two-pad reviewed snapshot remains fully uncorrelated', () async {
    const second = ConnectedGamepad(
      id: 'second-pad',
      order: 0,
      identity: ControllerIdentity(
        displayName: 'Second Pad',
        sdlGuid: 'second-guid',
        serial: 'second-serial',
      ),
    );
    final plan = await _launchReviewed(
      profile: _profileFor(BuiltinRuntimeProfiles.duckStationAdapterId),
      claims: SessionControllerSlotClaims(),
      devices: const <ConnectedGamepad>[_exactPad, second],
    );

    expect(plan.playerControllers, hasLength(2));
    for (final player in plan.playerControllers) {
      expect(
        player.inputReference.correlation,
        RuntimeControllerInputCorrelation.uncorrelated,
      );
      expect(player.inputReference.runtimeReference, isNull);
      expect(player.inputReference.adapterPolicyId, isNull);
      expect(player.inputReference.controllerShortcutsAllowed, isFalse);
      expect(player.inputReference.canEmitControllerShortcuts, isFalse);
      expect(player.inputReference.canEmitControllerGameplay, isFalse);
    }
  });

  test('single fallback pad remains uncorrelated', () async {
    final fallback = ConnectedGamepad.fallback(
      id: 'fallback-pad',
      name: 'Fallback Pad',
      order: 0,
    );
    final plan = await _launchReviewed(
      profile: _profileFor(BuiltinRuntimeProfiles.retroArchAdapterId),
      claims: SessionControllerSlotClaims(),
      devices: <ConnectedGamepad>[fallback],
    );

    final reference = plan.playerControllers.single.inputReference;
    expect(reference.provider, RuntimeControllerInputProvider.other);
    expect(
      reference.correlation,
      RuntimeControllerInputCorrelation.uncorrelated,
    );
    expect(reference.runtimeReference, isNull);
  });

  test('single GUID-only pad remains uncorrelated', () async {
    const guidOnly = ConnectedGamepad(
      id: 'guid-only',
      order: 0,
      identity: ControllerIdentity(
        displayName: 'GUID-only Pad',
        sdlGuid: 'guid-without-serial',
      ),
    );
    final plan = await _launchReviewed(
      profile: _profileFor(BuiltinRuntimeProfiles.duckStationAdapterId),
      claims: SessionControllerSlotClaims(),
      devices: const <ConnectedGamepad>[guidOnly],
    );

    final reference = plan.playerControllers.single.inputReference;
    expect(reference.provider, RuntimeControllerInputProvider.sdl3);
    expect(
      reference.correlation,
      RuntimeControllerInputCorrelation.uncorrelated,
    );
    expect(reference.runtimeReference, isNull);
  });

  test('null snapshot never qualifies a live single pad', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        BuiltinRuntimeProfiles.retroArchAdapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerSlotClaims: SessionControllerSlotClaims.fromClaims(
        <ControllerSlotClaim?>[ControllerSlotClaim.fromGamepad(_exactPad)],
      ),
      gamepadLister: () async => const <ConnectedGamepad>[_exactPad],
    );

    await provider.launch(
      profile: _profileFor(BuiltinRuntimeProfiles.retroArchAdapterId),
      target: _target,
      dependencies: const <ResolvedRuntimeDependency>[],
    );

    final reference =
        adapter.plans.single.playerControllers.single.inputReference;
    expect(
      reference.correlation,
      RuntimeControllerInputCorrelation.uncorrelated,
    );
    expect(reference.runtimeReference, isNull);
  });

  test(
    'unsupported adapter policies keep a lone exact pad uncorrelated',
    () async {
      for (final adapterId in <RuntimeAdapterId>[
        BuiltinRuntimeProfiles.pcsx2AdapterId,
        const RuntimeAdapterId('unknown-runtime'),
      ]) {
        final plan = await _launchReviewed(
          profile: _profileFor(adapterId),
          claims: SessionControllerSlotClaims(),
          devices: const <ConnectedGamepad>[_exactPad],
        );

        final reference = plan.playerControllers.single.inputReference;
        expect(
          reference.correlation,
          RuntimeControllerInputCorrelation.uncorrelated,
        );
        expect(reference.runtimeReference, isNull);
        expect(reference.adapterPolicyId, isNull);
      }
    },
  );

  test('without controller dependencies the plan carries the standard '
      'chord scheme', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
    );

    await provider.launch(
      profile: _profile,
      target: _target,
      dependencies: const <ResolvedRuntimeDependency>[],
    );

    expect(
      adapter.plans.single.controllerMapping.bindings,
      BuiltinControllerTemplates.defaultMapping.bindings,
    );
  });

  test('a per-title binding rule reaches the launch plan mapping', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerMappings: ControllerMappingResolver(
        rules: _StubBindingRules(<ControllerBindingRule>[
          ControllerBindingRule(
            scope: ControllerBindingScope.title,
            scopeValue: 'title-1',
            action: RomdAction.saveState,
            button: GamepadButtonPosition.faceEast,
            updatedAt: DateTime.utc(2026, 7, 6),
          ),
        ]),
      ),
    );

    await provider.launch(
      profile: _profile,
      target: _target,
      dependencies: const <ResolvedRuntimeDependency>[],
    );

    final mapping = adapter.plans.single.controllerMapping;
    expect(
      mapping.bindingFor(RomdAction.saveState),
      GamepadButtonPosition.faceEast,
    );
    expect(mapping.bindingFor(RomdAction.menu), GamepadButtonPosition.start);
  });

  test('a failing rule store falls back to the standard chords and still '
      'launches', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerMappings: ControllerMappingResolver(
        rules: _ThrowingBindingRules(),
      ),
    );

    final result = await provider.launch(
      profile: _profile,
      target: _target,
      dependencies: const <ResolvedRuntimeDependency>[],
    );

    expect(result, isA<LaunchStarted>());
    expect(
      await (result as LaunchStarted).session.completed,
      isA<LaunchExited>(),
    );
    expect(
      adapter.plans.single.controllerMapping.bindings,
      BuiltinControllerTemplates.defaultMapping.bindings,
    );
  });

  test(
    'claimed slots resolve into controller slots on the launch plan',
    () async {
      final adapter = _FakeAdapter.started(const LaunchExited(0));
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          _adapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _RecordingDependencyResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerSlotClaims:
            SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
              ControllerSlotClaim.fallback('DualSense'),
              ControllerSlotClaim.fallback('Xbox Controller'),
            ]),
        gamepadLister: () async => <ConnectedGamepad>[
          ConnectedGamepad.fallback(id: '0', name: 'Xbox Controller', order: 0),
          ConnectedGamepad.fallback(id: '1', name: 'DualSense', order: 1),
        ],
      );

      await provider.launch(
        profile: _profile,
        target: _target,
        dependencies: const <ResolvedRuntimeDependency>[],
      );

      // P1 = the claimed DualSense (device 1), P2 = the remaining pad.
      final plan = adapter.plans.single;
      expect(plan.controllerSlots.map((slot) => slot.controller.name), <String>[
        'DualSense',
        'Xbox Controller',
      ]);
      expect(plan.controllerSlots.map((slot) => slot.runtimeIndex), <int>[
        1,
        0,
      ]);
      expect(plan.padDeviceIndices, <int>[1, 0]);
    },
  );

  test(
    'launch consumes the reviewed controller resolution unchanged',
    () async {
      final adapter = _FakeAdapter.started(const LaunchExited(0));
      final claims =
          SessionControllerSlotClaims.fromClaims(const <ControllerSlotClaim?>[
            ControllerSlotClaim(
              displayName: 'Offline DualSense',
              sdlGuid: 'guid-a',
              serial: 'serial-a',
            ),
            ControllerSlotClaim(displayName: 'Xbox', providerId: 'pad-2'),
          ]);
      final connectedP2 = ConnectedGamepad.fallback(
        id: 'pad-2',
        name: 'Xbox',
        order: 0,
      );
      final snapshot = ReviewedLaunchSnapshot.resolve(
        claimRevision: claims.revision,
        claims: claims.claimDetails,
        devices: <ConnectedGamepad>[connectedP2],
      );
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          _adapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _RecordingDependencyResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerSlotClaims: claims,
        gamepadLister: () async => <ConnectedGamepad>[connectedP2],
      );

      final result = await provider.launch(
        profile: _profile,
        target: _target,
        dependencies: const <ResolvedRuntimeDependency>[],
        controllerSnapshot: snapshot,
      );

      expect(result, isA<LaunchStarted>());
      final setup = adapter.plans.single.controllerSetup;
      expect(setup.reservedPlayerSlots, <int>{0});
      expect(setup.controllerSlots.single.playerSlot, 1);
    },
  );

  test('device changes after review prevent process start', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    final claims = SessionControllerSlotClaims();
    final reviewed = ConnectedGamepad.fallback(
      id: 'pad-old',
      name: 'DualSense',
      order: 0,
    );
    final reconnected = ConnectedGamepad.fallback(
      id: 'pad-new',
      name: 'DualSense',
      order: 0,
    );
    final snapshot = ReviewedLaunchSnapshot.resolve(
      claimRevision: claims.revision,
      claims: claims.claimDetails,
      devices: <ConnectedGamepad>[reviewed],
    );
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerSlotClaims: claims,
      gamepadLister: () async => <ConnectedGamepad>[reconnected],
    );

    final result = await provider.launch(
      profile: _profile,
      target: _target,
      dependencies: const <ResolvedRuntimeDependency>[],
      controllerSnapshot: snapshot,
    );

    expect(result, isA<LaunchNotStarted>());
    expect(
      (result as LaunchNotStarted).result,
      isA<LaunchControllerReviewRequired>(),
    );
    expect(adapter.plans, isEmpty);
  });

  test(
    'device changes during setup are rechecked before process start',
    () async {
      final adapter = _FakeAdapter.started(const LaunchExited(0));
      final claims = SessionControllerSlotClaims();
      const reviewed = ConnectedGamepad(
        id: 'pad-old',
        order: 0,
        identity: ControllerIdentity(
          displayName: 'DualSense',
          sdlGuid: 'dual-sense-guid',
          serial: 'dual-sense-serial',
        ),
      );
      const reconnected = ConnectedGamepad(
        id: 'pad-new',
        order: 0,
        identity: ControllerIdentity(
          displayName: 'DualSense',
          sdlGuid: 'dual-sense-guid',
          serial: 'dual-sense-serial',
        ),
      );
      final snapshot = ReviewedLaunchSnapshot.resolve(
        claimRevision: claims.revision,
        claims: claims.claimDetails,
        devices: <ConnectedGamepad>[reviewed],
      );
      var listings = 0;
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          _adapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _RecordingDependencyResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerSlotClaims: claims,
        gamepadLister: () async => ++listings == 1
            ? <ConnectedGamepad>[reviewed]
            : <ConnectedGamepad>[reconnected],
      );

      final result = await provider.launch(
        profile: _profile,
        target: _target,
        dependencies: const <ResolvedRuntimeDependency>[],
        controllerSnapshot: snapshot,
      );

      expect(
        (result as LaunchNotStarted).result,
        isA<LaunchControllerReviewRequired>(),
      );
      expect(adapter.plans, isEmpty);
    },
  );

  test('claim mutation after review prevents process start', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    final claims = SessionControllerSlotClaims();
    final pad = ConnectedGamepad.fallback(
      id: 'pad-1',
      name: 'DualSense',
      order: 0,
    );
    final snapshot = ReviewedLaunchSnapshot.resolve(
      claimRevision: claims.revision,
      claims: claims.claimDetails,
      devices: <ConnectedGamepad>[pad],
    );
    await claims.replaceAllClaims(<ControllerSlotClaim?>[
      ControllerSlotClaim.fromGamepad(pad),
    ]);
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerSlotClaims: claims,
      gamepadLister: () async => <ConnectedGamepad>[pad],
    );

    final result = await provider.launch(
      profile: _profile,
      target: _target,
      dependencies: const <ResolvedRuntimeDependency>[],
      controllerSnapshot: snapshot,
    );

    expect(result, isA<LaunchNotStarted>());
    expect(
      (result as LaunchNotStarted).result,
      isA<LaunchControllerReviewRequired>(),
    );
    expect(adapter.plans, isEmpty);
  });

  test(
    'controller listing recovery after unavailable review requires re-review',
    () async {
      final adapter = _FakeAdapter.started(const LaunchExited(0));
      final claims = SessionControllerSlotClaims();
      final snapshot = ReviewedLaunchSnapshot.resolve(
        claimRevision: claims.revision,
        claims: claims.claimDetails,
        devices: const <ConnectedGamepad>[],
        inventoryAvailable: false,
      );
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          _adapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _RecordingDependencyResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerSlotClaims: claims,
        gamepadLister: () async => const <ConnectedGamepad>[],
      );

      final result = await provider.launch(
        profile: _profile,
        target: _target,
        dependencies: const <ResolvedRuntimeDependency>[],
        controllerSnapshot: snapshot,
      );

      expect(
        (result as LaunchNotStarted).result,
        isA<LaunchControllerReviewRequired>(),
      );
      expect(adapter.plans, isEmpty);
    },
  );

  test('unavailable listing preserves the reviewed safe fallback', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    final claims = SessionControllerSlotClaims();
    final snapshot = ReviewedLaunchSnapshot.resolve(
      claimRevision: claims.revision,
      claims: claims.claimDetails,
      devices: const <ConnectedGamepad>[],
      inventoryAvailable: false,
    );
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerSlotClaims: claims,
      gamepadLister: () async => throw StateError('provider unavailable'),
    );

    final result = await provider.launch(
      profile: _profile,
      target: _target,
      dependencies: const <ResolvedRuntimeDependency>[],
      controllerSnapshot: snapshot,
    );

    expect(result, isA<LaunchStarted>());
    expect(adapter.plans.single.controllerSetup.controllerSlots, isEmpty);
  });

  test(
    'exact controller claims distinguish identical connected pads',
    () async {
      final adapter = _FakeAdapter.started(const LaunchExited(0));
      const dualSenseA = ConnectedGamepad(
        id: '7',
        order: 0,
        identity: ControllerIdentity(
          displayName: 'DualSense',
          sdlGuid: '030000004c050000e60c000000006800',
          serial: 'serial-a',
        ),
      );
      const dualSenseB = ConnectedGamepad(
        id: '9',
        order: 1,
        identity: ControllerIdentity(
          displayName: 'DualSense',
          sdlGuid: '030000004c050000e60c000000006800',
          serial: 'serial-b',
        ),
      );
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          _adapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _RecordingDependencyResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerSlotClaims:
            SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
              ControllerSlotClaim.fromGamepad(dualSenseB),
              ControllerSlotClaim.fromGamepad(dualSenseA),
            ]),
        gamepadLister: () async => const <ConnectedGamepad>[
          dualSenseA,
          dualSenseB,
        ],
      );

      await provider.launch(
        profile: _profile,
        target: _target,
        dependencies: const <ResolvedRuntimeDependency>[],
      );

      final plan = adapter.plans.single;
      expect(
        plan.controllerSlots.map((slot) => slot.controller.identity.serial),
        <String?>['serial-b', 'serial-a'],
      );
      expect(plan.padDeviceIndices, <int>[1, 0]);
    },
  );

  test('exact offline reservations remain launch slot holes', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    const reservedP1 = ConnectedGamepad(
      id: '7',
      order: 0,
      identity: ControllerIdentity(
        displayName: 'DualSense',
        sdlGuid: '030000004c050000e60c000000006800',
        serial: 'serial-a',
      ),
    );
    const connectedP2 = ConnectedGamepad(
      id: '9',
      order: 0,
      identity: ControllerIdentity(
        displayName: '8BitDo Pro 2',
        sdlGuid: '03000000c82d00001590000000010000',
      ),
    );
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerMappings: ControllerMappingResolver(
        profileRules: _StubProfileBindingRules(<ControllerBindingRule>[
          ControllerBindingRule(
            scope: ControllerBindingScope.global,
            scopeValue: '',
            action: RomdAction.saveState,
            button: GamepadButtonPosition.faceWest,
            updatedAt: DateTime.utc(2026, 7, 9),
          ),
        ]),
      ),
      controllerSlotClaims:
          SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
            ControllerSlotClaim.fromGamepad(reservedP1),
            ControllerSlotClaim.fromGamepad(connectedP2),
          ]),
      gamepadLister: () async => const <ConnectedGamepad>[connectedP2],
    );

    await provider.launch(
      profile: _profile,
      target: _targetForProfile('jan'),
      dependencies: const <ResolvedRuntimeDependency>[],
    );

    final plan = adapter.plans.single;
    expect(plan.reservedPlayerSlots, <int>{0});
    expect(plan.blockedPlayerSlots, isEmpty);
    expect(plan.controllerSlots.map((slot) => slot.playerSlot), <int>[1]);
    expect(plan.controllerSlots.single.controller.name, '8BitDo Pro 2');
    expect(plan.padDeviceIndices, <int>[0]);
    expect(
      plan.controllerMapping.bindingFor(RomdAction.saveState),
      BuiltinControllerTemplates.defaultMapping.bindingFor(
        RomdAction.saveState,
      ),
    );
  });

  test(
    'fallback claims do not launch-steal exact same-name controllers',
    () async {
      final adapter = _FakeAdapter.started(const LaunchExited(0));
      const exactDualSense = ConnectedGamepad(
        id: '7',
        order: 0,
        identity: ControllerIdentity(
          displayName: 'DualSense',
          sdlGuid: '030000004c050000e60c000000006800',
          serial: 'serial-a',
        ),
      );
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          _adapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _RecordingDependencyResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerSlotClaims: SessionControllerSlotClaims(<String?>[
          'DualSense',
          null,
          null,
          null,
        ]),
        gamepadLister: () async => const <ConnectedGamepad>[exactDualSense],
      );

      await provider.launch(
        profile: _profile,
        target: _target,
        dependencies: const <ResolvedRuntimeDependency>[],
      );

      final plan = adapter.plans.single;
      expect(plan.reservedPlayerSlots, isEmpty);
      expect(plan.blockedPlayerSlots, <int>{0});
      expect(plan.controllerSlots, isEmpty);
    },
  );

  test('profile/GUID binding reaches the launch plan mapping', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    const controller = ConnectedGamepad(
      id: '7',
      order: 0,
      identity: ControllerIdentity(
        displayName: '8BitDo Pro 2',
        sdlGuid: '03000000c82d00001590000000010000',
      ),
    );
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerMappings: ControllerMappingResolver(
        profileRules: _StubProfileBindingRules(<ControllerBindingRule>[
          ControllerBindingRule(
            scope: ControllerBindingScope.global,
            scopeValue: '',
            action: RomdAction.saveState,
            button: GamepadButtonPosition.faceWest,
            updatedAt: DateTime.utc(2026, 7, 9),
          ),
        ]),
      ),
      controllerSlotClaims: SessionControllerSlotClaims.fromClaims(
        <ControllerSlotClaim?>[ControllerSlotClaim.fromGamepad(controller)],
      ),
      gamepadLister: () async => const <ConnectedGamepad>[controller],
    );

    await provider.launch(
      profile: _profile,
      target: _targetForProfile('jan'),
      dependencies: const <ResolvedRuntimeDependency>[],
    );

    expect(
      adapter.plans.single.controllerMapping.bindingFor(RomdAction.saveState),
      GamepadButtonPosition.faceWest,
    );
  });

  test('connected players resolve independent profile/GUID mappings', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    const p1 = ConnectedGamepad(
      id: '7',
      order: 0,
      identity: ControllerIdentity(displayName: 'Pad A', sdlGuid: 'guid-a'),
    );
    const p2 = ConnectedGamepad(
      id: '9',
      order: 1,
      identity: ControllerIdentity(displayName: 'Pad B', sdlGuid: 'guid-b'),
    );
    final profileRules = _PerGuidProfileBindingRules(
      <String, List<ControllerBindingRule>>{
        'guid-a': <ControllerBindingRule>[
          ControllerBindingRule(
            scope: ControllerBindingScope.global,
            scopeValue: '',
            action: RomdAction.saveState,
            button: GamepadButtonPosition.faceWest,
            updatedAt: DateTime.utc(2026, 7, 10),
          ),
        ],
        'guid-b': <ControllerBindingRule>[
          ControllerBindingRule(
            scope: ControllerBindingScope.global,
            scopeValue: '',
            action: RomdAction.saveState,
            button: GamepadButtonPosition.faceEast,
            updatedAt: DateTime.utc(2026, 7, 10),
          ),
        ],
      },
    );
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerMappings: ControllerMappingResolver(profileRules: profileRules),
      controllerSlotClaims:
          SessionControllerSlotClaims.fromClaims(const <ControllerSlotClaim?>[
            ControllerSlotClaim(displayName: 'Pad A', providerId: '7'),
            ControllerSlotClaim(displayName: 'Pad B', providerId: '9'),
          ]),
      gamepadLister: () async => const <ConnectedGamepad>[p1, p2],
    );

    await provider.launch(
      profile: _profile,
      target: _targetForProfile('jan'),
      dependencies: const <ResolvedRuntimeDependency>[],
    );

    final players = adapter.plans.single.controllerSetup.playerControllers;
    expect(players.map((player) => player.playerSlot), <int>[0, 1]);
    expect(players.map((player) => player.identity.sdlGuid), <String?>[
      'guid-a',
      'guid-b',
    ]);
    expect(
      players.map((player) => player.mapping.bindingFor(RomdAction.saveState)),
      <GamepadButtonPosition?>[
        GamepadButtonPosition.faceWest,
        GamepadButtonPosition.faceEast,
      ],
    );
    expect(profileRules.requestedGuids, <String>['guid-a', 'guid-b']);
    expect(
      players.map((player) => player.inputReference.providerOrdinal),
      <int>[0, 1],
    );
    expect(
      players.every(
        (player) =>
            player.templateId == BuiltinControllerTemplates.generic.id &&
            identical(
              player.capabilities,
              BuiltinControllerTemplates.generic.capabilities,
            ),
      ),
      isTrue,
    );
  });

  test(
    'hardware mappings resolve by SDL platform/GUID with per-player failure isolation',
    () async {
      final adapter = _FakeAdapter.started(const LaunchExited(0));
      const guidA = '0300000000000000000000000000000a';
      const guidB = '0300000000000000000000000000000b';
      const p1 = ConnectedGamepad(
        id: 'hardware-a',
        order: 0,
        identity: ControllerIdentity(displayName: 'Pad A', sdlGuid: guidA),
      );
      const p2 = ConnectedGamepad(
        id: 'hardware-b',
        order: 1,
        identity: ControllerIdentity(displayName: 'Pad B', sdlGuid: guidB),
      );
      final now = DateTime.utc(2026, 7, 12);
      final saved = ControllerHardwareMapping(
        sdlPlatform: 'macOS',
        sdlGuid: guidA,
        displayName: 'Pad A',
        mapping: CanonicalControllerMapping(
          const <CanonicalGamepadControl, RawGamepadInput>{
            CanonicalGamepadControl.faceSouth: RawButtonInput(3),
          },
        ),
        createdAt: now,
        updatedAt: now,
      );
      final hardwareMappings = _HardwareMappings(
        <(String, String), ControllerHardwareMapping>{('macOS', guidA): saved},
        throwingGuids: const <String>{guidB},
      );
      final claims = SessionControllerSlotClaims.fromClaims(
        <ControllerSlotClaim?>[
          ControllerSlotClaim.fromGamepad(p1),
          ControllerSlotClaim.fromGamepad(p2),
        ],
      );
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          _adapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _RecordingDependencyResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerHardwareMappings: hardwareMappings,
        controllerSlotClaims: claims,
        gamepadLister: () async => const <ConnectedGamepad>[p1, p2],
        operatingSystem: 'macos',
      );

      await provider.launch(
        profile: _profile,
        target: _target,
        dependencies: const <ResolvedRuntimeDependency>[],
      );

      final players = adapter.plans.single.playerControllers;
      expect(players, hasLength(2));
      expect(players[0].gameplayMapping, same(saved));
      expect(players[1].gameplayMapping, isNull);
      expect(players[0].resolvedSlot.controller, p1);
      expect(players[1].resolvedSlot.controller, p2);
      expect(hardwareMappings.requests, <(String, String)>[
        ('macOS', guidA),
        ('macOS', guidB),
      ]);
    },
  );

  test(
    'assigned, fallback, and mixed seats resolve against the right profiles',
    () async {
      const p1 = ConnectedGamepad(
        id: 'p1',
        order: 0,
        identity: ControllerIdentity(displayName: 'Pad A', sdlGuid: 'guid-a'),
      );
      const p2 = ConnectedGamepad(
        id: 'p2',
        order: 1,
        identity: ControllerIdentity(displayName: 'Pad B', sdlGuid: 'guid-b'),
      );
      const p3 = ConnectedGamepad(
        id: 'p3',
        order: 2,
        identity: ControllerIdentity(displayName: 'Pad C', sdlGuid: 'guid-c'),
      );
      ControllerBindingRule binding(
        RomdAction action,
        GamepadButtonPosition button,
      ) => ControllerBindingRule(
        scope: ControllerBindingScope.global,
        scopeValue: '',
        action: action,
        button: button,
        updatedAt: DateTime.utc(2026, 7, 11),
      );
      final profileRules = _PerProfileGuidBindingRules(
        <(String, String), List<ControllerBindingRule>>{
          ('active', 'guid-a'): <ControllerBindingRule>[
            binding(RomdAction.saveState, GamepadButtonPosition.faceWest),
          ],
          ('active', 'guid-b'): <ControllerBindingRule>[
            binding(RomdAction.loadState, GamepadButtonPosition.faceEast),
          ],
          ('guest', 'guid-b'): <ControllerBindingRule>[
            binding(RomdAction.saveState, GamepadButtonPosition.faceNorth),
          ],
          ('active', 'guid-c'): <ControllerBindingRule>[
            binding(RomdAction.saveState, GamepadButtonPosition.faceSouth),
          ],
        },
      );
      final adapter = _FakeAdapter.started(const LaunchExited(0));
      final claims =
          SessionControllerSlotClaims.fromClaims(const <ControllerSlotClaim?>[
            ControllerSlotClaim(displayName: 'Pad A', providerId: 'p1'),
            ControllerSlotClaim(
              displayName: 'Pad B',
              providerId: 'p2',
              localProfileId: 'guest',
            ),
            ControllerSlotClaim(displayName: 'Pad C', providerId: 'p3'),
          ]);
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          _adapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _RecordingDependencyResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerMappings: ControllerMappingResolver(
          profileRules: profileRules,
        ),
        controllerSlotClaims: claims,
        gamepadLister: () async => const <ConnectedGamepad>[p1, p2, p3],
      );
      final snapshot = ReviewedLaunchSnapshot.resolve(
        claimRevision: claims.revision,
        claims: claims.claimDetails,
        devices: const <ConnectedGamepad>[p1, p2, p3],
      );

      await provider.launch(
        profile: _profile,
        target: _targetForProfile('active'),
        dependencies: const <ResolvedRuntimeDependency>[],
        controllerSnapshot: snapshot,
      );

      final plan = adapter.plans.single;
      final players = plan.controllerSetup.playerControllers;
      expect(plan.target.localProfileId, 'active');
      expect(plan.target.saveRoot, '/profiles/active/games/snes/title-1/saves');
      expect(
        plan.target.stateRoot,
        '/profiles/active/games/snes/title-1/states',
      );
      expect(
        players[0].mapping.bindingFor(RomdAction.saveState),
        GamepadButtonPosition.faceWest,
      );
      expect(
        players[1].mapping.bindingFor(RomdAction.saveState),
        GamepadButtonPosition.faceNorth,
      );
      expect(
        players[1].mapping.bindingFor(RomdAction.loadState),
        GamepadButtonPosition.faceEast,
      );
      expect(
        players[2].mapping.bindingFor(RomdAction.saveState),
        GamepadButtonPosition.faceSouth,
      );
      expect(profileRules.requests, <(String, String)>[
        ('active', 'guid-a'),
        ('active', 'guid-b'),
        ('guest', 'guid-b'),
        ('active', 'guid-c'),
      ]);
    },
  );

  test(
    'one player mapping failure preserves other mappings and seating',
    () async {
      final adapter = _FakeAdapter.started(const LaunchExited(0));
      const p1 = ConnectedGamepad(
        id: '7',
        order: 1,
        identity: ControllerIdentity(displayName: 'Pad A', sdlGuid: 'guid-a'),
      );
      const p2 = ConnectedGamepad(
        id: '9',
        order: 0,
        identity: ControllerIdentity(displayName: 'Pad B', sdlGuid: 'guid-b'),
      );
      final profileRules = _PerGuidProfileBindingRules(
        <String, List<ControllerBindingRule>>{
          'guid-b': <ControllerBindingRule>[
            ControllerBindingRule(
              scope: ControllerBindingScope.global,
              scopeValue: '',
              action: RomdAction.saveState,
              button: GamepadButtonPosition.faceEast,
              updatedAt: DateTime.utc(2026, 7, 10),
            ),
          ],
        },
        throwingGuid: 'guid-a',
      );
      final provider = EmulatorLaunchProvider(
        runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
          _adapterId: EmulatorRuntimeLaunchEntry(
            dependencyResolver: _RecordingDependencyResolver(),
            adapterFactory: () => adapter,
          ),
        },
        controllerMappings: ControllerMappingResolver(
          profileRules: profileRules,
        ),
        controllerSlotClaims:
            SessionControllerSlotClaims.fromClaims(const <ControllerSlotClaim?>[
              ControllerSlotClaim(displayName: 'Pad A', providerId: '7'),
              ControllerSlotClaim(displayName: 'Pad B', providerId: '9'),
            ]),
        gamepadLister: () async => const <ConnectedGamepad>[p2, p1],
      );

      await provider.launch(
        profile: _profile,
        target: _targetForProfile('jan'),
        dependencies: const <ResolvedRuntimeDependency>[],
      );

      final players = adapter.plans.single.controllerSetup.playerControllers;
      expect(players.map((player) => player.playerSlot), <int>[0, 1]);
      expect(
        players[0].mapping.bindings,
        BuiltinControllerTemplates.defaultMapping.bindings,
      );
      expect(
        players[1].mapping.bindingFor(RomdAction.saveState),
        GamepadButtonPosition.faceEast,
      );
      expect(
        players.map((player) => player.inputReference.providerOrdinal),
        <int>[1, 0],
      );
    },
  );

  test('controllers without GUID preserve the legacy mapping path', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerMappings: ControllerMappingResolver(
        rules: _StubBindingRules(<ControllerBindingRule>[
          ControllerBindingRule(
            scope: ControllerBindingScope.global,
            scopeValue: '',
            action: RomdAction.menu,
            button: GamepadButtonPosition.guide,
            updatedAt: DateTime.utc(2026, 7, 9),
          ),
        ]),
        profileRules: _ThrowingProfileBindingRules(),
      ),
      controllerSlotClaims: SessionControllerSlotClaims(<String?>[
        'Fallback Pad',
        null,
        null,
        null,
      ]),
      gamepadLister: () async => <ConnectedGamepad>[
        ConnectedGamepad.fallback(id: '0', name: 'Fallback Pad', order: 0),
      ],
    );

    await provider.launch(
      profile: _profile,
      target: _targetForProfile('jan'),
      dependencies: const <ResolvedRuntimeDependency>[],
    );

    expect(
      adapter.plans.single.controllerMapping.bindingFor(RomdAction.menu),
      GamepadButtonPosition.guide,
    );
  });

  test('a failing binding-rule store keeps independently resolved '
      'assignments', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerMappings: ControllerMappingResolver(
        rules: _ThrowingBindingRules(),
      ),
      controllerSlotClaims:
          SessionControllerSlotClaims.fromClaims(<ControllerSlotClaim?>[
            ControllerSlotClaim.fallback('DualSense'),
            ControllerSlotClaim.fallback('Xbox Controller'),
          ]),
      gamepadLister: () async => <ConnectedGamepad>[
        ConnectedGamepad.fallback(id: '0', name: 'Xbox Controller', order: 0),
        ConnectedGamepad.fallback(id: '1', name: 'DualSense', order: 1),
      ],
    );

    await provider.launch(
      profile: _profile,
      target: _target,
      dependencies: const <ResolvedRuntimeDependency>[],
    );

    final plan = adapter.plans.single;
    expect(
      plan.controllerMapping.bindings,
      BuiltinControllerTemplates.defaultMapping.bindings,
    );
    expect(plan.controllerSlots.map((slot) => slot.controller.name), <String>[
      'DualSense',
      'Xbox Controller',
    ]);
    expect(plan.padDeviceIndices, <int>[1, 0]);
  });

  test('a failing preferences store falls back to defaults and still '
      'launches', () async {
    final adapter = _FakeAdapter.started(const LaunchExited(0));
    final provider = EmulatorLaunchProvider(
      runtimes: <RuntimeAdapterId, EmulatorRuntimeLaunchEntry>{
        _adapterId: EmulatorRuntimeLaunchEntry(
          dependencyResolver: _RecordingDependencyResolver(),
          adapterFactory: () => adapter,
        ),
      },
      controllerPreferences: _ThrowingPreferences(),
    );

    final result = await provider.launch(
      profile: _profile,
      target: _target,
      dependencies: const <ResolvedRuntimeDependency>[],
    );

    expect(result, isA<LaunchStarted>());
    expect(
      await (result as LaunchStarted).session.completed,
      isA<LaunchExited>(),
    );
    final plan = adapter.plans.single;
    expect(plan.padDeviceIndices, isEmpty);
    expect(
      plan.controllerMapping.bindings,
      BuiltinControllerTemplates.defaultMapping.bindings,
    );
  });
}
