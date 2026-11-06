import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/sdl_gamepad_mapping_codec.dart';
import 'package:romd_console/src/play/emulator/data/adapters/duckstation/duckstation_config_writer.dart';
import 'package:romd_console/src/play/emulator/data/adapters/duckstation/duckstation_user_directory.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';

void main() {
  late Directory tmp;
  late DuckStationUserDirectory userDirectory;
  const reducedCapabilities =
      ControllerCapabilities.fixed(<GamepadButtonPosition>{
        GamepadButtonPosition.faceSouth,
        GamepadButtonPosition.faceEast,
        GamepadButtonPosition.dpadUp,
        GamepadButtonPosition.dpadDown,
        GamepadButtonPosition.dpadLeft,
        GamepadButtonPosition.dpadRight,
        GamepadButtonPosition.select,
        GamepadButtonPosition.start,
      });

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_duckstation_config_test');
    userDirectory = DuckStationUserDirectory(
      root: Directory(p.join(tmp.path, 'duckstation-user')),
      operatingSystem: 'macos',
    );
  });

  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  ResolvedControllerSlot slot({
    required int playerSlot,
    required int runtimeIndex,
    bool exactIdentity = false,
  }) => ResolvedControllerSlot(
    playerSlot: playerSlot,
    controller: exactIdentity
        ? ConnectedGamepad(
            id: 'sdl-instance-$runtimeIndex',
            order: runtimeIndex,
            identity: ControllerIdentity(
              displayName: 'Pad $runtimeIndex',
              sdlGuid: '03000000deadbeef0000000000000000',
              serial: 'serial-$runtimeIndex',
            ),
          )
        : ConnectedGamepad.fallback(
            id: 'pad-$runtimeIndex',
            name: 'Pad $runtimeIndex',
            order: runtimeIndex,
          ),
    runtimeIndex: runtimeIndex,
  );

  LaunchPlayerController player({
    required int playerSlot,
    required int runtimeIndex,
    required ControllerMapping mapping,
    bool verified = false,
    bool exactIdentity = false,
    String adapterPolicyId = 'test.duckstation.verified',
    bool controllerShortcutsAllowed = false,
    RuntimeControllerInputProvider? provider,
    RuntimeControllerInputCorrelation correlation =
        RuntimeControllerInputCorrelation.verified,
    ControllerHardwareMapping? gameplayMapping,
  }) => LaunchPlayerController(
    resolvedSlot: slot(
      playerSlot: playerSlot,
      runtimeIndex: runtimeIndex,
      exactIdentity: exactIdentity,
    ),
    inputReference: verified
        ? RuntimeControllerInputReference(
            provider:
                provider ??
                (exactIdentity
                    ? RuntimeControllerInputProvider.sdl3
                    : RuntimeControllerInputProvider.other),
            providerDeviceId: exactIdentity
                ? 'sdl-instance-$runtimeIndex'
                : 'pad-$runtimeIndex',
            providerOrdinal: runtimeIndex,
            correlation: correlation,
            runtimeReference: runtimeIndex,
            adapterPolicyId: adapterPolicyId,
            controllerShortcutsAllowed: controllerShortcutsAllowed,
          )
        : RuntimeControllerInputReference.uncorrelated(
            provider: RuntimeControllerInputProvider.other,
            providerDeviceId: 'pad-$runtimeIndex',
            providerOrdinal: runtimeIndex,
          ),
    mapping: mapping,
    templateId: BuiltinControllerTemplates.generic.id,
    capabilities: BuiltinControllerTemplates.generic.capabilities,
    gameplayMapping: gameplayMapping,
  );

  LaunchPlayerController singleControllerPlayer({
    int playerSlot = 0,
    ControllerMapping mapping = BuiltinControllerTemplates.defaultMapping,
    ControllerHardwareMapping? gameplayMapping,
  }) => player(
    playerSlot: playerSlot,
    runtimeIndex: 0,
    mapping: mapping,
    verified: true,
    exactIdentity: true,
    adapterPolicyId:
        RuntimeControllerInputPolicies.duckStationMacosSingleController,
    controllerShortcutsAllowed: true,
    gameplayMapping: gameplayMapping,
  );

  LaunchControllerSetup setup({
    ControllerMapping mapping = BuiltinControllerTemplates.defaultMapping,
    List<ResolvedControllerSlot>? controllerSlots,
    List<int> padDeviceIndices = const <int>[],
    Set<int> reservedPlayerSlots = const <int>{},
    Set<int> blockedPlayerSlots = const <int>{},
  }) => LaunchControllerSetup(
    mapping: mapping,
    controllerSlots:
        controllerSlots ??
        <ResolvedControllerSlot>[
          for (final (playerSlot, runtimeIndex) in padDeviceIndices.indexed)
            slot(playerSlot: playerSlot, runtimeIndex: runtimeIndex),
        ],
    reservedPlayerSlots: reservedPlayerSlots,
    blockedPlayerSlots: blockedPlayerSlots,
  );

  ControllerHardwareMapping hardwareMapping() {
    final now = DateTime.utc(2026, 7, 12);
    return ControllerHardwareMapping(
      sdlPlatform: 'macOS',
      sdlGuid: '03000000deadbeef0000000000000000',
      displayName: 'Configured Pad',
      mapping: CanonicalControllerMapping(
        const <CanonicalGamepadControl, RawGamepadInput>{
          CanonicalGamepadControl.faceSouth: RawButtonInput(3),
          CanonicalGamepadControl.faceEast: RawButtonInput(2),
          CanonicalGamepadControl.faceWest: RawButtonInput(1),
          CanonicalGamepadControl.faceNorth: RawButtonInput(0),
          CanonicalGamepadControl.dpadUp: RawHatInput(0, 1),
          CanonicalGamepadControl.dpadRight: RawHatInput(0, 2),
          CanonicalGamepadControl.dpadDown: RawHatInput(0, 4),
          CanonicalGamepadControl.dpadLeft: RawHatInput(0, 8),
          CanonicalGamepadControl.leftShoulder: RawButtonInput(4),
          CanonicalGamepadControl.rightShoulder: RawButtonInput(5),
          CanonicalGamepadControl.leftTrigger: RawAxisInput(
            4,
            direction: RawAxisDirection.positive,
          ),
          CanonicalGamepadControl.rightTrigger: RawAxisInput(
            5,
            direction: RawAxisDirection.positive,
          ),
          CanonicalGamepadControl.start: RawButtonInput(7),
          CanonicalGamepadControl.guide: RawButtonInput(8),
          CanonicalGamepadControl.leftStickPress: RawButtonInput(9),
          CanonicalGamepadControl.rightStickPress: RawButtonInput(10),
          CanonicalGamepadControl.leftStickX: RawAxisInput(0),
          CanonicalGamepadControl.leftStickY: RawAxisInput(1, inverted: true),
          CanonicalGamepadControl.rightStickX: RawAxisInput(2),
          CanonicalGamepadControl.rightStickY: RawAxisInput(3),
        },
      ),
      createdAt: now,
      updatedAt: now,
    );
  }

  test('write creates a first-run ready DuckStation config', () async {
    const writer = DuckStationIniConfigWriter();

    await writer.write(userDirectory);

    final settings = File(userDirectory.settingsPath).readAsStringSync();
    expect(settings, contains('[Main]'));
    expect(settings, contains('SetupWizardIncomplete = false'));
    expect(settings, contains('StartFullscreenUI = false'));
    expect(settings, contains('[AutoUpdater]'));
    expect(settings, contains('CheckAtStartup = false'));
    expect(settings, contains('[BIOS]'));
    expect(
      settings,
      contains('SearchDirectory = ${userDirectory.biosDirectoryPath}'),
    );
    expect(settings, contains('PatchFastBoot = true'));
    expect(settings, contains('[GPU]'));
    expect(settings, contains('ResolutionScale = 2'));
    expect(settings, contains('PGXPEnable = true'));
    expect(settings, contains('[Pad1]'));
    expect(settings, contains('Type = AnalogController'));
    expect(settings, contains('Cross = Keyboard/K'));
    expect(settings, contains('Cross = SDL-0/A'));
    expect(settings, contains('LUp = SDL-0/-LeftY'));
    expect(settings, contains('R2 = SDL-0/+RightTrigger'));
  });

  test('write replaces profile-owned memory card and state roots', () async {
    const writer = DuckStationIniConfigWriter();
    final firstSave = p.join(tmp.path, 'profiles', 'one', 'saves');
    final firstState = p.join(tmp.path, 'profiles', 'one', 'states');
    final secondSave = p.join(tmp.path, 'profiles', 'two', 'saves');
    final secondState = p.join(tmp.path, 'profiles', 'two', 'states');

    await writer.write(
      userDirectory,
      saveRoot: firstSave,
      stateRoot: firstState,
    );
    await writer.write(
      userDirectory,
      saveRoot: secondSave,
      stateRoot: secondState,
    );

    final settings = File(userDirectory.settingsPath).readAsStringSync();
    expect(settings, contains('SaveStates = $secondState'));
    expect(settings, contains('Directory = $secondSave'));
    expect(settings, isNot(contains(firstSave)));
    expect(settings, isNot(contains(firstState)));
  });

  test(
    'write preserves unrelated settings and replaces ROMD-owned settings',
    () async {
      const writer = DuckStationIniConfigWriter();
      final settingsFile = File(userDirectory.settingsPath)
        ..createSync(recursive: true)
        ..writeAsStringSync('''
[Main]
SetupWizardIncomplete = true
CustomThing = keep-me
SetupWizardIncomplete = true

[GPU]
ResolutionScale = 1
ExperimentalThing = enabled
''');

      await writer.write(userDirectory);

      final settings = settingsFile.readAsStringSync();
      expect(settings, contains('SetupWizardIncomplete = false'));
      expect(settings, isNot(contains('SetupWizardIncomplete = true')));
      expect(
        RegExp(
          r'^SetupWizardIncomplete = false$',
          multiLine: true,
        ).allMatches(settings),
        hasLength(1),
      );
      expect(settings, contains('CustomThing = keep-me'));
      expect(settings, contains('ResolutionScale = 2'));
      expect(settings, contains('ExperimentalThing = enabled'));
    },
  );

  test('write adds missing keys to existing sections', () async {
    const writer = DuckStationIniConfigWriter();
    final settingsFile = File(userDirectory.settingsPath)
      ..createSync(recursive: true)
      ..writeAsStringSync('''
[Main]
CustomThing = keep-me

[GPU]
ExperimentalThing = enabled
''');

    await writer.write(userDirectory);

    final settings = settingsFile.readAsStringSync();
    expect(
      RegExp(r'^\[Main\]$', multiLine: true).allMatches(settings),
      hasLength(1),
    );
    expect(
      RegExp(r'^\[GPU\]$', multiLine: true).allMatches(settings),
      hasLength(1),
    );
    expect(settings, contains('SetupWizardIncomplete = false'));
    expect(settings, contains('ResolutionScale = 2'));
  });

  test(
    'write replaces stale controller bindings with keyboard and SDL lists',
    () async {
      const writer = DuckStationIniConfigWriter();
      final settingsFile = File(userDirectory.settingsPath)
        ..createSync(recursive: true)
        ..writeAsStringSync('''
[Pad1]
Type = DigitalController
Cross = Keyboard/K
Cross = OldDevice/A
Triangle = OldDevice/Y
''');

      await writer.write(userDirectory);

      final settings = settingsFile.readAsStringSync();
      expect(settings, contains('Type = AnalogController'));
      // Pad1 keyboard + Pad1 SDL-0 + Pad2 SDL-1.
      expect(
        RegExp(r'^Cross = ', multiLine: true).allMatches(settings),
        hasLength(3),
      );
      expect(settings, contains('Cross = Keyboard/K'));
      expect(settings, contains('Cross = SDL-0/A'));
      expect(settings, isNot(contains('OldDevice')));
      expect(settings, contains('Triangle = Keyboard/I'));
      expect(settings, contains('Triangle = SDL-0/Y'));
    },
  );

  String sectionOf(String settings, String header) {
    final start = settings.indexOf('[$header]');
    expect(start, isNot(-1), reason: 'missing section [$header]');
    final end = settings.indexOf('\n[', start);
    return end == -1
        ? settings.substring(start)
        : settings.substring(start, end);
  }

  test(
    'binds Pad2 to the second SDL device with no keyboard fallback',
    () async {
      const writer = DuckStationIniConfigWriter();

      await writer.write(userDirectory);

      final settings = File(userDirectory.settingsPath).readAsStringSync();
      final pad2 = sectionOf(settings, 'Pad2');
      expect(pad2, contains('Type = AnalogController'));
      expect(pad2, contains('Cross = SDL-1/A'));
      expect(pad2, contains('R2 = SDL-1/+RightTrigger'));
      expect(pad2, isNot(contains('Keyboard/')));
    },
  );

  test('emits the standard chord scheme into [Hotkeys] on player 1, with '
      'unbound managed keys explicitly emptied', () async {
    const writer = DuckStationIniConfigWriter();

    await writer.write(userDirectory);

    final settings = File(userDirectory.settingsPath).readAsStringSync();
    final hotkeys = sectionOf(settings, 'Hotkeys');
    expect(hotkeys, contains('OpenPauseMenu = SDL-0/Back & SDL-0/Start'));
    expect(
      hotkeys,
      contains('SaveSelectedSaveState = SDL-0/Back & SDL-0/RightShoulder'),
    );
    expect(
      hotkeys,
      contains('LoadSelectedSaveState = SDL-0/Back & SDL-0/LeftShoulder'),
    );
    expect(
      hotkeys,
      contains('SelectNextSaveStateSlot = SDL-0/Back & SDL-0/DPadRight'),
    );
    expect(
      hotkeys,
      contains('SelectPreviousSaveStateSlot = SDL-0/Back & SDL-0/DPadLeft'),
    );
    expect(hotkeys, contains('Screenshot = SDL-0/Back & SDL-0/Y'));
    // Absent-by-default actions are still owned: written empty so a stale
    // chord from an earlier launch can never linger in the persistent ini.
    expect(RegExp(r'^PowerOff = $', multiLine: true).hasMatch(hotkeys), isTrue);
    expect(
      RegExp(r'^FastForward = $', multiLine: true).hasMatch(hotkeys),
      isTrue,
    );
    expect(
      RegExp(r'^TogglePause = $', multiLine: true).hasMatch(hotkeys),
      isTrue,
    );
  });

  test('replaces DuckStation-injected keyboard hotkey defaults on managed '
      'keys but leaves unmanaged hotkeys alone', () async {
    const writer = DuckStationIniConfigWriter();
    final settingsFile = File(userDirectory.settingsPath)
      ..createSync(recursive: true)
      ..writeAsStringSync('''
[Hotkeys]
FastForward = Keyboard/Tab
OpenPauseMenu = Keyboard/Escape
SaveSelectedSaveState = Keyboard/F2
ToggleFullscreen = Keyboard/F11
''');

    await writer.write(userDirectory);

    final settings = settingsFile.readAsStringSync();
    expect(settings, isNot(contains('Keyboard/Escape')));
    expect(settings, isNot(contains('Keyboard/F2')));
    expect(settings, isNot(contains('Keyboard/Tab')));
    expect(settings, contains('ToggleFullscreen = Keyboard/F11'));
    expect(settings, contains('OpenPauseMenu = SDL-0/Back & SDL-0/Start'));
  });

  test('rebinds, explicit unbinds, and modifier collisions all reach '
      '[Hotkeys]', () async {
    const writer = DuckStationIniConfigWriter();
    final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
      const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
        RomdAction.saveState: GamepadButtonPosition.faceEast,
        RomdAction.screenshot: null,
        RomdAction.menu: GamepadButtonPosition.select,
      }),
    );

    await writer.write(userDirectory, controllerSetup: setup(mapping: mapping));

    final hotkeys = sectionOf(
      File(userDirectory.settingsPath).readAsStringSync(),
      'Hotkeys',
    );
    expect(hotkeys, contains('SaveSelectedSaveState = SDL-0/Back & SDL-0/B'));
    expect(
      RegExp(r'^Screenshot = $', multiLine: true).hasMatch(hotkeys),
      isTrue,
    );
    // menu bound to the modifier itself: a one-button "chord" must unbind.
    expect(
      RegExp(r'^OpenPauseMenu = $', multiLine: true).hasMatch(hotkeys),
      isTrue,
    );
    expect(
      hotkeys,
      contains('LoadSelectedSaveState = SDL-0/Back & SDL-0/LeftShoulder'),
    );
  });

  test('guide is a usable modifier here, unlike RetroPad', () async {
    const writer = DuckStationIniConfigWriter();
    final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
      const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
        RomdAction.hotkeyEnable: GamepadButtonPosition.guide,
      }),
    );

    await writer.write(userDirectory, controllerSetup: setup(mapping: mapping));

    final hotkeys = sectionOf(
      File(userDirectory.settingsPath).readAsStringSync(),
      'Hotkeys',
    );
    expect(hotkeys, contains('OpenPauseMenu = SDL-0/Guide & SDL-0/Start'));
  });

  test(
    'uncorrelated provider order preserves DuckStation automatic order',
    () async {
      const writer = DuckStationIniConfigWriter();

      await writer.write(
        userDirectory,
        controllerSetup: setup(padDeviceIndices: const <int>[1, 0]),
      );

      final settingsFile = File(userDirectory.settingsPath);
      final settings = settingsFile.readAsStringSync();
      final pad1 = sectionOf(settings, 'Pad1');
      final pad2 = sectionOf(settings, 'Pad2');
      final hotkeys = sectionOf(settings, 'Hotkeys');
      expect(pad1, contains('Cross = SDL-0/A'));
      expect(pad1, contains('Cross = Keyboard/K'));
      expect(pad2, contains('Cross = SDL-1/A'));
      expect(pad2, isNot(contains('Keyboard/')));
      expect(hotkeys, isNot(contains('SDL-')));

      await writer.write(
        userDirectory,
        controllerSetup: setup(padDeviceIndices: const <int>[7, 4]),
      );
      expect(settingsFile.readAsStringSync(), settings);
    },
  );

  test(
    'zero-player reserved or blocked P1 keeps keyboard without SDL-0',
    () async {
      const writer = DuckStationIniConfigWriter();
      for (final testCase in <({String label, LaunchControllerSetup setup})>[
        (label: 'reserved', setup: setup(reservedPlayerSlots: const <int>{0})),
        (label: 'blocked', setup: setup(blockedPlayerSlots: const <int>{0})),
      ]) {
        await writer.write(userDirectory, controllerSetup: testCase.setup);

        final settings = File(userDirectory.settingsPath).readAsStringSync();
        final pad1 = sectionOf(settings, 'Pad1');
        final pad2 = sectionOf(settings, 'Pad2');
        expect(pad1, contains('Cross = Keyboard/K'), reason: testCase.label);
        expect(pad1, isNot(contains('SDL-0/')), reason: testCase.label);
        expect(pad2, contains('Cross = SDL-1/A'), reason: testCase.label);
        expect(pad2, isNot(contains('Keyboard/')), reason: testCase.label);
        expect(
          sectionOf(settings, 'Hotkeys'),
          isNot(contains('SDL-')),
          reason: testCase.label,
        );
      }
    },
  );

  test('zero-player blocked P2 omits SDL-1 only', () async {
    const writer = DuckStationIniConfigWriter();

    await writer.write(
      userDirectory,
      controllerSetup: setup(blockedPlayerSlots: const <int>{1}),
    );

    final settings = File(userDirectory.settingsPath).readAsStringSync();
    final pad1 = sectionOf(settings, 'Pad1');
    final pad2 = sectionOf(settings, 'Pad2');
    expect(pad1, contains('Cross = Keyboard/K'));
    expect(pad1, contains('Cross = SDL-0/A'));
    expect(pad2, isNot(contains('SDL-1/')));
    expect(pad2, isNot(contains('Keyboard/')));
    expect(sectionOf(settings, 'Hotkeys'), isNot(contains('SDL-')));
  });

  test(
    'single-controller policy binds the exact SDL pad to Pad1 with shortcuts',
    () async {
      const writer = DuckStationIniConfigWriter();
      final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
        const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
          RomdAction.saveState: GamepadButtonPosition.faceEast,
        }),
      );

      await writer.write(
        userDirectory,
        controllerSetup: LaunchControllerSetup(
          playerControllers: <LaunchPlayerController>[
            singleControllerPlayer(mapping: mapping),
          ],
        ),
      );
      final settings = File(userDirectory.settingsPath).readAsStringSync();

      expect(sectionOf(settings, 'Pad1'), contains('Cross = SDL-0/A'));
      expect(sectionOf(settings, 'Pad2'), contains('Cross = SDL-1/A'));
      expect(
        sectionOf(settings, 'Hotkeys'),
        contains('SaveSelectedSaveState = SDL-0/Back & SDL-0/B'),
      );
    },
  );

  test(
    'verified singleton installs physical gameplay mapping through SDL canonical controls',
    () async {
      const writer = DuckStationIniConfigWriter();
      final gameplayMapping = hardwareMapping();
      final controllerSetup = LaunchControllerSetup(
        playerControllers: <LaunchPlayerController>[
          singleControllerPlayer(gameplayMapping: gameplayMapping),
        ],
      );

      await writer.write(userDirectory, controllerSetup: controllerSetup);

      final database = File(
        userDirectory.gameControllerDatabasePath,
      ).readAsStringSync();
      expect(database, contains('${gameplayMapping.sdlGuid},Configured Pad,'));
      expect(database, contains('a:b3,b:b2,x:b1,y:b0'));
      expect(database, contains('dpup:h0.1'));
      expect(database, contains('dpdown:h0.4'));
      expect(database, contains('lefttrigger:+a4'));
      expect(database, contains('leftx:a0'));
      expect(database, contains('lefty:a1~'));
      expect(database, contains('platform:macOS,'));
      expect(database, isNot(contains('back:')));

      final pad1 = sectionOf(
        File(userDirectory.settingsPath).readAsStringSync(),
        'Pad1',
      );
      for (final binding in <String>[
        'Cross = SDL-0/A',
        'Circle = SDL-0/B',
        'Square = SDL-0/X',
        'Triangle = SDL-0/Y',
        'Up = SDL-0/DPadUp',
        'LUp = SDL-0/-LeftY',
        'RRight = SDL-0/+RightX',
        'L1 = SDL-0/LeftShoulder',
        'L2 = SDL-0/+LeftTrigger',
        'L3 = SDL-0/LeftStick',
        'R1 = SDL-0/RightShoulder',
        'R2 = SDL-0/+RightTrigger',
        'R3 = SDL-0/RightStick',
        'Start = SDL-0/Start',
        'Select = SDL-0/Back',
      ]) {
        expect(pad1, contains(binding), reason: binding);
      }

      final firstDatabase = database;
      final firstSettings = File(userDirectory.settingsPath).readAsStringSync();
      await writer.write(userDirectory, controllerSetup: controllerSetup);
      expect(
        File(userDirectory.gameControllerDatabasePath).readAsStringSync(),
        firstDatabase,
      );
      expect(
        File(userDirectory.settingsPath).readAsStringSync(),
        firstSettings,
      );
    },
  );

  test(
    'gameplay mapping updates preserve unrelated user database entries',
    () async {
      const writer = DuckStationIniConfigWriter();
      final database = File(userDirectory.gameControllerDatabasePath)
        ..createSync(recursive: true)
        ..writeAsStringSync('# personal note\nother-guid,Other Pad,a:b0,\n');

      await writer.write(
        userDirectory,
        controllerSetup: LaunchControllerSetup(
          playerControllers: <LaunchPlayerController>[
            singleControllerPlayer(gameplayMapping: hardwareMapping()),
          ],
        ),
      );
      expect(database.readAsStringSync(), contains('# personal note'));
      expect(
        database.readAsStringSync(),
        contains('other-guid,Other Pad,a:b0,'),
      );

      await writer.write(
        userDirectory,
        controllerSetup: setup(padDeviceIndices: const <int>[0, 1]),
      );
      final automatic = database.readAsStringSync();
      expect(automatic, contains('# personal note'));
      expect(automatic, contains('other-guid,Other Pad,a:b0,'));
      expect(automatic, isNot(contains('# ROMD managed controller mapping')));
      expect(automatic, isNot(contains('Configured Pad')));
    },
  );

  test(
    'multi-controller output removes singleton gameplay override and stays automatic',
    () async {
      const writer = DuckStationIniConfigWriter();
      await writer.write(
        userDirectory,
        controllerSetup: LaunchControllerSetup(
          playerControllers: <LaunchPlayerController>[
            singleControllerPlayer(gameplayMapping: hardwareMapping()),
          ],
        ),
      );
      expect(
        File(userDirectory.gameControllerDatabasePath).readAsStringSync(),
        isNotEmpty,
      );

      await writer.write(
        userDirectory,
        controllerSetup: setup(padDeviceIndices: const <int>[0, 1]),
      );

      expect(
        File(userDirectory.gameControllerDatabasePath).existsSync(),
        isFalse,
      );
      final settings = File(userDirectory.settingsPath).readAsStringSync();
      expect(sectionOf(settings, 'Pad1'), contains('Cross = SDL-0/A'));
      expect(sectionOf(settings, 'Pad2'), contains('Cross = SDL-1/A'));
      expect(sectionOf(settings, 'Hotkeys'), isNot(contains('SDL-')));
    },
  );

  test(
    'unmanaged singleton policy does not install gameplay mapping',
    () async {
      const writer = DuckStationIniConfigWriter();

      await writer.write(
        userDirectory,
        controllerSetup: LaunchControllerSetup(
          playerControllers: <LaunchPlayerController>[
            player(
              playerSlot: 0,
              runtimeIndex: 0,
              mapping: BuiltinControllerTemplates.defaultMapping,
              verified: true,
              exactIdentity: true,
              adapterPolicyId: RuntimeControllerInputPolicies
                  .duckStationMacosSingleController,
              gameplayMapping: hardwareMapping(),
            ),
          ],
        ),
      );

      expect(
        File(userDirectory.gameControllerDatabasePath).existsSync(),
        isFalse,
      );
    },
  );

  test(
    'automatic output safely removes legacy mapping and preserves unrecognized content',
    () async {
      const writer = DuckStationIniConfigWriter();
      final database = File(userDirectory.gameControllerDatabasePath)
        ..createSync(recursive: true)
        ..writeAsStringSync(
          '# ROMD managed controller mapping\n'
          '${const SdlGamepadMappingCodec().encode(SdlGamepadMappingDocument(sdlGuid: '03000000deadbeef0000000000000000', displayName: 'Legacy Pad', sdlPlatform: 'macOS', mapping: CanonicalControllerMapping(<CanonicalGamepadControl, RawGamepadInput>{CanonicalGamepadControl.faceSouth: const RawButtonInput(0)})))}\n'
          '# ROMD managed controller mapping\n'
          '# personal note after truncated marker\n'
          'other-guid,Other Pad,a:b0,\n',
        );

      await writer.write(
        userDirectory,
        controllerSetup: setup(padDeviceIndices: const <int>[0, 1]),
      );

      expect(
        database.readAsStringSync(),
        '# personal note after truncated marker\n'
        'other-guid,Other Pad,a:b0,\n',
      );
    },
  );

  test(
    'single-controller policy projects a reserved-hole P2 to Pad1',
    () async {
      const writer = DuckStationIniConfigWriter();
      final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
        const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
          RomdAction.saveState: GamepadButtonPosition.faceEast,
        }),
      );

      await writer.write(
        userDirectory,
        controllerSetup: LaunchControllerSetup(
          playerControllers: <LaunchPlayerController>[
            singleControllerPlayer(playerSlot: 1, mapping: mapping),
          ],
          reservedPlayerSlots: const <int>{0},
        ),
      );
      final settings = File(userDirectory.settingsPath).readAsStringSync();

      expect(sectionOf(settings, 'Pad1'), contains('Cross = SDL-0/A'));
      expect(sectionOf(settings, 'Pad2'), contains('Cross = SDL-1/A'));
      expect(
        sectionOf(settings, 'Hotkeys'),
        contains('SaveSelectedSaveState = SDL-0/Back & SDL-0/B'),
      );
    },
  );

  test(
    'a wrong adapter policy preserves automatic order and no hotkeys',
    () async {
      const writer = DuckStationIniConfigWriter();

      await writer.write(
        userDirectory,
        controllerSetup: LaunchControllerSetup(
          playerControllers: <LaunchPlayerController>[
            player(
              playerSlot: 0,
              runtimeIndex: 7,
              mapping: BuiltinControllerTemplates.defaultMapping,
              verified: true,
              exactIdentity: true,
              adapterPolicyId:
                  RuntimeControllerInputPolicies.retroArchMacosSingleController,
              controllerShortcutsAllowed: true,
            ),
          ],
        ),
      );
      final settings = File(userDirectory.settingsPath).readAsStringSync();

      expect(sectionOf(settings, 'Pad1'), contains('Cross = SDL-0/A'));
      expect(sectionOf(settings, 'Pad1'), isNot(contains('SDL-7')));
      expect(sectionOf(settings, 'Pad2'), contains('Cross = SDL-1/A'));
      expect(sectionOf(settings, 'Hotkeys'), isNot(contains('SDL-')));
    },
  );

  test(
    'wrong provider and best-effort correlation fail the policy closed',
    () async {
      const writer = DuckStationIniConfigWriter();
      for (final testCase
          in <
            (
              String,
              RuntimeControllerInputProvider,
              RuntimeControllerInputCorrelation,
            )
          >[
            (
              'wrong provider',
              RuntimeControllerInputProvider.other,
              RuntimeControllerInputCorrelation.verified,
            ),
            (
              'best effort',
              RuntimeControllerInputProvider.sdl3,
              RuntimeControllerInputCorrelation.acceptedBestEffort,
            ),
          ]) {
        await writer.write(
          userDirectory,
          controllerSetup: LaunchControllerSetup(
            playerControllers: <LaunchPlayerController>[
              player(
                playerSlot: 0,
                runtimeIndex: 0,
                mapping: BuiltinControllerTemplates.defaultMapping,
                verified: true,
                exactIdentity: true,
                adapterPolicyId: RuntimeControllerInputPolicies
                    .duckStationMacosSingleController,
                controllerShortcutsAllowed: true,
                provider: testCase.$2,
                correlation: testCase.$3,
              ),
            ],
          ),
        );
        final settings = File(userDirectory.settingsPath).readAsStringSync();

        expect(
          sectionOf(settings, 'Pad1'),
          contains('Cross = SDL-0/A'),
          reason: testCase.$1,
        );
        expect(
          sectionOf(settings, 'Pad2'),
          contains('Cross = SDL-1/A'),
          reason: testCase.$1,
        );
        expect(
          sectionOf(settings, 'Hotkeys'),
          isNot(contains('SDL-')),
          reason: testCase.$1,
        );
      }
    },
  );

  test(
    'a fallback-provider pad preserves automatic output byte-for-byte',
    () async {
      const writer = DuckStationIniConfigWriter();

      await writer.write(
        userDirectory,
        controllerSetup: setup(padDeviceIndices: const <int>[2]),
      );

      final settingsFile = File(userDirectory.settingsPath);
      final settings = settingsFile.readAsStringSync();
      expect(sectionOf(settings, 'Pad1'), contains('Cross = SDL-0/A'));
      expect(sectionOf(settings, 'Pad2'), contains('Cross = SDL-1/A'));
      expect(sectionOf(settings, 'Hotkeys'), isNot(contains('SDL-')));

      await writer.write(
        userDirectory,
        controllerSetup: setup(padDeviceIndices: const <int>[9]),
      );
      expect(settingsFile.readAsStringSync(), settings);
    },
  );

  test(
    'reserved holes keep DuckStation player numbering non-compact',
    () async {
      const writer = DuckStationIniConfigWriter();

      await writer.write(
        userDirectory,
        controllerSetup: setup(
          controllerSlots: <ResolvedControllerSlot>[
            slot(playerSlot: 1, runtimeIndex: 0),
          ],
          reservedPlayerSlots: const <int>{0},
        ),
      );

      final settings = File(userDirectory.settingsPath).readAsStringSync();
      final pad1 = sectionOf(settings, 'Pad1');
      final pad2 = sectionOf(settings, 'Pad2');
      final hotkeys = sectionOf(settings, 'Hotkeys');
      expect(pad1, contains('Cross = Keyboard/K'));
      expect(pad1, contains('Cross = SDL-0/A'));
      expect(pad2, contains('Cross = SDL-1/A'));
      expect(pad2, isNot(contains('Keyboard/')));
      expect(hotkeys, isNot(contains('SDL-')));
      expect(
        RegExp(r'^OpenPauseMenu = $', multiLine: true).hasMatch(hotkeys),
        isTrue,
      );
    },
  );

  test('without a modifier every managed hotkey key is emptied', () async {
    const writer = DuckStationIniConfigWriter();
    final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
      const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
        RomdAction.hotkeyEnable: null,
      }),
    );

    await writer.write(userDirectory, controllerSetup: setup(mapping: mapping));

    final hotkeys = sectionOf(
      File(userDirectory.settingsPath).readAsStringSync(),
      'Hotkeys',
    );
    for (final key in <String>[
      'OpenPauseMenu',
      'SaveSelectedSaveState',
      'LoadSelectedSaveState',
      'SelectNextSaveStateSlot',
      'SelectPreviousSaveStateSlot',
      'Screenshot',
      'PowerOff',
      'FastForward',
      'TogglePause',
    ]) {
      expect(
        RegExp('^$key = \$', multiLine: true).hasMatch(hotkeys),
        isTrue,
        reason: '$key should be explicitly unbound',
      );
    }
  });

  test(
    'capability-filtered mappings do not emit unavailable buttons',
    () async {
      const writer = DuckStationIniConfigWriter();
      final mapping = BuiltinControllerTemplates.defaultMapping
          .overlaidWith(
            const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
              RomdAction.fastForward: GamepadButtonPosition.rightTrigger,
              RomdAction.screenshot: null,
            }),
          )
          .filteredFor(reducedCapabilities);

      await writer.write(
        userDirectory,
        controllerSetup: setup(mapping: mapping),
      );

      final hotkeys = sectionOf(
        File(userDirectory.settingsPath).readAsStringSync(),
        'Hotkeys',
      );
      expect(hotkeys, contains('OpenPauseMenu = SDL-0/Back & SDL-0/Start'));
      expect(hotkeys, isNot(contains('RightTrigger')));
      expect(
        RegExp(r'^FastForward = $', multiLine: true).hasMatch(hotkeys),
        isTrue,
      );
      expect(
        RegExp(r'^Screenshot = $', multiLine: true).hasMatch(hotkeys),
        isTrue,
      );
    },
  );
}
