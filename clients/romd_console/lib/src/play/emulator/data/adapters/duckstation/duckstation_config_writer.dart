import 'dart:io';

import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/sdl_gamepad_mapping_codec.dart';
import 'package:romd_console/src/play/emulator/data/adapters/ini/ini_document.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';
import 'duckstation_user_directory.dart';

/// DuckStation SDL binding suffixes for ROMD's positional model — this
/// emitter's explicit map, never enum `.index`. Names verified against
/// DuckStation's `sdl_input_source.cpp`; triggers are half-axes. Unlike
/// RetroPad, every position is expressible here, including `guide`.
const Map<GamepadButtonPosition, String> _sdlButtonName =
    <GamepadButtonPosition, String>{
      GamepadButtonPosition.faceSouth: 'A',
      GamepadButtonPosition.faceEast: 'B',
      GamepadButtonPosition.faceWest: 'X',
      GamepadButtonPosition.faceNorth: 'Y',
      GamepadButtonPosition.dpadUp: 'DPadUp',
      GamepadButtonPosition.dpadDown: 'DPadDown',
      GamepadButtonPosition.dpadLeft: 'DPadLeft',
      GamepadButtonPosition.dpadRight: 'DPadRight',
      GamepadButtonPosition.leftBumper: 'LeftShoulder',
      GamepadButtonPosition.rightBumper: 'RightShoulder',
      GamepadButtonPosition.leftTrigger: '+LeftTrigger',
      GamepadButtonPosition.rightTrigger: '+RightTrigger',
      GamepadButtonPosition.select: 'Back',
      GamepadButtonPosition.start: 'Start',
      GamepadButtonPosition.guide: 'Guide',
      GamepadButtonPosition.leftStickPress: 'LeftStick',
      GamepadButtonPosition.rightStickPress: 'RightStick',
    };

/// ROMD-managed `[Hotkeys]` keys in emission order, verified against
/// DuckStation's `hotkeys.cpp`. [RomdAction.fastForward] uses the hold
/// variant, matching the RetroArch emitter.
const List<(RomdAction, String)> _hotkeyConfigKeys = <(RomdAction, String)>[
  (RomdAction.menu, 'OpenPauseMenu'),
  (RomdAction.saveState, 'SaveSelectedSaveState'),
  (RomdAction.loadState, 'LoadSelectedSaveState'),
  (RomdAction.stateSlotNext, 'SelectNextSaveStateSlot'),
  (RomdAction.stateSlotPrevious, 'SelectPreviousSaveStateSlot'),
  (RomdAction.screenshot, 'Screenshot'),
  (RomdAction.quit, 'PowerOff'),
  (RomdAction.fastForward, 'FastForward'),
  (RomdAction.pause, 'TogglePause'),
];

/// PSX pad binds as (ini key, keyboard fallback, SDL suffix). Player 1 keeps
/// the keyboard fallback; higher pads bind their SDL device only.
const List<(String, String, String)> _padBindings = <(String, String, String)>[
  ('Up', 'Keyboard/UpArrow', 'DPadUp'),
  ('Right', 'Keyboard/RightArrow', 'DPadRight'),
  ('Down', 'Keyboard/DownArrow', 'DPadDown'),
  ('Left', 'Keyboard/LeftArrow', 'DPadLeft'),
  ('LUp', 'Keyboard/W', '-LeftY'),
  ('LRight', 'Keyboard/D', '+LeftX'),
  ('LDown', 'Keyboard/S', '+LeftY'),
  ('LLeft', 'Keyboard/A', '-LeftX'),
  ('RUp', 'Keyboard/T', '-RightY'),
  ('RRight', 'Keyboard/H', '+RightX'),
  ('RDown', 'Keyboard/G', '+RightY'),
  ('RLeft', 'Keyboard/F', '-RightX'),
  ('Start', 'Keyboard/Enter', 'Start'),
  ('Select', 'Keyboard/Backspace', 'Back'),
  ('Triangle', 'Keyboard/I', 'Y'),
  ('Circle', 'Keyboard/L', 'B'),
  ('Cross', 'Keyboard/K', 'A'),
  ('Square', 'Keyboard/J', 'X'),
  ('L1', 'Keyboard/Q', 'LeftShoulder'),
  ('L2', 'Keyboard/1', '+LeftTrigger'),
  ('L3', 'Keyboard/2', 'LeftStick'),
  ('R1', 'Keyboard/E', 'RightShoulder'),
  ('R2', 'Keyboard/3', '+RightTrigger'),
  ('R3', 'Keyboard/4', 'RightStick'),
];

abstract interface class DuckStationConfigWriter {
  Future<void> write(
    DuckStationUserDirectory userDirectory, {
    LaunchControllerSetup controllerSetup = LaunchControllerSetup.defaults,
    String? saveRoot,
    String? stateRoot,
  });
}

final class DuckStationIniConfigWriter implements DuckStationConfigWriter {
  const DuckStationIniConfigWriter();

  static const _managedGameControllerMappingMarker =
      '# ROMD managed controller mapping';
  static const _managedGameControllerMappingEndMarker =
      '# End ROMD managed controller mapping';

  @override
  Future<void> write(
    DuckStationUserDirectory userDirectory, {
    LaunchControllerSetup controllerSetup = LaunchControllerSetup.defaults,
    String? saveRoot,
    String? stateRoot,
  }) async {
    if ((saveRoot == null) != (stateRoot == null)) {
      throw ArgumentError('Save and state roots must be provided together.');
    }
    final settingsFile = File(userDirectory.settingsPath);
    await settingsFile.parent.create(recursive: true);

    final approvedController = _approvedSingleController(controllerSetup);
    await _writeGameControllerDatabase(
      userDirectory,
      approvedController?.inputReference.canEmitControllerShortcuts == true
          ? approvedController?.gameplayMapping
          : null,
    );

    final existingLines = await settingsFile.exists()
        ? await settingsFile.readAsLines()
        : const <String>[];
    final merged =
        IniDocument.parse(
          existingLines,
          sectionNameParsing: IniSectionNameParsing.trimAndRejectEmpty,
          keyParsing: IniKeyParsing.trimAndRejectEmpty,
          missingSettingPlacement:
              IniMissingSettingPlacement.beforeTrailingBlankLines,
        ).merge(
          scalarSettings: _romdSettings(
            userDirectory,
            saveRoot: saveRoot,
            stateRoot: stateRoot,
          ),
          listSettings: _romdListSettings(controllerSetup),
        );

    await settingsFile.writeAsString(merged);
  }

  Future<void> _writeGameControllerDatabase(
    DuckStationUserDirectory userDirectory,
    ControllerHardwareMapping? hardwareMapping,
  ) async {
    final database = File(userDirectory.gameControllerDatabasePath);
    final existingLines = await database.exists()
        ? await database.readAsLines()
        : const <String>[];
    final preservedLines = <String>[];
    for (var index = 0; index < existingLines.length; index++) {
      if (existingLines[index] == _managedGameControllerMappingMarker) {
        final hasFramedEntry =
            index + 2 < existingLines.length &&
            existingLines[index + 2] ==
                _managedGameControllerMappingEndMarker &&
            _isManagedMapping(existingLines[index + 1]);
        if (hasFramedEntry) {
          index += 2;
        } else if (index + 1 < existingLines.length &&
            _isManagedMapping(existingLines[index + 1])) {
          // Remove the two-line format emitted before framing was added.
          index++;
        }
        // A truncated marker is removed by itself. Unrecognized following
        // content is preserved on the next iteration.
      } else if (existingLines[index] ==
          _managedGameControllerMappingEndMarker) {
        // An orphaned ROMD end marker carries no user configuration.
      } else {
        preservedLines.add(existingLines[index]);
      }
    }
    if (hardwareMapping != null) {
      final document = SdlGamepadMappingDocument(
        sdlGuid: hardwareMapping.sdlGuid,
        displayName: hardwareMapping.displayName,
        sdlPlatform: hardwareMapping.sdlPlatform,
        mapping: hardwareMapping.mapping,
      );
      preservedLines
        ..add(_managedGameControllerMappingMarker)
        ..add(const SdlGamepadMappingCodec().encode(document))
        ..add(_managedGameControllerMappingEndMarker);
    }
    if (preservedLines.isEmpty) {
      if (await database.exists()) {
        await database.delete();
      }
      return;
    }
    final contents = '${preservedLines.join('\n')}\n';
    if (await database.exists() && await database.readAsString() == contents) {
      return;
    }
    await database.writeAsString(contents);
  }

  static bool _isManagedMapping(String value) {
    try {
      const SdlGamepadMappingCodec().decode(value);
      return true;
    } on FormatException {
      return false;
    } on ArgumentError {
      return false;
    }
  }

  Map<String, Map<String, String>> _romdSettings(
    DuckStationUserDirectory userDirectory, {
    String? saveRoot,
    String? stateRoot,
  }) => <String, Map<String, String>>{
    'Main': <String, String>{
      'SetupWizardIncomplete': 'false',
      'StartFullscreen': 'false',
      'StartFullscreenUI': 'false',
      'InhibitScreensaver': 'true',
      'HideCursorInFullscreen': 'true',
      'ConfirmPowerOff': 'false',
    },
    'AutoUpdater': <String, String>{'CheckAtStartup': 'false'},
    'BIOS': <String, String>{
      'SearchDirectory': userDirectory.biosDirectoryPath,
      'PatchFastBoot': 'true',
    },
    if (saveRoot != null && stateRoot != null) ...<String, Map<String, String>>{
      'Folders': <String, String>{'SaveStates': stateRoot},
      'MemoryCards': <String, String>{'Directory': saveRoot},
    },
    'Cheevos': <String, String>{'Enabled': 'false', 'ChallengeMode': 'false'},
    'GPU': <String, String>{
      'Renderer': 'Automatic',
      'ResolutionScale': '2',
      'PGXPEnable': 'true',
      'WidescreenHack': 'false',
    },
    'Display': <String, String>{
      'CropMode': 'Overscan',
      'Scaling': 'BilinearSmooth',
      'Scaling24Bit': 'BilinearSmooth',
    },
    'InputSources': <String, String>{
      'SDL': 'true',
      'SDLControllerEnhancedMode': 'false',
      'SDLPS5PlayerLED': 'false',
      'XInput': 'false',
      'RawInput': 'false',
    },
    'ControllerPorts': <String, String>{
      'MultitapMode': 'Disabled',
      'PointerXScale': '8.0',
      'PointerYScale': '8.0',
      'PointerXInvert': 'false',
      'PointerYInvert': 'false',
    },
    'Pad1': <String, String>{'Type': 'AnalogController'},
    'Pad2': <String, String>{'Type': 'AnalogController'},
  };

  /// [controllerSetup] is the resolved mapping and player seating. The named
  /// macOS single-controller policy projects its sole physical controller to
  /// DuckStation's Pad1/SDL-0 even when its ROMD player slot sits behind an
  /// offline reservation. Every other known-controller setup preserves
  /// DuckStation's automatic order and suppresses controller hotkeys. The
  /// wholly unresolved legacy setup keeps its existing Pad1 shortcuts.
  Map<String, Map<String, List<String>>> _romdListSettings(
    LaunchControllerSetup controllerSetup,
  ) {
    final singleController = _approvedSingleController(controllerSetup);
    final legacyUnresolved =
        controllerSetup.playerControllers.isEmpty &&
        controllerSetup.reservedPlayerSlots.isEmpty &&
        controllerSetup.blockedPlayerSlots.isEmpty;
    final populated = controllerSetup.playerControllers.isNotEmpty;
    final p1Index = populated || legacyUnresolved
        ? 0
        : (controllerSetup.isPlayerSlotBlocked(0) ? null : 0);
    final p2Index = populated || legacyUnresolved
        ? 1
        : (controllerSetup.isPlayerSlotBlocked(1) ? null : 1);
    final shortcutPlayer =
        singleController?.inputReference.canEmitControllerShortcuts == true
        ? singleController
        : null;
    return <String, Map<String, List<String>>>{
      'Pad1': _padSection(sdlIndex: p1Index, keyboardFallback: true),
      'Pad2': _padSection(sdlIndex: p2Index, keyboardFallback: false),
      'Hotkeys': _hotkeySection(
        shortcutPlayer?.mapping ??
            (legacyUnresolved
                ? controllerSetup.playerOneMapping ?? ControllerMapping.empty
                : ControllerMapping.empty),
        p1Index: shortcutPlayer != null || legacyUnresolved ? 0 : null,
      ),
    };
  }

  LaunchPlayerController? _approvedSingleController(
    LaunchControllerSetup controllerSetup,
  ) {
    if (controllerSetup.playerControllers.length != 1) {
      return null;
    }
    final player = controllerSetup.playerControllers.single;
    final reference = player.inputReference;
    return reference.provider == RuntimeControllerInputProvider.sdl3 &&
            reference.correlation ==
                RuntimeControllerInputCorrelation.verified &&
            reference.adapterPolicyId ==
                RuntimeControllerInputPolicies
                    .duckStationMacosSingleController &&
            reference.approvedRuntimeIndex == 0
        ? player
        : null;
  }

  Map<String, List<String>> _padSection({
    required int? sdlIndex,
    required bool keyboardFallback,
  }) => <String, List<String>>{
    for (final (key, keyboard, sdlSuffix) in _padBindings)
      key: <String>[
        if (keyboardFallback) keyboard,
        if (sdlIndex != null) 'SDL-$sdlIndex/$sdlSuffix',
      ],
  };

  /// ROMD owns every managed hotkey key on every write: the ini persists
  /// across launches, so an unmanaged key could carry a stale chord from an
  /// earlier launch's mapping. Unbound — explicitly, by absence, on a
  /// modifier collision (a one-button "chord"), or with no usable modifier —
  /// emits the key with an empty value: DuckStation loads that as unbound,
  /// and the key's presence keeps DuckStation's first-boot default writer
  /// from re-injecting its keyboard hotkeys (observed behavior on a fresh
  /// ini). Chords always ride player 1's device.
  Map<String, List<String>> _hotkeySection(
    ControllerMapping mapping, {
    required int? p1Index,
  }) {
    final modifier =
        _sdlButtonName[mapping.bindingFor(RomdAction.hotkeyEnable)];
    return <String, List<String>>{
      for (final (action, configKey) in _hotkeyConfigKeys)
        configKey: _chordOrUnbound(mapping, action, modifier, p1Index),
    };
  }

  List<String> _chordOrUnbound(
    ControllerMapping mapping,
    RomdAction action,
    String? modifier,
    int? p1Index,
  ) {
    final button = _sdlButtonName[mapping.bindingFor(action)];
    return p1Index == null ||
            modifier == null ||
            button == null ||
            button == modifier
        ? const <String>['']
        : <String>['SDL-$p1Index/$modifier & SDL-$p1Index/$button'];
  }
}
