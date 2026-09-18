import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/emulator/data/adapters/dolphin/dolphin_adapter.dart';
import 'package:romd_console/src/play/emulator/data/adapters/dolphin/dolphin_user_directory.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

const _profile = RuntimeProfile(
  id: RuntimeProfileId('dolphin:gc:standalone'),
  adapterId: BuiltinRuntimeProfiles.dolphinAdapterId,
  displayName: 'Dolphin',
  supportedPlatforms: <String>{'gc'},
  requirements: <RuntimeDependencyRequirement>[
    ExecutableRequirement(
      id: 'executable',
      displayName: 'Dolphin',
      managedArtifactId: 'dolphin',
    ),
  ],
);

LaunchControllerSetup _approvedSetup({
  ControllerMapping mapping = BuiltinControllerTemplates.defaultMapping,
}) {
  const gamepad = ConnectedGamepad(
    id: 'exact-pad',
    order: 3,
    identity: ControllerIdentity(
      displayName: '8BitDo Pro 2',
      sdlGuid: '030001f2c82d00000660000000026800',
      serial: 'serial-1',
    ),
  );
  final hardwareMapping = ControllerHardwareMapping(
    sdlPlatform: 'macOS',
    sdlGuid: gamepad.identity.sdlGuid!,
    displayName: gamepad.identity.displayName,
    mapping: CanonicalControllerMapping(
      const <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.faceSouth: RawButtonInput(1),
        CanonicalGamepadControl.faceEast: RawButtonInput(0),
        CanonicalGamepadControl.leftStickX: RawAxisInput(0),
        CanonicalGamepadControl.leftStickY: RawAxisInput(1, inverted: true),
      },
    ),
    createdAt: DateTime.utc(2026, 7, 14),
    updatedAt: DateTime.utc(2026, 7, 14),
  );
  return LaunchControllerSetup(
    playerControllers: <LaunchPlayerController>[
      LaunchPlayerController(
        resolvedSlot: const ResolvedControllerSlot(
          playerSlot: 0,
          controller: gamepad,
          runtimeIndex: 3,
        ),
        inputReference: const RuntimeControllerInputReference(
          provider: RuntimeControllerInputProvider.sdl3,
          providerDeviceId: 'exact-pad',
          providerOrdinal: 3,
          runtimeReference: 0,
          correlation: RuntimeControllerInputCorrelation.verified,
          adapterPolicyId: RuntimeControllerInputPolicies
              .dolphinMacos2606SingleControllerSdl3GameplayV1,
          controllerShortcutsAllowed: true,
          controllerGameplayAllowed: true,
        ),
        mapping: mapping,
        templateId: BuiltinControllerTemplates.generic.id,
        capabilities: BuiltinControllerTemplates.generic.capabilities,
        gameplayMapping: hardwareMapping,
      ),
    ],
  );
}

void main() {
  late Directory tmp;
  late ResolvedPlayTarget target;
  late DolphinUserDirectory userDirectory;

  EmulatorLaunchPlan plan({LaunchControllerSetup? controllerSetup}) =>
      EmulatorLaunchPlan(
        target: target,
        profile: _profile,
        dependencies: const <ResolvedRuntimeDependency>[
          ResolvedRuntimeDependency(
            requirementId: 'executable',
            path: '/managed/Dolphin.app',
            provenance: RuntimeDependencyProvenance.managed,
          ),
        ],
        controllerSetup: controllerSetup,
      );

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('romd_dolphin_adapter_test');
    target = ResolvedPlayTarget(
      releaseId: 'rel-1',
      titleId: 'title-1',
      platformShortName: 'gc',
      displayName: 'F-Zero GX',
      localProfileId: 'profile-1',
      contentRoot: p.join(tmp.path, 'content'),
      launchAbsolutePath: p.join(tmp.path, 'content', 'f-zero gx.iso'),
      saveRoot: p.join(tmp.path, 'saves'),
      stateRoot: p.join(tmp.path, 'states'),
      configRoot: p.join(tmp.path, 'config'),
    );
    userDirectory = DolphinUserDirectory(
      root: Directory(p.join(tmp.path, 'shared-dolphin-user')),
    );
  });

  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  test('buildCommand scopes Dolphin and boots the selected GameCube image', () {
    final command = DolphinAdapter(userDirectory: userDirectory).buildCommand(
      plan(controllerSetup: _approvedSetup()),
      '/Applications/Dolphin.app',
    );

    expect(
      command.executable,
      '/Applications/Dolphin.app/Contents/MacOS/Dolphin',
    );
    expect(command.arguments, <String>[
      '-u',
      userDirectory.rootPath,
      '-b',
      '-C',
      'Dolphin.Display.Fullscreen=True',
      '-C',
      'Dolphin.Interface.ConfirmStop=False',
      '-C',
      'Dolphin.Core.SIDevice0=6',
      '-C',
      'Dolphin.Core.SIDevice1=0',
      '-C',
      'Dolphin.Core.SIDevice2=0',
      '-C',
      'Dolphin.Core.SIDevice3=0',
      '-C',
      'Dolphin.Core.SlotA=8',
      '-C',
      'Dolphin.Core.SlotB=255',
      '-C',
      'Dolphin.Core.MemcardAPath=${p.join(target.saveRoot, 'MemoryCardA.raw')}',
      '-C',
      'Dolphin.Core.MemcardBPath=${p.join(target.saveRoot, 'MemoryCardB.raw')}',
      '-e',
      target.launchAbsolutePath,
    ]);
    expect(command.workingDirectory, target.contentRoot);
    expect(
      command.environment['SDL_GAMECONTROLLERCONFIG'],
      contains('030001f2c82d00000660000000026800,8BitDo Pro 2,'),
    );
  });

  test(
    'prepare writes canonical GameCube controls and durable links',
    () async {
      final adapter = DolphinAdapter(userDirectory: userDirectory);
      final launchPlan = plan(controllerSetup: _approvedSetup());

      await adapter.prepare(launchPlan);

      final user = userDirectory;
      final config = File(user.controllerSettingsPath).readAsStringSync();
      expect(config, contains('Device = SDL/0/8BitDo Pro 2'));
      expect(config, contains('Buttons/A = `Button S`'));
      expect(config, contains('Buttons/B = `Button E`'));
      expect(config, contains('Main Stick/Up = `Left Y+`'));
      expect(config, contains('C-Stick/Right = `Right X+`'));
      expect(config, contains('Triggers/L-Analog = `Trigger L`'));
      expect(config, contains('Buttons/Z = `Shoulder R`'));
      final hotkeys = File(user.hotkeySettingsPath).readAsStringSync();
      expect(hotkeys, contains('[Hotkeys]'));
      expect(hotkeys, contains('Device = SDL/0/8BitDo Pro 2'));
      expect(hotkeys, contains('General/Stop = `Back` & `Start`'));
      final settings = File(user.settingsPath).readAsStringSync();
      expect(settings, contains('SlotA = 8'));
      expect(settings, contains('SlotB = 255'));
      expect(settings, isNot(contains('GCIFolderAPath')));
      expect(settings, isNot(contains('GCIFolderBPath')));
      expect(settings, contains('UpdateTrack = '));
      expect(
        await Link(user.gameCubeSavePath).target(),
        p.absolute(target.saveRoot),
      );
      expect(
        await Link(user.stateSavePath).target(),
        p.absolute(target.stateRoot),
      );
      expect(Directory(user.screenshotPath).existsSync(), isTrue);
    },
  );

  test(
    'unapproved launch clears stale pad bindings and SDL injection',
    () async {
      final approvedPlan = plan(controllerSetup: _approvedSetup());
      final adapter = DolphinAdapter(userDirectory: userDirectory);
      await adapter.prepare(approvedPlan);

      final unapprovedPlan = plan();
      await adapter.prepare(unapprovedPlan);

      final config = File(
        userDirectory.controllerSettingsPath,
      ).readAsStringSync();
      expect(config, isNot(contains('Device =')));
      expect(config, contains('[GCPad1]'));
      expect(config, contains('[GCPad4]'));
      final hotkeys = File(userDirectory.hotkeySettingsPath).readAsStringSync();
      expect(hotkeys, contains('Device = '));
      expect(hotkeys, contains('General/Stop = '));
      expect(hotkeys, isNot(contains('General/Stop = `')));
      expect(
        adapter
            .buildCommand(unapprovedPlan, '/usr/local/bin/dolphin')
            .environment,
        isNot(contains('SDL_GAMECONTROLLERCONFIG')),
      );
    },
  );

  test(
    'prepare adopts empty directory skeletons created by Dolphin settings',
    () async {
      final user = userDirectory;
      Directory(
        p.join(user.gameCubeSavePath, 'USA'),
      ).createSync(recursive: true);
      Directory(user.stateSavePath).createSync(recursive: true);

      await DolphinAdapter(
        userDirectory: userDirectory,
      ).prepare(plan(controllerSetup: _approvedSetup()));

      expect(
        await FileSystemEntity.type(user.gameCubeSavePath, followLinks: false),
        FileSystemEntityType.link,
      );
      expect(
        await FileSystemEntity.type(user.stateSavePath, followLinks: false),
        FileSystemEntityType.link,
      );
      expect(await Link(user.gameCubeSavePath).target(), target.saveRoot);
      expect(await Link(user.stateSavePath).target(), target.stateRoot);
    },
  );

  test(
    'prepare fails closed rather than replacing existing Dolphin data',
    () async {
      final user = userDirectory;
      Directory(user.gameCubeSavePath).createSync(recursive: true);
      File(
        p.join(user.gameCubeSavePath, 'MemoryCardA.raw'),
      ).writeAsStringSync('existing');

      await expectLater(
        DolphinAdapter(
          userDirectory: userDirectory,
        ).prepare(plan(controllerSetup: _approvedSetup())),
        throwsA(isA<FileSystemException>()),
      );
      expect(Directory(user.gameCubeSavePath).existsSync(), isTrue);
      expect(
        File(
          p.join(user.gameCubeSavePath, 'MemoryCardA.raw'),
        ).readAsStringSync(),
        'existing',
      );
    },
  );

  test(
    'prepare preserves a nested link in a Dolphin durable directory',
    () async {
      final user = userDirectory;
      final external = Directory(p.join(tmp.path, 'external-memory-cards'))
        ..createSync(recursive: true);
      Directory(user.gameCubeSavePath).createSync(recursive: true);
      final nestedLink = Link(p.join(user.gameCubeSavePath, 'USA'))
        ..createSync(external.path);

      await expectLater(
        DolphinAdapter(
          userDirectory: userDirectory,
        ).prepare(plan(controllerSetup: _approvedSetup())),
        throwsA(isA<FileSystemException>()),
      );

      expect(Directory(user.gameCubeSavePath).existsSync(), isTrue);
      expect(
        await FileSystemEntity.type(nestedLink.path, followLinks: false),
        FileSystemEntityType.link,
      );
      expect(await nestedLink.target(), external.path);
    },
  );

  test('shared user directory retargets ROMD-owned links per game', () async {
    final adapter = DolphinAdapter(userDirectory: userDirectory);
    await adapter.prepare(plan(controllerSetup: _approvedSetup()));
    final otherTarget = ResolvedPlayTarget(
      releaseId: 'rel-2',
      titleId: 'title-2',
      platformShortName: 'gc',
      displayName: 'Mario Kart: Double Dash!!',
      localProfileId: 'profile-2',
      contentRoot: p.join(tmp.path, 'other-content'),
      launchAbsolutePath: p.join(tmp.path, 'other-content', 'game.iso'),
      saveRoot: p.join(tmp.path, 'other-saves'),
      stateRoot: p.join(tmp.path, 'other-states'),
      configRoot: p.join(tmp.path, 'other-config'),
    );
    final otherPlan = EmulatorLaunchPlan(
      target: otherTarget,
      profile: _profile,
      dependencies: const <ResolvedRuntimeDependency>[
        ResolvedRuntimeDependency(
          requirementId: 'executable',
          path: '/managed/Dolphin.app',
          provenance: RuntimeDependencyProvenance.managed,
        ),
      ],
      controllerSetup: _approvedSetup(),
    );

    await adapter.prepare(otherPlan);

    expect(
      await Link(userDirectory.gameCubeSavePath).target(),
      p.absolute(otherTarget.saveRoot),
    );
    expect(
      await Link(userDirectory.stateSavePath).target(),
      p.absolute(otherTarget.stateRoot),
    );
    expect(File(userDirectory.controllerSettingsPath).existsSync(), isTrue);
  });

  test(
    'game launch preserves user settings but reasserts ROMD boundaries',
    () async {
      await userDirectory.prepareBase();
      await File(userDirectory.settingsPath).writeAsString('''
[Core]
CPUThread = True
SlotA = 1

[AutoUpdate]
UpdateTrack = dev
''');
      await File(userDirectory.hotkeySettingsPath).writeAsString('''
[Hotkeys]
General/Take Screenshot = `F9`
General/Stop = `ESCAPE`
''');

      await DolphinAdapter(
        userDirectory: userDirectory,
      ).prepare(plan(controllerSetup: _approvedSetup()));

      final settings = await File(userDirectory.settingsPath).readAsString();
      expect(settings, contains('CPUThread = True'));
      expect(settings, contains('SlotA = 8'));
      expect(settings, isNot(contains('GCIFolderAPath')));
      expect(settings, isNot(contains('GCIFolderBPath')));
      expect(settings, contains('UpdateTrack = \n'));
      final hotkeys = await File(
        userDirectory.hotkeySettingsPath,
      ).readAsString();
      expect(hotkeys, contains('General/Take Screenshot = `F9`'));
      expect(hotkeys, contains('General/Stop = `Back` & `Start`'));
    },
  );

  test(
    'default regional GCI folders resolve through the profile save link',
    () async {
      final importedSave = File(
        p.join(
          target.saveRoot,
          'USA',
          'Card A',
          '01-GMSE-super_mario_sunshine.gci',
        ),
      );
      final flatSaveFromObsoleteOverride = File(
        p.join(target.saveRoot, '01-GMSE-super_mario_sunshine.gci'),
      );
      await importedSave.parent.create(recursive: true);
      await importedSave.writeAsString('imported-progress');
      await flatSaveFromObsoleteOverride.writeAsString('new-progress');

      await DolphinAdapter(
        userDirectory: userDirectory,
      ).prepare(plan(controllerSetup: _approvedSetup()));

      expect(
        File(
          p.join(
            userDirectory.gameCubeSavePath,
            'USA',
            'Card A',
            '01-GMSE-super_mario_sunshine.gci',
          ),
        ).readAsStringSync(),
        'imported-progress',
      );
      expect(flatSaveFromObsoleteOverride.readAsStringSync(), 'new-progress');
    },
  );

  test(
    'explicit Quit takes priority and optional Pause stays a chord',
    () async {
      final mapping = ControllerMapping(<RomdAction, GamepadButtonPosition?>{
        RomdAction.hotkeyEnable: GamepadButtonPosition.guide,
        RomdAction.menu: GamepadButtonPosition.start,
        RomdAction.quit: GamepadButtonPosition.faceEast,
        RomdAction.pause: GamepadButtonPosition.faceNorth,
      });

      await DolphinAdapter(
        userDirectory: userDirectory,
      ).prepare(plan(controllerSetup: _approvedSetup(mapping: mapping)));

      final hotkeys = File(userDirectory.hotkeySettingsPath).readAsStringSync();
      expect(hotkeys, contains('General/Stop = `Guide` & `Button E`'));
      expect(hotkeys, contains('General/Toggle Pause = `Guide` & `Button N`'));
      expect(hotkeys, isNot(contains('`Guide` & `Start`')));
    },
  );

  test('modifier collision emits no return-to-ROMD hotkey', () async {
    final mapping = ControllerMapping(<RomdAction, GamepadButtonPosition?>{
      RomdAction.hotkeyEnable: GamepadButtonPosition.select,
      RomdAction.menu: GamepadButtonPosition.select,
    });

    await DolphinAdapter(
      userDirectory: userDirectory,
    ).prepare(plan(controllerSetup: _approvedSetup(mapping: mapping)));

    final hotkeys = File(userDirectory.hotkeySettingsPath).readAsStringSync();
    expect(hotkeys, contains('Device = SDL/0/8BitDo Pro 2'));
    expect(hotkeys, contains('General/Stop = '));
    expect(hotkeys, isNot(contains('General/Stop = `')));
  });
}
