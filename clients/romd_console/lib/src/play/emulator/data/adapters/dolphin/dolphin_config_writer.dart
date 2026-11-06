import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/sdl_gamepad_mapping_codec.dart';
import 'package:romd_console/src/play/emulator/data/adapters/ini/ini_document.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';
import 'package:romd_console/src/play/session/domain/builtin_runtime_profiles.dart';

import 'dolphin_user_directory.dart';

abstract interface class DolphinConfigWriter {
  Future<void> write(
    EmulatorLaunchPlan plan,
    DolphinUserDirectory userDirectory,
  );

  Map<String, String> environmentFor(EmulatorLaunchPlan plan);
}

abstract interface class DolphinSettingsConfigWriter {
  Future<void> prepareSettings(DolphinUserDirectory userDirectory);
}

/// Emits a GameCube pad and return-to-ROMD hotkey only inside ROMD's pinned,
/// single-controller SDL3 policy. Every other launch receives empty sections
/// so prior authorization cannot leak into a later launch.
final class DolphinIniConfigWriter
    implements DolphinConfigWriter, DolphinSettingsConfigWriter {
  const DolphinIniConfigWriter();

  @override
  Future<void> write(
    EmulatorLaunchPlan plan,
    DolphinUserDirectory userDirectory,
  ) async {
    await userDirectory.prepareForTarget(plan.target);
    await _mergeMainSettings(
      userDirectory,
      gameplaySettings: <String, String>{
        'SlotA': '8',
        'SlotB': '255',
        'MemcardAPath': p.join(plan.target.saveRoot, 'MemoryCardA.raw'),
        'MemcardBPath': p.join(plan.target.saveRoot, 'MemoryCardB.raw'),
      },
    );

    final approved = _approvedSingleController(plan);
    await _replaceIfChanged(
      File(userDirectory.controllerSettingsPath),
      _controllerSettings(approved),
    );
    await _mergeHotkeys(userDirectory, approved);
  }

  @override
  Future<void> prepareSettings(DolphinUserDirectory userDirectory) async {
    await userDirectory.prepareBase();
    await _mergeMainSettings(userDirectory);
  }

  static Future<void> _mergeMainSettings(
    DolphinUserDirectory userDirectory, {
    Map<String, String> gameplaySettings = const <String, String>{},
  }) async {
    final settings = File(userDirectory.settingsPath);
    final existing = await settings.exists()
        ? await settings.readAsLines()
        : const <String>[];
    final merged =
        IniDocument.parse(
          existing,
          sectionNameParsing: IniSectionNameParsing.trimAndRejectEmpty,
          keyParsing: IniKeyParsing.trimAndRejectEmpty,
          missingSettingPlacement:
              IniMissingSettingPlacement.beforeTrailingBlankLines,
        ).merge(
          scalarSettings: <String, Map<String, String>>{
            'AutoUpdate': const <String, String>{'UpdateTrack': ''},
            if (gameplaySettings.isNotEmpty) 'Core': gameplaySettings,
          },
        );
    await _replaceIfChanged(settings, merged);
  }

  static Future<void> _mergeHotkeys(
    DolphinUserDirectory userDirectory,
    LaunchPlayerController? player,
  ) async {
    final bindings = _hotkeyBindings(player);
    final settings = File(userDirectory.hotkeySettingsPath);
    final existing = await settings.exists()
        ? await settings.readAsLines()
        : const <String>[];
    final merged =
        IniDocument.parse(
          existing,
          sectionNameParsing: IniSectionNameParsing.trimAndRejectEmpty,
          keyParsing: IniKeyParsing.trimAndRejectEmpty,
          missingSettingPlacement:
              IniMissingSettingPlacement.beforeTrailingBlankLines,
        ).merge(
          scalarSettings: <String, Map<String, String>>{
            'Hotkeys': <String, String>{
              'Device': bindings.device ?? '',
              'General/Stop': bindings.stop ?? '',
              'General/Toggle Pause': bindings.pause ?? '',
            },
          },
        );
    await _replaceIfChanged(settings, merged);
  }

  static Future<void> _replaceIfChanged(File settings, String contents) async {
    if (await settings.exists() && await settings.readAsString() == contents) {
      return;
    }
    final staged = File('${settings.path}.romd-staged');
    await staged.writeAsString(contents, flush: true);
    await staged.rename(settings.path);
  }

  @override
  Map<String, String> environmentFor(EmulatorLaunchPlan plan) {
    final approved = _approvedSingleController(plan);
    final mapping = approved?.gameplayMapping;
    if (mapping == null) {
      return const <String, String>{};
    }
    return <String, String>{
      'SDL_GAMECONTROLLERCONFIG': const SdlGamepadMappingCodec().encode(
        SdlGamepadMappingDocument(
          sdlGuid: mapping.sdlGuid,
          displayName: mapping.displayName,
          sdlPlatform: mapping.sdlPlatform,
          mapping: mapping.mapping,
        ),
      ),
    };
  }

  static LaunchPlayerController? _approvedSingleController(
    EmulatorLaunchPlan plan,
  ) {
    if (plan.profile.adapterId != BuiltinRuntimeProfiles.dolphinAdapterId ||
        plan.controllerSetup.playerControllers.length != 1) {
      return null;
    }
    final player = plan.controllerSetup.playerControllers.single;
    final reference = player.inputReference;
    return reference.provider == RuntimeControllerInputProvider.sdl3 &&
            reference.correlation ==
                RuntimeControllerInputCorrelation.verified &&
            reference.adapterPolicyId ==
                RuntimeControllerInputPolicies
                    .dolphinMacos2606SingleControllerSdl3GameplayV1 &&
            reference.approvedRuntimeIndex == 0 &&
            reference.canEmitControllerGameplay &&
            reference.canEmitControllerShortcuts &&
            player.gameplayMapping != null
        ? player
        : null;
  }

  static String _controllerSettings(LaunchPlayerController? player) {
    final buffer = StringBuffer();
    for (var pad = 1; pad <= 4; pad++) {
      buffer.writeln('[GCPad$pad]');
      if (pad == 1 && player != null) {
        final name = player.gameplayMapping!.displayName;
        if (name.contains('\n') || name.contains('\r')) {
          throw const FormatException('Invalid SDL controller display name.');
        }
        buffer
          ..writeln('Device = SDL/0/$name')
          ..writeln('Buttons/A = `Button S`')
          ..writeln('Buttons/B = `Button E`')
          ..writeln('Buttons/X = `Button W`')
          ..writeln('Buttons/Y = `Button N`')
          ..writeln('Buttons/Z = `Shoulder R`')
          ..writeln('Buttons/Start = `Start`')
          ..writeln('Main Stick/Up = `Left Y+`')
          ..writeln('Main Stick/Down = `Left Y-`')
          ..writeln('Main Stick/Left = `Left X-`')
          ..writeln('Main Stick/Right = `Left X+`')
          ..writeln('C-Stick/Up = `Right Y+`')
          ..writeln('C-Stick/Down = `Right Y-`')
          ..writeln('C-Stick/Left = `Right X-`')
          ..writeln('C-Stick/Right = `Right X+`')
          ..writeln('Triggers/L = `Trigger L`')
          ..writeln('Triggers/R = `Trigger R`')
          ..writeln('Triggers/L-Analog = `Trigger L`')
          ..writeln('Triggers/R-Analog = `Trigger R`')
          ..writeln('D-Pad/Up = `Pad N`')
          ..writeln('D-Pad/Down = `Pad S`')
          ..writeln('D-Pad/Left = `Pad W`')
          ..writeln('D-Pad/Right = `Pad E`');
      }
      buffer.writeln();
    }
    return buffer.toString();
  }

  static _DolphinHotkeyBindings _hotkeyBindings(
    LaunchPlayerController? player,
  ) {
    if (player == null) {
      return const _DolphinHotkeyBindings();
    }
    final name = player.gameplayMapping!.displayName;
    if (name.contains('\n') || name.contains('\r')) {
      throw const FormatException('Invalid SDL controller display name.');
    }
    final mapping = player.mapping;
    final stopAction =
        mapping.bindingFor(RomdAction.quit) ??
        mapping.bindingFor(RomdAction.menu);
    final stop = _shortcutExpression(mapping, stopAction);
    final pause = _shortcutExpression(
      mapping,
      mapping.bindingFor(RomdAction.pause),
    );
    return _DolphinHotkeyBindings(
      device: 'SDL/0/$name',
      stop: stop,
      pause: pause,
    );
  }

  static String? _shortcutExpression(
    ControllerMapping mapping,
    GamepadButtonPosition? action,
  ) {
    final modifier = mapping.bindingFor(RomdAction.hotkeyEnable);
    if (modifier == null || action == null || modifier == action) {
      return null;
    }
    final modifierName = _dolphinInputName[modifier];
    final actionName = _dolphinInputName[action];
    if (modifierName == null || actionName == null) {
      return null;
    }
    return '`$modifierName` & `$actionName`';
  }
}

final class _DolphinHotkeyBindings {
  const _DolphinHotkeyBindings({this.device, this.stop, this.pause});

  final String? device;
  final String? stop;
  final String? pause;
}

const Map<GamepadButtonPosition, String> _dolphinInputName =
    <GamepadButtonPosition, String>{
      GamepadButtonPosition.faceSouth: 'Button S',
      GamepadButtonPosition.faceEast: 'Button E',
      GamepadButtonPosition.faceWest: 'Button W',
      GamepadButtonPosition.faceNorth: 'Button N',
      GamepadButtonPosition.dpadUp: 'Pad N',
      GamepadButtonPosition.dpadDown: 'Pad S',
      GamepadButtonPosition.dpadLeft: 'Pad W',
      GamepadButtonPosition.dpadRight: 'Pad E',
      GamepadButtonPosition.leftBumper: 'Shoulder L',
      GamepadButtonPosition.rightBumper: 'Shoulder R',
      GamepadButtonPosition.leftTrigger: 'Trigger L',
      GamepadButtonPosition.rightTrigger: 'Trigger R',
      GamepadButtonPosition.select: 'Back',
      GamepadButtonPosition.start: 'Start',
      GamepadButtonPosition.guide: 'Guide',
      GamepadButtonPosition.leftStickPress: 'Thumb L',
      GamepadButtonPosition.rightStickPress: 'Thumb R',
    };
