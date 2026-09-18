import 'dart:io';

import 'package:path/path.dart' as p;

import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

/// Legacy RetroPad-derived button numbering used by ROMD's existing hotkey
/// output. RetroArch `_btn` binds still require physical calibration for the
/// selected joypad driver, so the production single-controller policy does not
/// authorize these values. This explicit map remains for the no-controller
/// compatibility path. A future calibrated policy must be distinct before it
/// can authorize shortcut emission. `guide` has no represented id and is
/// treated as unbound.
const Map<GamepadButtonPosition, int> _retroPadIndex =
    <GamepadButtonPosition, int>{
      GamepadButtonPosition.faceSouth: 0, // B
      GamepadButtonPosition.faceWest: 1, // Y
      GamepadButtonPosition.select: 2,
      GamepadButtonPosition.start: 3,
      GamepadButtonPosition.dpadUp: 4,
      GamepadButtonPosition.dpadDown: 5,
      GamepadButtonPosition.dpadLeft: 6,
      GamepadButtonPosition.dpadRight: 7,
      GamepadButtonPosition.faceEast: 8, // A
      GamepadButtonPosition.faceNorth: 9, // X
      GamepadButtonPosition.leftBumper: 10, // L
      GamepadButtonPosition.rightBumper: 11, // R
      GamepadButtonPosition.leftTrigger: 12, // L2
      GamepadButtonPosition.rightTrigger: 13, // R2
      GamepadButtonPosition.leftStickPress: 14, // L3
      GamepadButtonPosition.rightStickPress: 15, // R3
    };

/// Physical MFi `_btn` values measured twice from clean RetroArch 1.22.2
/// configs with the exact 8BitDo Pro 2 USB SDL identity named by the calibrated
/// policy. Positions absent here remain fail-closed rather than inheriting the
/// legacy RetroPad-derived guesses.
const Map<GamepadButtonPosition, int> _mfi8BitDoPro2UsbIndex =
    <GamepadButtonPosition, int>{
      GamepadButtonPosition.faceNorth: 1,
      GamepadButtonPosition.select: 2,
      GamepadButtonPosition.start: 3,
      GamepadButtonPosition.dpadLeft: 6,
      GamepadButtonPosition.dpadRight: 7,
      GamepadButtonPosition.leftBumper: 10,
      GamepadButtonPosition.rightBumper: 11,
    };

/// Chordable actions in emission order, with their RetroArch config keys.
/// [RomdAction.fastForward] uses the hold variant: fast-forward while the
/// chord is held, normal speed on release.
const List<(RomdAction, String)> _hotkeyConfigKeys = <(RomdAction, String)>[
  (RomdAction.menu, 'input_menu_toggle_btn'),
  (RomdAction.saveState, 'input_save_state_btn'),
  (RomdAction.loadState, 'input_load_state_btn'),
  (RomdAction.stateSlotNext, 'input_state_slot_increase_btn'),
  (RomdAction.stateSlotPrevious, 'input_state_slot_decrease_btn'),
  (RomdAction.screenshot, 'input_screenshot_btn'),
  (RomdAction.quit, 'input_exit_emulator_btn'),
  (RomdAction.fastForward, 'input_hold_fast_forward_btn'),
  (RomdAction.pause, 'input_pause_toggle_btn'),
];

/// Complete calibrated MFi target for RetroPad's sixteen digital controls.
///
/// Raw SDL inputs are validated and translated separately before any line is
/// emitted; canonical position alone is not calibration evidence.
const Map<CanonicalGamepadControl, String> _gameplayDigitalTargets =
    <CanonicalGamepadControl, String>{
      CanonicalGamepadControl.faceSouth: 'b',
      CanonicalGamepadControl.faceWest: 'y',
      CanonicalGamepadControl.select: 'select',
      CanonicalGamepadControl.start: 'start',
      CanonicalGamepadControl.dpadUp: 'up',
      CanonicalGamepadControl.dpadDown: 'down',
      CanonicalGamepadControl.dpadLeft: 'left',
      CanonicalGamepadControl.dpadRight: 'right',
      CanonicalGamepadControl.faceEast: 'a',
      CanonicalGamepadControl.faceNorth: 'x',
      CanonicalGamepadControl.leftShoulder: 'l',
      CanonicalGamepadControl.rightShoulder: 'r',
      CanonicalGamepadControl.leftTrigger: 'l2',
      CanonicalGamepadControl.rightTrigger: 'r2',
      CanonicalGamepadControl.leftStickPress: 'l3',
      CanonicalGamepadControl.rightStickPress: 'r3',
    };

/// Calibrated stick-axis target key stems.
/// RetroArch's Y minus/plus keys have the opposite sign from SDL's raw Y axes.
const Map<CanonicalGamepadControl, String> _gameplayAxisTargets =
    <CanonicalGamepadControl, String>{
      CanonicalGamepadControl.leftStickX: 'l_x',
      CanonicalGamepadControl.leftStickY: 'l_y',
      CanonicalGamepadControl.rightStickX: 'r_x',
      CanonicalGamepadControl.rightStickY: 'r_y',
    };

/// Generates a ROMD-owned RetroArch session config so that:
/// - saves / states / screenshots land in ROMD's per-title dirs (not the global
///   `~/Documents/RetroArch/...`),
/// - ROMD writes the authoritative config at every launch and disables the
///   normal RetroArch save-on-exit path (`config_save_on_exit = false`),
/// - the unresolved compatibility path retains its existing gamepad hotkeys;
///   populated controller setups suppress them unless a distinct calibrated
///   policy can translate physical `_btn`/hat values safely.
final class RetroArchConfigWriter {
  const RetroArchConfigWriter();

  /// Deterministic config path for a session (pure — usable from `buildCommand`).
  String configPathFor(String configRoot) =>
      p.join(configRoot, 'retroarch.cfg');

  /// ROMD-owned Mupen64Plus-Next options for one N64 title session.
  String n64CoreOptionsPathFor(String configRoot) =>
      p.join(configRoot, 'mupen64plus-next.opt');

  /// ROMD-owned remap boundary for one title session.
  String remapDirectoryFor(String configRoot) => p.join(configRoot, 'remaps');

  /// Compatibility alias for the original N64-only policy surface.
  String n64RemapDirectoryFor(String configRoot) =>
      remapDirectoryFor(configRoot);

  /// Writes the session config and returns its path. [systemDirectory] points
  /// RetroArch's BIOS lookup at ROMD's per-platform bios dir; when null the
  /// output is byte-identical to before BIOS support existed.
  ///
  /// [controllerSetup] is the launch's resolved mapping and player seating.
  /// The named macOS single-controller policy projects its sole controller to
  /// runtime port 1. Every other populated setup omits joypad indices, leaving
  /// order to RetroArch. The no-controller compatibility output stays unchanged.
  Future<String> write(
    ResolvedPlayTarget resolved, {
    String? systemDirectory,
    LaunchControllerSetup controllerSetup = LaunchControllerSetup.defaults,
    String? coreId,
  }) async {
    await Directory(resolved.configRoot).create(recursive: true);
    final path = configPathFor(resolved.configRoot);
    final platformShortName = resolved.platformShortName.trim().toLowerCase();
    final isN64 = platformShortName == 'n64';
    final isGenesisPlusGxCore =
        platformShortName == 'genesis' &&
        coreId?.trim().toLowerCase() == 'genesis_plus_gx';
    final n64CoreOptionsPath = n64CoreOptionsPathFor(resolved.configRoot);
    final titleRemapDirectory = remapDirectoryFor(resolved.configRoot);
    final gameplayController = _singleControllerForPolicy(
      controllerSetup,
      RuntimeControllerInputPolicies
          .retroArchMacosSingleControllerMfi8BitDoPro2UsbGameplayV1,
      requireCalibratedIdentity: true,
    );
    final appliesGenesisPlusGxPolicy =
        isGenesisPlusGxCore &&
        gameplayController?.inputReference.canEmitControllerGameplay == true;
    if (isN64 || appliesGenesisPlusGxPolicy) {
      await Directory(titleRemapDirectory).create(recursive: true);
    }
    if (isN64) {
      await File(
        n64CoreOptionsPath,
      ).writeAsString('${_n64CoreOptionLines.join('\n')}\n');
    }
    final shortcutController = _singleControllerForPolicy(
      controllerSetup,
      RuntimeControllerInputPolicies
          .retroArchMacosSingleControllerMfi8BitDoPro2Usb,
      requireCalibratedIdentity: true,
    );
    // The gameplay-v1 envelope is a strict superset of the reproduced
    // shortcut envelope, but the two permissions remain independent.
    final calibratedController = gameplayController ?? shortcutController;
    final singleController =
        calibratedController ??
        _singleControllerForPolicy(
          controllerSetup,
          RuntimeControllerInputPolicies.retroArchMacosSingleController,
        );
    final shortcutMapping = controllerSetup.playerControllers.isEmpty
        ? controllerSetup.playerOneMapping ?? ControllerMapping.empty
        : calibratedController?.inputReference.canEmitControllerShortcuts ==
              true
        ? calibratedController!.mapping
        : ControllerMapping.empty;
    final lines = <String>[
      // ROMD owns the config; RetroArch must not overwrite it.
      'config_save_on_exit = "false"',
      // Saves / states / screenshots land in ROMD's per-title dirs.
      'savefile_directory = "${resolved.saveRoot}"',
      'savestate_directory = "${resolved.stateRoot}"',
      'screenshot_directory = "${resolved.configRoot}"',
      // Kiosk launch: no RetroArch OSD/widgets/history over the game viewport.
      'video_fullscreen = "true"',
      'video_windowed_fullscreen = "true"',
      'video_fullscreen_x = "0"',
      'video_fullscreen_y = "0"',
      'video_font_enable = "false"',
      'menu_enable_widgets = "false"',
      'menu_show_load_content_animation = "false"',
      'history_list_enable = "false"',
      'content_runtime_log = "false"',
      'content_runtime_log_aggregate = "false"',
      'ui_companion_start_on_boot = "false"',
      'ui_companion_enable = "false"',
      'ui_companion_toggle = "false"',
      'desktop_menu_enable = "false"',
      'ui_menubar_enable = "false"',
      'notification_show_autoconfig = "false"',
      'notification_show_autoconfig_fails = "false"',
      'notification_show_cheats_applied = "false"',
      'notification_show_patch_applied = "false"',
      'notification_show_remap_load = "false"',
      'notification_show_config_override_load = "false"',
      'notification_show_set_initial_disk = "false"',
      'notification_show_disk_control = "false"',
      'notification_show_save_state = "false"',
      'notification_show_fast_forward = "false"',
      'notification_show_screenshot = "false"',
      'notification_show_refresh_rate = "false"',
      'notification_show_netplay_extra = "false"',
      'notification_show_when_menu_is_alive = "false"',
      'input_overlay_enable = "false"',
      'input_autodetect_enable = "true"',
      if (isN64) ...<String>[
        // Isolate Mupen64Plus-Next's selected RetroPad-to-N64 semantics inside
        // the same per-title ROMD boundary as the generated session config.
        'core_options_path = "$n64CoreOptionsPath"',
      ],
      if (appliesGenesisPlusGxPolicy) ...<String>[
        // Genesis Plus GX receives canonical RetroPad positions as a standard
        // joypad. No core-options file is needed for this device selection.
        'input_libretro_device_p1 = "1"',
        'input_player1_analog_dpad_mode = "1"',
      ],
      if (isN64 || appliesGenesisPlusGxPolicy) ...<String>[
        // Keep core-specific controller policy inside this title's ROMD-owned
        // boundary instead of inheriting global remaps and overrides.
        'auto_remaps_enable = "false"',
        'auto_overrides_enable = "false"',
        'game_specific_options = "false"',
        'remap_save_on_exit = "false"',
        'input_remapping_directory = "$titleRemapDirectory"',
      ],
      if (calibratedController != null) 'input_joypad_driver = "mfi"',
      // Lightweight, asset-free menu; a single Quit exits cleanly back to ROMD.
      'menu_driver = "rgui"',
      'rgui_show_start_screen = "false"',
      'quit_press_twice = "false"',
      'quit_on_close_content = "2"',
      // In-game controls: chords from the resolved controller mapping.
      ..._hotkeyLines(
        shortcutMapping,
        buttonIndices: calibratedController == null
            ? _retroPadIndex
            : _mfi8BitDoPro2UsbIndex,
        ownAllManagedKeys: calibratedController != null,
      ),
      if (gameplayController?.inputReference.canEmitControllerGameplay == true)
        ...?_gameplayLines(gameplayController!),
      // Cardinality, not the preserved ROMD player slot, establishes port 1:
      // with one physical controller it is RetroArch's only runtime input.
      if (singleController != null) 'input_player1_joypad_index = "0"',
      // BIOS lookup for cores that need one; absent for BIOS-free platforms.
      if (systemDirectory != null) 'system_directory = "$systemDirectory"',
    ];
    await File(path).writeAsString('${lines.join('\n')}\n');
    return path;
  }

  static const List<String> _n64CoreOptionLines = <String>[
    'mupen64plus-alt-map = "False"',
    'mupen64plus-r-cbutton = "C1"',
    'mupen64plus-l-cbutton = "C2"',
    'mupen64plus-d-cbutton = "C3"',
    'mupen64plus-u-cbutton = "C4"',
  ];

  /// Returns all 48 owned port-1 gameplay alternatives, or null to preserve
  /// RetroArch autoconfig when any present raw input is outside the duplicated
  /// SDL-to-MFi calibration. A missing control is an explicit unbind and nuls
  /// both alternatives. Guide is frontend-only and deliberately ignored.
  static List<String>? _gameplayLines(LaunchPlayerController player) {
    final hardware = player.gameplayMapping;
    if (hardware == null ||
        hardware.sdlPlatform != 'macOS' ||
        hardware.sdlGuid.toLowerCase() !=
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid ||
        hardware.sdlGuid.toLowerCase() !=
            player.identity.sdlGuid?.toLowerCase()) {
      return null;
    }

    final mapping = hardware.mapping;
    for (final entry in mapping.bindings.entries) {
      if (entry.key == CanonicalGamepadControl.guide) {
        continue;
      }
      final valid = _gameplayDigitalTargets.containsKey(entry.key)
          ? _calibratedMfiDigitalSource(entry.value) != null
          : _gameplayAxisTargets.containsKey(entry.key) &&
                _calibratedMfiAxis(entry.value) != null;
      if (!valid) {
        return null;
      }
    }

    return <String>[
      for (final entry in _gameplayDigitalTargets.entries)
        ..._digitalGameplayLines(
          keySuffix: entry.value,
          input: switch (mapping.inputFor(entry.key)) {
            final input? => _calibratedMfiDigitalSource(input),
            null => null,
          },
        ),
      for (final entry in _gameplayAxisTargets.entries)
        ..._axisGameplayLines(
          keyStem: entry.value,
          input: switch (mapping.inputFor(entry.key)) {
            final input? => _calibratedMfiAxis(input),
            null => null,
          },
        ),
    ];
  }

  /// Translates a reproduced raw SDL digital source to its saved MFi button.
  /// Triggers accept SDL's full or positive-axis forms only; negative and
  /// inverted forms are outside the calibration envelope.
  static ({int? button, String? axis})? _calibratedMfiDigitalSource(
    RawGamepadInput input,
  ) {
    if (input is RawButtonInput) {
      final button = switch (input.button) {
        0 => 0,
        1 => 8,
        2 => 1,
        3 => 9,
        4 => 2,
        6 => 3,
        7 => 14,
        8 => 15,
        9 => 10,
        10 => 11,
        _ => null,
      };
      return button == null ? null : (button: button, axis: null);
    }
    if (input is RawHatInput && input.hat == 0) {
      final button = switch (input.mask) {
        1 => 4,
        4 => 5,
        8 => 6,
        2 => 7,
        _ => null,
      };
      return button == null ? null : (button: button, axis: null);
    }
    if (input is RawAxisInput && !input.inverted && input.axis >= 4) {
      final button = switch (input.axis) {
        4
            when input.direction == RawAxisDirection.full ||
                input.direction == RawAxisDirection.positive =>
          12,
        5
            when input.direction == RawAxisDirection.full ||
                input.direction == RawAxisDirection.positive =>
          13,
        _ => null,
      };
      return button == null ? null : (button: button, axis: null);
    }
    if (input is RawAxisInput &&
        input.axis >= 0 &&
        input.axis <= 3 &&
        input.direction != RawAxisDirection.full) {
      var positive = input.direction == RawAxisDirection.positive;
      if (input.inverted) {
        positive = !positive;
      }
      if (input.axis.isOdd) {
        positive = !positive;
      }
      return (button: null, axis: '${positive ? '+' : '-'}${input.axis}');
    }
    return null;
  }

  /// Returns `(MFi axis, base inverted, custom inverted)` for one reproduced
  /// full-range SDL stick source. Custom inversion swaps the emitted signs.
  static (int, bool, bool)? _calibratedMfiAxis(RawGamepadInput input) {
    if (input is! RawAxisInput ||
        input.direction != RawAxisDirection.full ||
        input.axis < 0 ||
        input.axis > 3) {
      return null;
    }
    return (input.axis, input.axis.isOdd, input.inverted);
  }

  static List<String> _digitalGameplayLines({
    required String keySuffix,
    required ({int? button, String? axis})? input,
  }) => <String>[
    'input_player1_${keySuffix}_btn = "${input?.button ?? 'nul'}"',
    'input_player1_${keySuffix}_axis = "${input?.axis ?? 'nul'}"',
  ];

  static List<String> _axisGameplayLines({
    required String keyStem,
    required (int, bool, bool)? input,
  }) {
    if (input == null) {
      return <String>[
        'input_player1_${keyStem}_minus_btn = "nul"',
        'input_player1_${keyStem}_minus_axis = "nul"',
        'input_player1_${keyStem}_plus_btn = "nul"',
        'input_player1_${keyStem}_plus_axis = "nul"',
      ];
    }
    final (axis, baseInverted, customInverted) = input;
    final inverted = baseInverted ^ customInverted;
    final minus = inverted ? '+$axis' : '-$axis';
    final plus = inverted ? '-$axis' : '+$axis';
    return <String>[
      'input_player1_${keyStem}_minus_btn = "nul"',
      'input_player1_${keyStem}_minus_axis = "$minus"',
      'input_player1_${keyStem}_plus_btn = "nul"',
      'input_player1_${keyStem}_plus_axis = "$plus"',
    ];
  }

  static LaunchPlayerController? _singleControllerForPolicy(
    LaunchControllerSetup controllerSetup,
    String policyId, {
    bool requireCalibratedIdentity = false,
  }) {
    if (controllerSetup.playerControllers.length != 1) {
      return null;
    }
    final player = controllerSetup.playerControllers.single;
    final reference = player.inputReference;
    return reference.provider == RuntimeControllerInputProvider.sdl3 &&
            reference.correlation ==
                RuntimeControllerInputCorrelation.verified &&
            reference.adapterPolicyId == policyId &&
            reference.approvedRuntimeIndex == 0 &&
            (!requireCalibratedIdentity ||
                (player.identity.hasExactIdentity &&
                    player.identity.displayName ==
                        RuntimeControllerInputPolicies
                            .retroArchMfi8BitDoPro2UsbDisplayName &&
                    player.identity.sdlGuid?.toLowerCase() ==
                        RuntimeControllerInputPolicies
                            .retroArchMfi8BitDoPro2UsbSdlGuid))
        ? player
        : null;
  }

  /// Emits the hotkey block for [mapping]. `"nul"` is RetroArch's explicit
  /// unbind — required wherever "no bind" is meant, because with
  /// `input_autodetect_enable` a bundled autoconfig profile may supply hotkey
  /// binds for the detected pad; omission only inherits. The legacy unresolved
  /// path omits actions absent from its mapping to preserve its bytes. A
  /// calibrated policy instead owns and explicitly nuls every managed key.
  ///
  /// Without a mappable hotkey-enable button everything is nul'd: RetroArch
  /// treats hotkeys as always active when no enable-hotkey bind exists, so a
  /// leftover action bind would fire on a bare button press mid-game.
  /// RetroArch's built-in gamepad combos stay disabled regardless: ROMD's
  /// chords are the only pad-driven hotkeys.
  static List<String> _hotkeyLines(
    ControllerMapping mapping, {
    Map<GamepadButtonPosition, int> buttonIndices = _retroPadIndex,
    bool ownAllManagedKeys = false,
  }) {
    final modifier = buttonIndices[mapping.bindingFor(RomdAction.hotkeyEnable)];
    if (modifier == null) {
      return <String>[
        'input_enable_hotkey_btn = "nul"',
        'input_menu_toggle_gamepad_combo = "0"',
        'input_quit_gamepad_combo = "0"',
        for (final (_, configKey) in _hotkeyConfigKeys) '$configKey = "nul"',
      ];
    }

    return <String>[
      'input_enable_hotkey_btn = "$modifier"',
      'input_menu_toggle_gamepad_combo = "0"',
      'input_quit_gamepad_combo = "0"',
      for (final (action, configKey) in _hotkeyConfigKeys)
        if (_actionLine(
              mapping,
              action,
              configKey,
              modifier,
              buttonIndices,
              ownAllManagedKeys,
            )
            case final line?)
          line,
    ];
  }

  /// One action's bind line, or null to omit it. Explicit unbinds, positions
  /// with no RetroPad id (guide), and binds colliding with the modifier (a
  /// one-button "chord" would trigger while merely holding the modifier) all
  /// emit an explicit `"nul"`.
  static String? _actionLine(
    ControllerMapping mapping,
    RomdAction action,
    String configKey,
    int modifier,
    Map<GamepadButtonPosition, int> buttonIndices,
    bool ownAllManagedKeys,
  ) {
    if (!mapping.bindings.containsKey(action)) {
      return ownAllManagedKeys ? '$configKey = "nul"' : null;
    }
    final index = buttonIndices[mapping.bindingFor(action)];
    return index == null || index == modifier
        ? '$configKey = "nul"'
        : '$configKey = "$index"';
  }
}
