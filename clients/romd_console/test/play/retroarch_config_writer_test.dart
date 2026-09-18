import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/emulator/data/adapters/retroarch/retroarch_config_writer.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';

void main() {
  late Directory tmp;
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

  setUp(() => tmp = Directory.systemTemp.createTempSync('romd_cfg_test'));
  tearDown(() {
    if (tmp.existsSync()) {
      tmp.deleteSync(recursive: true);
    }
  });

  ResolvedPlayTarget target({
    String platformShortName = 'snes',
    String? configRoot,
  }) => ResolvedPlayTarget(
    releaseId: 'rel-1',
    titleId: 'title-1',
    platformShortName: platformShortName,
    displayName: 'Chrono Trigger',
    localProfileId: 'profile-1',
    contentRoot: p.join(tmp.path, 'content'),
    launchAbsolutePath: p.join(tmp.path, 'content', 'chrono.sfc'),
    saveRoot: p.join(tmp.path, 'saves'),
    stateRoot: p.join(tmp.path, 'states'),
    configRoot: configRoot ?? p.join(tmp.path, 'config'),
  );

  ResolvedControllerSlot slot({
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

  ResolvedControllerSlot exactSlot({
    required int playerSlot,
    int providerOrdinal = 7,
    String displayName =
        RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbDisplayName,
    String sdlGuid =
        RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
    String? serial = 'pro2-serial',
  }) => ResolvedControllerSlot(
    playerSlot: playerSlot,
    controller: ConnectedGamepad(
      id: 'exact-pad',
      order: 7,
      identity: ControllerIdentity(
        displayName: displayName,
        sdlGuid: sdlGuid,
        serial: serial,
      ),
    ),
    runtimeIndex: providerOrdinal,
  );

  LaunchPlayerController player({
    required ResolvedControllerSlot resolvedSlot,
    required ControllerMapping mapping,
    String policyId =
        RuntimeControllerInputPolicies.retroArchMacosSingleController,
    int runtimeIndex = 0,
    bool shortcutsAllowed = false,
    bool gameplayAllowed = false,
    RuntimeControllerInputProvider provider =
        RuntimeControllerInputProvider.sdl3,
    RuntimeControllerInputCorrelation correlation =
        RuntimeControllerInputCorrelation.verified,
    ControllerHardwareMapping? gameplayMapping,
  }) => LaunchPlayerController(
    resolvedSlot: resolvedSlot,
    inputReference: RuntimeControllerInputReference(
      provider: provider,
      providerDeviceId: resolvedSlot.controller.id,
      providerOrdinal: resolvedSlot.runtimeIndex,
      correlation: correlation,
      runtimeReference: runtimeIndex,
      adapterPolicyId: policyId,
      controllerShortcutsAllowed: shortcutsAllowed,
      controllerGameplayAllowed: gameplayAllowed,
    ),
    mapping: mapping,
    templateId: BuiltinControllerTemplates.generic.id,
    capabilities: BuiltinControllerTemplates.generic.capabilities,
    gameplayMapping: gameplayMapping,
  );

  ControllerHardwareMapping hardwareMapping(
    Map<CanonicalGamepadControl, RawGamepadInput> bindings, {
    String sdlPlatform = 'macOS',
    String sdlGuid =
        RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
  }) => ControllerHardwareMapping(
    sdlPlatform: sdlPlatform,
    sdlGuid: sdlGuid,
    displayName:
        RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbDisplayName,
    mapping: CanonicalControllerMapping(bindings),
    createdAt: DateTime.utc(2026, 7, 13),
    updatedAt: DateTime.utc(2026, 7, 13),
  );

  Map<CanonicalGamepadControl, RawGamepadInput> calibratedBindings() =>
      <CanonicalGamepadControl, RawGamepadInput>{
        CanonicalGamepadControl.faceSouth: const RawButtonInput(1),
        CanonicalGamepadControl.faceEast: const RawButtonInput(0),
        CanonicalGamepadControl.faceWest: const RawButtonInput(3),
        CanonicalGamepadControl.faceNorth: const RawButtonInput(2),
        CanonicalGamepadControl.select: const RawButtonInput(4),
        CanonicalGamepadControl.start: const RawButtonInput(6),
        CanonicalGamepadControl.guide: const RawButtonInput(5),
        CanonicalGamepadControl.dpadUp: const RawHatInput(0, 1),
        CanonicalGamepadControl.dpadDown: const RawHatInput(0, 4),
        CanonicalGamepadControl.dpadLeft: const RawHatInput(0, 8),
        CanonicalGamepadControl.dpadRight: const RawHatInput(0, 2),
        CanonicalGamepadControl.leftShoulder: const RawButtonInput(9),
        CanonicalGamepadControl.rightShoulder: const RawButtonInput(10),
        CanonicalGamepadControl.leftTrigger: const RawAxisInput(4),
        CanonicalGamepadControl.rightTrigger: const RawAxisInput(
          5,
          direction: RawAxisDirection.positive,
        ),
        CanonicalGamepadControl.leftStickPress: const RawButtonInput(7),
        CanonicalGamepadControl.rightStickPress: const RawButtonInput(8),
        CanonicalGamepadControl.leftStickX: const RawAxisInput(0),
        CanonicalGamepadControl.leftStickY: const RawAxisInput(1),
        CanonicalGamepadControl.rightStickX: const RawAxisInput(2),
        CanonicalGamepadControl.rightStickY: const RawAxisInput(3),
      };

  LaunchControllerSetup gameplaySetup({
    required ControllerHardwareMapping gameplayMapping,
    bool shortcutsAllowed = true,
    bool gameplayAllowed = true,
    String policyId = RuntimeControllerInputPolicies
        .retroArchMacosSingleControllerMfi8BitDoPro2UsbGameplayV1,
    List<LaunchPlayerController>? additionalPlayers,
  }) => LaunchControllerSetup(
    playerControllers: <LaunchPlayerController>[
      player(
        resolvedSlot: exactSlot(playerSlot: 0),
        mapping: BuiltinControllerTemplates.defaultMapping,
        policyId: policyId,
        shortcutsAllowed: shortcutsAllowed,
        gameplayAllowed: gameplayAllowed,
        gameplayMapping: gameplayMapping,
      ),
      ...?additionalPlayers,
    ],
  );

  List<String> ownedGameplayLines(String cfg) {
    const digital = <String>{
      'b',
      'y',
      'select',
      'start',
      'up',
      'down',
      'left',
      'right',
      'a',
      'x',
      'l',
      'r',
      'l2',
      'r2',
      'l3',
      'r3',
    };
    const axes = <String>{
      'l_x_minus',
      'l_x_plus',
      'l_y_minus',
      'l_y_plus',
      'r_x_minus',
      'r_x_plus',
      'r_y_minus',
      'r_y_plus',
    };
    return cfg
        .split('\n')
        .where((line) {
          final match = RegExp(
            r'^input_player1_(.+)_(btn|axis) = ',
          ).firstMatch(line);
          return match != null &&
              (digital.contains(match.group(1)) ||
                  axes.contains(match.group(1)));
        })
        .toList(growable: false);
  }

  LaunchControllerSetup setup({
    ControllerMapping mapping = BuiltinControllerTemplates.defaultMapping,
    List<ResolvedControllerSlot>? controllerSlots,
    List<int> padDeviceIndices = const <int>[],
    Set<int> reservedPlayerSlots = const <int>{},
  }) => LaunchControllerSetup(
    mapping: mapping,
    controllerSlots:
        controllerSlots ??
        <ResolvedControllerSlot>[
          for (final (playerSlot, runtimeIndex) in padDeviceIndices.indexed)
            slot(playerSlot: playerSlot, runtimeIndex: runtimeIndex),
        ],
    reservedPlayerSlots: reservedPlayerSlots,
  );

  test(
    'points saves/states/screenshots at ROMD dirs and owns the config',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();

      final path = await writer.write(resolved);

      expect(path, writer.configPathFor(resolved.configRoot));
      final cfg = await File(path).readAsString();
      expect(cfg, contains('config_save_on_exit = "false"'));
      expect(cfg, contains('savefile_directory = "${resolved.saveRoot}"'));
      expect(cfg, contains('savestate_directory = "${resolved.stateRoot}"'));
      expect(cfg, contains('screenshot_directory = "${resolved.configRoot}"'));
      expect(cfg, contains('history_list_enable = "false"'));
      expect(cfg, contains('content_runtime_log = "false"'));
      // NCI is no longer generated (the two-process overlay was abandoned).
      expect(cfg, isNot(contains('network_cmd_enable')));
    },
  );

  test(
    'N64 owns exact Mupen core options and disables inherited remap state',
    () async {
      final resolved = target(platformShortName: 'n64');
      const writer = RetroArchConfigWriter();

      final cfg = await File(await writer.write(resolved)).readAsString();
      final coreOptionsPath = writer.n64CoreOptionsPathFor(resolved.configRoot);
      final remapDirectory = writer.remapDirectoryFor(resolved.configRoot);

      expect(
        cfg,
        contains(
          'input_autodetect_enable = "true"\n'
          'core_options_path = "$coreOptionsPath"\n'
          'auto_remaps_enable = "false"\n'
          'auto_overrides_enable = "false"\n'
          'game_specific_options = "false"\n'
          'remap_save_on_exit = "false"\n'
          'input_remapping_directory = "$remapDirectory"\n'
          'menu_driver = "rgui"\n',
        ),
      );
      expect(cfg, contains('config_save_on_exit = "false"'));
      expect(
        await File(coreOptionsPath).readAsString(),
        'mupen64plus-alt-map = "False"\n'
        'mupen64plus-r-cbutton = "C1"\n'
        'mupen64plus-l-cbutton = "C2"\n'
        'mupen64plus-d-cbutton = "C3"\n'
        'mupen64plus-u-cbutton = "C4"\n',
      );
      expect(Directory(remapDirectory).existsSync(), isTrue);
    },
  );

  test(
    'N64 core options and remaps stay inside each resolved config root',
    () async {
      const writer = RetroArchConfigWriter();
      final first = target(
        platformShortName: ' N64 ',
        configRoot: p.join(tmp.path, 'config', 'n64', 'first'),
      );
      final second = target(
        platformShortName: 'n64',
        configRoot: p.join(tmp.path, 'config', 'n64', 'second'),
      );

      final firstCfg = await File(await writer.write(first)).readAsString();
      final secondCfg = await File(await writer.write(second)).readAsString();
      final firstOptions = writer.n64CoreOptionsPathFor(first.configRoot);
      final secondOptions = writer.n64CoreOptionsPathFor(second.configRoot);
      final firstRemaps = writer.remapDirectoryFor(first.configRoot);
      final secondRemaps = writer.remapDirectoryFor(second.configRoot);

      expect(p.isWithin(first.configRoot, firstOptions), isTrue);
      expect(p.isWithin(first.configRoot, firstRemaps), isTrue);
      expect(p.isWithin(second.configRoot, secondOptions), isTrue);
      expect(p.isWithin(second.configRoot, secondRemaps), isTrue);
      expect(firstOptions, isNot(secondOptions));
      expect(firstRemaps, isNot(secondRemaps));
      expect(firstCfg, contains('core_options_path = "$firstOptions"'));
      expect(secondCfg, contains('core_options_path = "$secondOptions"'));
      expect(File(firstOptions).existsSync(), isTrue);
      expect(File(secondOptions).existsSync(), isTrue);
    },
  );

  test(
    'non-N64 output does not create or reference N64 policy files',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();

      final cfg = await File(await writer.write(resolved)).readAsString();

      expect(cfg, isNot(contains('core_options_path')));
      expect(cfg, isNot(contains('auto_remaps_enable')));
      expect(cfg, isNot(contains('auto_overrides_enable')));
      expect(cfg, isNot(contains('game_specific_options')));
      expect(cfg, isNot(contains('remap_save_on_exit')));
      expect(cfg, isNot(contains('input_remapping_directory')));
      expect(
        File(writer.n64CoreOptionsPathFor(resolved.configRoot)).existsSync(),
        isFalse,
      );
      expect(
        Directory(writer.remapDirectoryFor(resolved.configRoot)).existsSync(),
        isFalse,
      );
    },
  );

  test(
    'Genesis Plus GX emits exact joypad policy and title isolation bytes',
    () async {
      final resolved = target(platformShortName: 'genesis');
      const writer = RetroArchConfigWriter();
      final remapDirectory = writer.remapDirectoryFor(resolved.configRoot);

      final cfg = await File(
        await writer.write(
          resolved,
          coreId: 'genesis_plus_gx',
          controllerSetup: gameplaySetup(
            gameplayMapping: hardwareMapping(calibratedBindings()),
          ),
        ),
      ).readAsString();

      expect(
        cfg,
        contains(
          'input_autodetect_enable = "true"\n'
          'input_libretro_device_p1 = "1"\n'
          'input_player1_analog_dpad_mode = "1"\n'
          'auto_remaps_enable = "false"\n'
          'auto_overrides_enable = "false"\n'
          'game_specific_options = "false"\n'
          'remap_save_on_exit = "false"\n'
          'input_remapping_directory = "$remapDirectory"\n'
          'input_joypad_driver = "mfi"\n'
          'menu_driver = "rgui"\n',
        ),
      );
      expect(Directory(remapDirectory).existsSync(), isTrue);
      expect(
        File(writer.n64CoreOptionsPathFor(resolved.configRoot)).existsSync(),
        isFalse,
      );
      expect(cfg, isNot(contains('core_options_path')));
    },
  );

  test(
    'Genesis Plus GX remaps stay inside each resolved config root',
    () async {
      const writer = RetroArchConfigWriter();
      final first = target(
        platformShortName: ' GENESIS ',
        configRoot: p.join(tmp.path, 'config', 'genesis', 'first'),
      );
      final second = target(
        platformShortName: 'genesis',
        configRoot: p.join(tmp.path, 'config', 'genesis', 'second'),
      );

      final firstCfg = await File(
        await writer.write(
          first,
          coreId: ' genesis_plus_gx ',
          controllerSetup: gameplaySetup(
            gameplayMapping: hardwareMapping(calibratedBindings()),
          ),
        ),
      ).readAsString();
      final secondCfg = await File(
        await writer.write(
          second,
          coreId: 'genesis_plus_gx',
          controllerSetup: gameplaySetup(
            gameplayMapping: hardwareMapping(calibratedBindings()),
          ),
        ),
      ).readAsString();
      final firstRemaps = writer.remapDirectoryFor(first.configRoot);
      final secondRemaps = writer.remapDirectoryFor(second.configRoot);

      expect(p.isWithin(first.configRoot, firstRemaps), isTrue);
      expect(p.isWithin(second.configRoot, secondRemaps), isTrue);
      expect(firstRemaps, isNot(secondRemaps));
      expect(firstCfg, contains('input_remapping_directory = "$firstRemaps"'));
      expect(
        secondCfg,
        contains('input_remapping_directory = "$secondRemaps"'),
      );
      expect(Directory(firstRemaps).existsSync(), isTrue);
      expect(Directory(secondRemaps).existsSync(), isTrue);
    },
  );

  test(
    'Genesis Plus GX stays byte-identical outside the gameplay envelope',
    () async {
      const writer = RetroArchConfigWriter();
      final resolved = target(platformShortName: 'genesis');
      final mapping = hardwareMapping(calibratedBindings());
      final secondPlayer = player(
        resolvedSlot: exactSlot(playerSlot: 1, providerOrdinal: 8),
        mapping: BuiltinControllerTemplates.defaultMapping,
        policyId: RuntimeControllerInputPolicies
            .retroArchMacosSingleControllerMfi8BitDoPro2UsbGameplayV1,
        runtimeIndex: 1,
        shortcutsAllowed: true,
        gameplayAllowed: true,
        gameplayMapping: mapping,
      );
      final rejectedSetups = <String, LaunchControllerSetup>{
        'empty': LaunchControllerSetup.defaults,
        'fallback': setup(padDeviceIndices: const <int>[0]),
        'multi-controller': gameplaySetup(
          gameplayMapping: mapping,
          additionalPlayers: <LaunchPlayerController>[secondPlayer],
        ),
        'gameplay-disabled': gameplaySetup(
          gameplayMapping: mapping,
          gameplayAllowed: false,
        ),
      };

      for (final entry in rejectedSetups.entries) {
        final baseline = await File(
          await writer.write(resolved, controllerSetup: entry.value),
        ).readAsString();
        final withGenesisCore = await File(
          await writer.write(
            resolved,
            coreId: 'genesis_plus_gx',
            controllerSetup: entry.value,
          ),
        ).readAsString();
        expect(withGenesisCore, baseline, reason: entry.key);
      }

      expect(
        Directory(writer.remapDirectoryFor(resolved.configRoot)).existsSync(),
        isFalse,
      );
    },
  );

  test(
    'PicoDrive and missing or unknown Genesis cores omit the GPGX policy',
    () async {
      const writer = RetroArchConfigWriter();
      final resolved = target(platformShortName: 'genesis');
      final approvedSetup = gameplaySetup(
        gameplayMapping: hardwareMapping(calibratedBindings()),
      );

      final baseline = await File(
        await writer.write(resolved, controllerSetup: approvedSetup),
      ).readAsString();
      for (final coreId in <String?>['picodrive', 'unknown', null]) {
        final cfg = await File(
          await writer.write(
            resolved,
            coreId: coreId,
            controllerSetup: approvedSetup,
          ),
        ).readAsString();
        expect(cfg, baseline, reason: 'coreId=$coreId');
      }

      expect(baseline, isNot(contains('input_libretro_device_p1')));
      expect(baseline, isNot(contains('input_player1_analog_dpad_mode')));
      expect(baseline, isNot(contains('auto_remaps_enable')));
      expect(
        Directory(writer.remapDirectoryFor(resolved.configRoot)).existsSync(),
        isFalse,
      );
    },
  );

  test('Genesis core id cannot change non-Genesis output bytes', () async {
    const writer = RetroArchConfigWriter();
    final resolved = target();
    final approvedSetup = gameplaySetup(
      gameplayMapping: hardwareMapping(calibratedBindings()),
    );

    final baseline = await File(
      await writer.write(resolved, controllerSetup: approvedSetup),
    ).readAsString();
    final withGenesisCore = await File(
      await writer.write(
        resolved,
        coreId: 'genesis_plus_gx',
        controllerSetup: approvedSetup,
      ),
    ).readAsString();

    expect(withGenesisCore, baseline);
  });

  test(
    'generates a quiet kiosk session with no startup/status popups',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();

      final cfg = await File(await writer.write(resolved)).readAsString();

      expect(cfg, contains('video_fullscreen = "true"'));
      expect(cfg, contains('video_windowed_fullscreen = "true"'));
      expect(cfg, contains('video_font_enable = "false"'));
      expect(cfg, contains('menu_enable_widgets = "false"'));
      expect(cfg, contains('menu_show_load_content_animation = "false"'));
      expect(cfg, contains('input_overlay_enable = "false"'));
      expect(cfg, contains('input_autodetect_enable = "true"'));
      expect(cfg, contains('notification_show_autoconfig = "false"'));
      expect(cfg, contains('notification_show_autoconfig_fails = "false"'));
      expect(cfg, contains('notification_show_config_override_load = "false"'));
      expect(cfg, contains('notification_show_remap_load = "false"'));
      expect(cfg, contains('notification_show_save_state = "false"'));
      expect(cfg, contains('notification_show_screenshot = "false"'));
      expect(cfg, contains('notification_show_refresh_rate = "false"'));
      expect(cfg, contains('notification_show_when_menu_is_alive = "false"'));
    },
  );

  test('appends system_directory only when one is passed, leaving the '
      'BIOS-free output byte-identical', () async {
    final resolved = target();
    const writer = RetroArchConfigWriter();
    final biosDir = p.join(tmp.path, 'bios', 'psx');

    final without = await File(await writer.write(resolved)).readAsString();
    final withBios = await File(
      await writer.write(resolved, systemDirectory: biosDir),
    ).readAsString();

    expect(without, isNot(contains('system_directory')));
    // The line is appended last, so everything before it is byte-identical
    // to the BIOS-free config.
    expect(
      withBios,
      '${without.substring(0, without.length - 1)}\n'
      'system_directory = "$biosDir"\n',
    );
  });

  test(
    'generates the SELECT-modifier in-game hotkey scheme + Quick Menu',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();

      final cfg = await File(await writer.write(resolved)).readAsString();

      // rgui (asset-free) + single-press quit so the menu Quit returns to ROMD.
      expect(cfg, contains('menu_driver = "rgui"'));
      expect(cfg, contains('rgui_show_start_screen = "false"'));
      expect(cfg, contains('quit_press_twice = "false"'));
      expect(cfg, contains('quit_on_close_content = "2"'));
      // SELECT (2) is the hotkey modifier; Start (3) opens the Quick Menu.
      expect(cfg, contains('input_enable_hotkey_btn = "2"'));
      expect(cfg, contains('input_menu_toggle_gamepad_combo = "0"'));
      expect(cfg, contains('input_quit_gamepad_combo = "0"'));
      expect(cfg, contains('input_menu_toggle_btn = "3"'));
      // Save/Load on R/L, slot on ◂ ▸, screenshot on X.
      expect(cfg, contains('input_save_state_btn = "11"'));
      expect(cfg, contains('input_load_state_btn = "10"'));
      expect(cfg, contains('input_state_slot_increase_btn = "7"'));
      expect(cfg, contains('input_state_slot_decrease_btn = "6"'));
      expect(cfg, contains('input_screenshot_btn = "9"'));
      // Unbound-by-default actions emit no bind at all.
      expect(cfg, isNot(contains('input_exit_emulator_btn')));
      expect(cfg, isNot(contains('input_hold_fast_forward_btn')));
      expect(cfg, isNot(contains('input_pause_toggle_btn')));
    },
  );

  test('multi-pad uncorrelated output is byte-identical across provider '
      'ordinals', () async {
    final resolved = target();
    const writer = RetroArchConfigWriter();

    final first = await File(
      await writer.write(
        resolved,
        controllerSetup: setup(padDeviceIndices: const <int>[1, 0]),
      ),
    ).readAsString();
    final varied = await File(
      await writer.write(
        resolved,
        controllerSetup: setup(padDeviceIndices: const <int>[9, 4]),
      ),
    ).readAsString();

    expect(first, varied);
    expect(first, isNot(contains('joypad_index')));
    expect(first, contains('input_enable_hotkey_btn = "nul"'));
  });

  test(
    'production single-controller policy projects its exact SDL pad to port 1',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();

      final cfg = await File(
        await writer.write(
          resolved,
          controllerSetup: LaunchControllerSetup(
            playerControllers: <LaunchPlayerController>[
              player(
                resolvedSlot: exactSlot(playerSlot: 0),
                mapping: BuiltinControllerTemplates.defaultMapping,
              ),
            ],
          ),
        ),
      ).readAsString();

      expect(cfg, contains('input_player1_joypad_index = "0"'));
      expect(cfg, isNot(contains('input_player2_joypad_index')));
      expect(cfg, isNot(contains('input_joypad_driver')));
      expect(cfg, contains('input_enable_hotkey_btn = "nul"'));
      expect(cfg, contains('input_menu_toggle_btn = "nul"'));
    },
  );

  test(
    'calibrated MFi 8BitDo policy emits the reproduced physical hotkeys',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();

      final cfg = await File(
        await writer.write(
          resolved,
          controllerSetup: LaunchControllerSetup(
            playerControllers: <LaunchPlayerController>[
              player(
                resolvedSlot: exactSlot(playerSlot: 0),
                mapping: BuiltinControllerTemplates.defaultMapping,
                policyId: RuntimeControllerInputPolicies
                    .retroArchMacosSingleControllerMfi8BitDoPro2Usb,
                shortcutsAllowed: true,
              ),
            ],
          ),
        ),
      ).readAsString();

      expect(cfg, contains('input_player1_joypad_index = "0"'));
      expect(cfg, contains('input_joypad_driver = "mfi"'));
      expect(cfg, contains('input_enable_hotkey_btn = "2"'));
      expect(cfg, contains('input_menu_toggle_btn = "3"'));
      expect(cfg, contains('input_save_state_btn = "11"'));
      expect(cfg, contains('input_load_state_btn = "10"'));
      expect(cfg, contains('input_state_slot_increase_btn = "7"'));
      expect(cfg, contains('input_state_slot_decrease_btn = "6"'));
      expect(cfg, contains('input_screenshot_btn = "1"'));
      expect(cfg, isNot(contains('input_screenshot_btn = "9"')));
      expect(cfg, contains('input_exit_emulator_btn = "nul"'));
      expect(cfg, contains('input_hold_fast_forward_btn = "nul"'));
      expect(cfg, contains('input_pause_toggle_btn = "nul"'));
    },
  );

  test(
    'canonical gameplay mapping cannot broaden any current RetroArch policy',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();
      final gameplayMapping = ControllerHardwareMapping(
        sdlPlatform: 'macOS',
        sdlGuid:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbSdlGuid,
        displayName:
            RuntimeControllerInputPolicies.retroArchMfi8BitDoPro2UsbDisplayName,
        mapping: CanonicalControllerMapping(
          const <CanonicalGamepadControl, RawGamepadInput>{
            CanonicalGamepadControl.faceSouth: RawButtonInput(7),
            CanonicalGamepadControl.leftStickX: RawAxisInput(3),
          },
        ),
        createdAt: DateTime.utc(2026, 7, 13),
        updatedAt: DateTime.utc(2026, 7, 13),
      );

      Future<String> write({
        required bool calibrated,
        required bool withGameplayMapping,
        bool multiController = false,
      }) async {
        LaunchPlayerController configuredPlayer(int playerSlot) => player(
          resolvedSlot: exactSlot(playerSlot: playerSlot),
          mapping: BuiltinControllerTemplates.defaultMapping,
          policyId: calibrated
              ? RuntimeControllerInputPolicies
                    .retroArchMacosSingleControllerMfi8BitDoPro2Usb
              : RuntimeControllerInputPolicies.retroArchMacosSingleController,
          shortcutsAllowed: calibrated,
          gameplayMapping: withGameplayMapping ? gameplayMapping : null,
        );
        return File(
          await writer.write(
            resolved,
            controllerSetup: LaunchControllerSetup(
              playerControllers: <LaunchPlayerController>[
                configuredPlayer(0),
                if (multiController) configuredPlayer(1),
              ],
            ),
          ),
        ).readAsString();
      }

      for (final (calibrated, multiController) in <(bool, bool)>[
        (false, false),
        (true, false),
        (true, true),
      ]) {
        final withoutGameplay = await write(
          calibrated: calibrated,
          withGameplayMapping: false,
          multiController: multiController,
        );
        final withGameplay = await write(
          calibrated: calibrated,
          withGameplayMapping: true,
          multiController: multiController,
        );
        expect(
          withGameplay,
          withoutGameplay,
          reason:
              'Gameplay mapping must remain inert for calibrated=$calibrated, '
              'multiController=$multiController',
        );
      }
    },
  );

  test(
    'gameplay-v1 emits the exhaustive calibrated 48-line golden block',
    () async {
      final cfg = await File(
        await const RetroArchConfigWriter().write(
          target(),
          controllerSetup: gameplaySetup(
            gameplayMapping: hardwareMapping(calibratedBindings()),
          ),
        ),
      ).readAsString();

      expect(ownedGameplayLines(cfg), <String>[
        'input_player1_b_btn = "8"',
        'input_player1_b_axis = "nul"',
        'input_player1_y_btn = "9"',
        'input_player1_y_axis = "nul"',
        'input_player1_select_btn = "2"',
        'input_player1_select_axis = "nul"',
        'input_player1_start_btn = "3"',
        'input_player1_start_axis = "nul"',
        'input_player1_up_btn = "4"',
        'input_player1_up_axis = "nul"',
        'input_player1_down_btn = "5"',
        'input_player1_down_axis = "nul"',
        'input_player1_left_btn = "6"',
        'input_player1_left_axis = "nul"',
        'input_player1_right_btn = "7"',
        'input_player1_right_axis = "nul"',
        'input_player1_a_btn = "0"',
        'input_player1_a_axis = "nul"',
        'input_player1_x_btn = "1"',
        'input_player1_x_axis = "nul"',
        'input_player1_l_btn = "10"',
        'input_player1_l_axis = "nul"',
        'input_player1_r_btn = "11"',
        'input_player1_r_axis = "nul"',
        'input_player1_l2_btn = "12"',
        'input_player1_l2_axis = "nul"',
        'input_player1_r2_btn = "13"',
        'input_player1_r2_axis = "nul"',
        'input_player1_l3_btn = "14"',
        'input_player1_l3_axis = "nul"',
        'input_player1_r3_btn = "15"',
        'input_player1_r3_axis = "nul"',
        'input_player1_l_x_minus_btn = "nul"',
        'input_player1_l_x_minus_axis = "-0"',
        'input_player1_l_x_plus_btn = "nul"',
        'input_player1_l_x_plus_axis = "+0"',
        'input_player1_l_y_minus_btn = "nul"',
        'input_player1_l_y_minus_axis = "+1"',
        'input_player1_l_y_plus_btn = "nul"',
        'input_player1_l_y_plus_axis = "-1"',
        'input_player1_r_x_minus_btn = "nul"',
        'input_player1_r_x_minus_axis = "-2"',
        'input_player1_r_x_plus_btn = "nul"',
        'input_player1_r_x_plus_axis = "+2"',
        'input_player1_r_y_minus_btn = "nul"',
        'input_player1_r_y_minus_axis = "+3"',
        'input_player1_r_y_plus_btn = "nul"',
        'input_player1_r_y_plus_axis = "-3"',
      ]);
      expect(cfg, isNot(contains('input_player1_guide_')));
      expect(cfg, contains('input_joypad_driver = "mfi"'));
      expect(cfg, contains('input_player1_joypad_index = "0"'));
    },
  );

  test(
    'SDL3 positional face mapping reaches the calibrated physical MFi buttons',
    () async {
      final cfg = await File(
        await const RetroArchConfigWriter().write(
          target(),
          controllerSetup: gameplaySetup(
            gameplayMapping:
                hardwareMapping(<CanonicalGamepadControl, RawGamepadInput>{
                  CanonicalGamepadControl.faceSouth: const RawButtonInput(1),
                  CanonicalGamepadControl.faceEast: const RawButtonInput(0),
                  CanonicalGamepadControl.faceWest: const RawButtonInput(3),
                  CanonicalGamepadControl.faceNorth: const RawButtonInput(2),
                }),
          ),
        ),
      ).readAsString();

      expect(cfg, contains('input_player1_b_btn = "8"'));
      expect(cfg, contains('input_player1_a_btn = "0"'));
      expect(cfg, contains('input_player1_y_btn = "9"'));
      expect(cfg, contains('input_player1_x_btn = "1"'));
    },
  );

  test(
    'gameplay-v1 translates button, shoulder, hat, and stick-direction swaps',
    () async {
      final mapping =
          hardwareMapping(<CanonicalGamepadControl, RawGamepadInput>{
            CanonicalGamepadControl.faceSouth: const RawButtonInput(0),
            CanonicalGamepadControl.faceEast: const RawButtonInput(1),
            CanonicalGamepadControl.leftShoulder: const RawButtonInput(10),
            CanonicalGamepadControl.rightShoulder: const RawButtonInput(9),
            CanonicalGamepadControl.dpadUp: const RawAxisInput(
              0,
              direction: RawAxisDirection.positive,
            ),
            CanonicalGamepadControl.dpadDown: const RawAxisInput(
              1,
              direction: RawAxisDirection.positive,
            ),
            CanonicalGamepadControl.dpadLeft: const RawHatInput(0, 2),
            CanonicalGamepadControl.dpadRight: const RawHatInput(0, 8),
          });

      final cfg = await File(
        await const RetroArchConfigWriter().write(
          target(),
          controllerSetup: gameplaySetup(gameplayMapping: mapping),
        ),
      ).readAsString();

      expect(cfg, contains('input_player1_b_btn = "0"'));
      expect(cfg, contains('input_player1_a_btn = "8"'));
      expect(cfg, contains('input_player1_l_btn = "11"'));
      expect(cfg, contains('input_player1_r_btn = "10"'));
      expect(cfg, contains('input_player1_up_btn = "nul"'));
      expect(cfg, contains('input_player1_up_axis = "+0"'));
      expect(cfg, contains('input_player1_down_axis = "-1"'));
      expect(cfg, contains('input_player1_left_btn = "7"'));
      expect(cfg, contains('input_player1_right_btn = "6"'));
    },
  );

  test(
    'gameplay-v1 swaps stick sources and composes custom inversion',
    () async {
      final mapping =
          hardwareMapping(<CanonicalGamepadControl, RawGamepadInput>{
            CanonicalGamepadControl.leftStickX: const RawAxisInput(
              2,
              inverted: true,
            ),
            CanonicalGamepadControl.leftStickY: const RawAxisInput(
              3,
              inverted: true,
            ),
            CanonicalGamepadControl.rightStickX: const RawAxisInput(0),
            CanonicalGamepadControl.rightStickY: const RawAxisInput(1),
          });

      final cfg = await File(
        await const RetroArchConfigWriter().write(
          target(),
          controllerSetup: gameplaySetup(gameplayMapping: mapping),
        ),
      ).readAsString();

      expect(cfg, contains('input_player1_l_x_minus_axis = "+2"'));
      expect(cfg, contains('input_player1_l_x_plus_axis = "-2"'));
      expect(cfg, contains('input_player1_l_y_minus_axis = "-3"'));
      expect(cfg, contains('input_player1_l_y_plus_axis = "+3"'));
      expect(cfg, contains('input_player1_r_x_minus_axis = "-0"'));
      expect(cfg, contains('input_player1_r_x_plus_axis = "+0"'));
      expect(cfg, contains('input_player1_r_y_minus_axis = "+1"'));
      expect(cfg, contains('input_player1_r_y_plus_axis = "-1"'));
    },
  );

  test(
    'missing canonical controls explicitly unbind both alternatives',
    () async {
      final cfg = await File(
        await const RetroArchConfigWriter().write(
          target(),
          controllerSetup: gameplaySetup(
            gameplayMapping: hardwareMapping(
              <CanonicalGamepadControl, RawGamepadInput>{
                CanonicalGamepadControl.faceSouth: const RawButtonInput(1),
              },
            ),
          ),
        ),
      ).readAsString();

      expect(ownedGameplayLines(cfg), hasLength(48));
      expect(cfg, contains('input_player1_b_btn = "8"'));
      expect(cfg, contains('input_player1_b_axis = "nul"'));
      expect(cfg, contains('input_player1_a_btn = "nul"'));
      expect(cfg, contains('input_player1_a_axis = "nul"'));
      expect(cfg, contains('input_player1_l_x_minus_btn = "nul"'));
      expect(cfg, contains('input_player1_l_x_minus_axis = "nul"'));
      expect(cfg, contains('input_player1_l_x_plus_btn = "nul"'));
      expect(cfg, contains('input_player1_l_x_plus_axis = "nul"'));
    },
  );

  test(
    'a later projection clears every stale owned gameplay alternative',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();
      await writer.write(
        resolved,
        controllerSetup: gameplaySetup(
          gameplayMapping: hardwareMapping(calibratedBindings()),
        ),
      );

      final second = await File(
        await writer.write(
          resolved,
          controllerSetup: gameplaySetup(
            gameplayMapping: hardwareMapping(
              <CanonicalGamepadControl, RawGamepadInput>{
                CanonicalGamepadControl.faceSouth: const RawButtonInput(0),
              },
            ),
          ),
        ),
      ).readAsString();

      expect(ownedGameplayLines(second), hasLength(48));
      expect(second, contains('input_player1_b_btn = "0"'));
      expect(second, isNot(contains('input_player1_b_btn = "8"')));
      expect(second, contains('input_player1_r2_btn = "nul"'));
      expect(second, isNot(contains('input_player1_r2_btn = "13"')));
      expect(second, contains('input_player1_r_y_plus_axis = "nul"'));
      expect(second, isNot(contains('input_player1_r_y_plus_axis = "-3"')));
    },
  );

  test(
    'one unknown raw source suppresses the entire custom projection',
    () async {
      final bindings = calibratedBindings();
      bindings[CanonicalGamepadControl.faceSouth] = const RawButtonInput(42);
      final cfg = await File(
        await const RetroArchConfigWriter().write(
          target(),
          controllerSetup: gameplaySetup(
            gameplayMapping: hardwareMapping(bindings),
          ),
        ),
      ).readAsString();

      expect(ownedGameplayLines(cfg), isEmpty);
      expect(cfg, isNot(contains('input_player1_b_btn')));
      expect(cfg, contains('input_player1_joypad_index = "0"'));
      expect(cfg, contains('input_joypad_driver = "mfi"'));
    },
  );

  test(
    'negative or inverted trigger sources fail the whole projection closed',
    () async {
      for (final trigger in <RawAxisInput>[
        const RawAxisInput(4, direction: RawAxisDirection.negative),
        const RawAxisInput(4, inverted: true),
        const RawAxisInput(
          4,
          direction: RawAxisDirection.positive,
          inverted: true,
        ),
      ]) {
        final cfg = await File(
          await const RetroArchConfigWriter().write(
            target(),
            controllerSetup: gameplaySetup(
              gameplayMapping: hardwareMapping(
                <CanonicalGamepadControl, RawGamepadInput>{
                  CanonicalGamepadControl.leftTrigger: trigger,
                },
              ),
            ),
          ),
        ).readAsString();

        expect(ownedGameplayLines(cfg), isEmpty, reason: '$trigger');
      }
    },
  );

  test('gameplay metadata must match the calibrated macOS GUID', () async {
    for (final mapping in <ControllerHardwareMapping>[
      hardwareMapping(calibratedBindings(), sdlPlatform: 'Linux'),
      hardwareMapping(calibratedBindings(), sdlGuid: 'wrong-guid'),
    ]) {
      final cfg = await File(
        await const RetroArchConfigWriter().write(
          target(),
          controllerSetup: gameplaySetup(gameplayMapping: mapping),
        ),
      ).readAsString();
      expect(ownedGameplayLines(cfg), isEmpty);
    }
  });

  test(
    'gameplay-v1 rejects missing mapping and every non-production input shape',
    () async {
      final mapping = hardwareMapping(calibratedBindings());

      Future<String> write({
        ResolvedControllerSlot? resolvedSlot,
        int runtimeIndex = 0,
        RuntimeControllerInputProvider provider =
            RuntimeControllerInputProvider.sdl3,
        RuntimeControllerInputCorrelation correlation =
            RuntimeControllerInputCorrelation.verified,
        ControllerHardwareMapping? gameplayMapping,
      }) async => File(
        await const RetroArchConfigWriter().write(
          target(),
          controllerSetup: LaunchControllerSetup(
            playerControllers: <LaunchPlayerController>[
              player(
                resolvedSlot: resolvedSlot ?? exactSlot(playerSlot: 0),
                mapping: BuiltinControllerTemplates.defaultMapping,
                policyId: RuntimeControllerInputPolicies
                    .retroArchMacosSingleControllerMfi8BitDoPro2UsbGameplayV1,
                runtimeIndex: runtimeIndex,
                provider: provider,
                correlation: correlation,
                shortcutsAllowed: true,
                gameplayAllowed: true,
                gameplayMapping: gameplayMapping,
              ),
            ],
          ),
        ),
      ).readAsString();

      final rejected = <String>[
        await write(),
        await write(
          resolvedSlot: exactSlot(playerSlot: 0, serial: null),
          gameplayMapping: mapping,
        ),
        await write(
          resolvedSlot: exactSlot(playerSlot: 0, displayName: 'Wrong Pad'),
          gameplayMapping: mapping,
        ),
        await write(
          resolvedSlot: exactSlot(playerSlot: 0, sdlGuid: 'wrong-guid'),
          gameplayMapping: mapping,
        ),
        await write(runtimeIndex: 1, gameplayMapping: mapping),
        await write(
          provider: RuntimeControllerInputProvider.other,
          gameplayMapping: mapping,
        ),
        await write(
          correlation: RuntimeControllerInputCorrelation.acceptedBestEffort,
          gameplayMapping: mapping,
        ),
      ];

      for (final cfg in rejected) {
        expect(ownedGameplayLines(cfg), isEmpty);
      }
    },
  );

  test('shortcut and gameplay permissions remain independent', () async {
    for (final permissions in <({bool shortcuts, bool gameplay})>[
      (shortcuts: false, gameplay: false),
      (shortcuts: true, gameplay: false),
      (shortcuts: false, gameplay: true),
      (shortcuts: true, gameplay: true),
    ]) {
      final cfg = await File(
        await const RetroArchConfigWriter().write(
          target(),
          controllerSetup: gameplaySetup(
            gameplayMapping: hardwareMapping(calibratedBindings()),
            shortcutsAllowed: permissions.shortcuts,
            gameplayAllowed: permissions.gameplay,
          ),
        ),
      ).readAsString();

      expect(
        cfg.contains('input_enable_hotkey_btn = "2"'),
        permissions.shortcuts,
        reason: '$permissions',
      );
      expect(
        ownedGameplayLines(cfg).isNotEmpty,
        permissions.gameplay,
        reason: '$permissions',
      );
    }
  });

  test(
    'gameplay permission cannot broaden old policies or multi-pad output',
    () async {
      final mapping = hardwareMapping(calibratedBindings());
      final oldPolicy = await File(
        await const RetroArchConfigWriter().write(
          target(),
          controllerSetup: gameplaySetup(
            gameplayMapping: mapping,
            policyId: RuntimeControllerInputPolicies
                .retroArchMacosSingleControllerMfi8BitDoPro2Usb,
          ),
        ),
      ).readAsString();
      final secondPlayer = player(
        resolvedSlot: exactSlot(playerSlot: 1, providerOrdinal: 8),
        mapping: BuiltinControllerTemplates.defaultMapping,
        policyId: RuntimeControllerInputPolicies
            .retroArchMacosSingleControllerMfi8BitDoPro2UsbGameplayV1,
        shortcutsAllowed: true,
        gameplayAllowed: true,
        gameplayMapping: mapping,
      );
      final multi = await File(
        await const RetroArchConfigWriter().write(
          target(),
          controllerSetup: gameplaySetup(
            gameplayMapping: mapping,
            additionalPlayers: <LaunchPlayerController>[secondPlayer],
          ),
        ),
      ).readAsString();

      expect(ownedGameplayLines(oldPolicy), isEmpty);
      expect(ownedGameplayLines(multi), isEmpty);
      expect(multi, isNot(contains('joypad_index')));
    },
  );

  test(
    'gameplay-v1 with gameplay disabled preserves calibrated and multi-pad bytes',
    () async {
      final mapping = hardwareMapping(calibratedBindings());
      final resolved = target();
      const writer = RetroArchConfigWriter();
      final oldCalibrated = await File(
        await writer.write(
          resolved,
          controllerSetup: gameplaySetup(
            gameplayMapping: mapping,
            gameplayAllowed: false,
            policyId: RuntimeControllerInputPolicies
                .retroArchMacosSingleControllerMfi8BitDoPro2Usb,
          ),
        ),
      ).readAsString();
      final gameplayDisabled = await File(
        await writer.write(
          resolved,
          controllerSetup: gameplaySetup(
            gameplayMapping: mapping,
            gameplayAllowed: false,
          ),
        ),
      ).readAsString();
      expect(gameplayDisabled, oldCalibrated);

      final gameplayPlayer = player(
        resolvedSlot: exactSlot(playerSlot: 1, providerOrdinal: 8),
        mapping: BuiltinControllerTemplates.defaultMapping,
        policyId: RuntimeControllerInputPolicies
            .retroArchMacosSingleControllerMfi8BitDoPro2UsbGameplayV1,
        shortcutsAllowed: true,
        gameplayAllowed: true,
        gameplayMapping: mapping,
      );
      final gameplayMulti = await File(
        await writer.write(
          resolved,
          controllerSetup: gameplaySetup(
            gameplayMapping: mapping,
            additionalPlayers: <LaunchPlayerController>[gameplayPlayer],
          ),
        ),
      ).readAsString();
      final compatibilityMulti = await File(
        await writer.write(
          resolved,
          controllerSetup: setup(padDeviceIndices: const <int>[0, 1]),
        ),
      ).readAsString();
      expect(gameplayMulti, compatibilityMulti);
    },
  );

  test(
    'calibrated policy nuls a binding outside its measured positions',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();
      final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
        const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
          RomdAction.screenshot: GamepadButtonPosition.faceEast,
        }),
      );

      final cfg = await File(
        await writer.write(
          resolved,
          controllerSetup: LaunchControllerSetup(
            playerControllers: <LaunchPlayerController>[
              player(
                resolvedSlot: exactSlot(playerSlot: 0),
                mapping: mapping,
                policyId: RuntimeControllerInputPolicies
                    .retroArchMacosSingleControllerMfi8BitDoPro2Usb,
                shortcutsAllowed: true,
              ),
            ],
          ),
        ),
      ).readAsString();

      expect(cfg, contains('input_screenshot_btn = "nul"'));
      expect(cfg, contains('input_save_state_btn = "11"'));
    },
  );

  test('calibrated policy rejects incomplete or mismatched identity', () async {
    final resolved = target();
    const writer = RetroArchConfigWriter();

    for (final slot in <ResolvedControllerSlot>[
      exactSlot(playerSlot: 0, serial: null),
      exactSlot(playerSlot: 0, displayName: 'Wrong Pad'),
      exactSlot(playerSlot: 0, sdlGuid: 'wrong-guid'),
    ]) {
      final cfg = await File(
        await writer.write(
          resolved,
          controllerSetup: LaunchControllerSetup(
            playerControllers: <LaunchPlayerController>[
              player(
                resolvedSlot: slot,
                mapping: BuiltinControllerTemplates.defaultMapping,
                policyId: RuntimeControllerInputPolicies
                    .retroArchMacosSingleControllerMfi8BitDoPro2Usb,
                shortcutsAllowed: true,
              ),
            ],
          ),
        ),
      ).readAsString();

      expect(cfg, isNot(contains('joypad_index')));
      expect(cfg, contains('input_enable_hotkey_btn = "nul"'));
      expect(cfg, contains('input_menu_toggle_btn = "nul"'));
    }
  });

  test(
    'a reserved P1 hole does not keep the lone exact pad off runtime port 1',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();

      final cfg = await File(
        await writer.write(
          resolved,
          controllerSetup: LaunchControllerSetup(
            playerControllers: <LaunchPlayerController>[
              player(
                resolvedSlot: exactSlot(playerSlot: 1),
                mapping: BuiltinControllerTemplates.defaultMapping,
              ),
            ],
            reservedPlayerSlots: const <int>{0},
          ),
        ),
      ).readAsString();

      expect(cfg, contains('input_player1_joypad_index = "0"'));
      expect(cfg, isNot(contains('input_player2_joypad_index')));
      expect(cfg, contains('input_enable_hotkey_btn = "nul"'));
      expect(cfg, contains('input_menu_toggle_btn = "nul"'));
    },
  );

  test(
    'single fallback controller remains uncorrelated and unchanged',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();

      final first = await File(
        await writer.write(
          resolved,
          controllerSetup: setup(padDeviceIndices: const <int>[0]),
        ),
      ).readAsString();
      final varied = await File(
        await writer.write(
          resolved,
          controllerSetup: setup(padDeviceIndices: const <int>[8]),
        ),
      ).readAsString();

      expect(first, varied);
      expect(first, isNot(contains('joypad_index')));
      expect(first, contains('input_enable_hotkey_btn = "nul"'));
    },
  );

  test(
    'DuckStation and arbitrary policies cannot authorize RetroArch output',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();

      for (final policyId in <String>[
        RuntimeControllerInputPolicies.duckStationMacosSingleController,
        'test.retroarch.arbitrary',
      ]) {
        final cfg = await File(
          await writer.write(
            resolved,
            controllerSetup: LaunchControllerSetup(
              playerControllers: <LaunchPlayerController>[
                player(
                  resolvedSlot: exactSlot(playerSlot: 0),
                  mapping: BuiltinControllerTemplates.defaultMapping,
                  policyId: policyId,
                  shortcutsAllowed: true,
                ),
              ],
            ),
          ),
        ).readAsString();

        expect(cfg, isNot(contains('joypad_index')), reason: policyId);
        expect(cfg, contains('input_enable_hotkey_btn = "nul"'));
        expect(cfg, contains('input_menu_toggle_btn = "nul"'));
      }
    },
  );

  test(
    'the RetroArch policy id fails closed with a non-production input shape',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();

      for (final shape
          in <
            ({
              RuntimeControllerInputProvider provider,
              RuntimeControllerInputCorrelation correlation,
            })
          >[
            (
              provider: RuntimeControllerInputProvider.other,
              correlation: RuntimeControllerInputCorrelation.verified,
            ),
            (
              provider: RuntimeControllerInputProvider.sdl3,
              correlation: RuntimeControllerInputCorrelation.acceptedBestEffort,
            ),
          ]) {
        final cfg = await File(
          await writer.write(
            resolved,
            controllerSetup: LaunchControllerSetup(
              playerControllers: <LaunchPlayerController>[
                player(
                  resolvedSlot: exactSlot(playerSlot: 0),
                  mapping: BuiltinControllerTemplates.defaultMapping,
                  provider: shape.provider,
                  correlation: shape.correlation,
                  shortcutsAllowed: true,
                ),
              ],
            ),
          ),
        ).readAsString();

        expect(cfg, isNot(contains('joypad_index')), reason: '$shape');
        expect(cfg, contains('input_enable_hotkey_btn = "nul"'));
        expect(cfg, contains('input_menu_toggle_btn = "nul"'));
      }
    },
  );

  test(
    'the production RetroArch policy cannot enable uncalibrated shortcuts',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();
      final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
        const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
          RomdAction.saveState: GamepadButtonPosition.faceEast,
        }),
      );

      final cfg = await File(
        await writer.write(
          resolved,
          controllerSetup: LaunchControllerSetup(
            playerControllers: <LaunchPlayerController>[
              player(
                resolvedSlot: exactSlot(playerSlot: 2),
                mapping: mapping,
                shortcutsAllowed: true,
              ),
            ],
            reservedPlayerSlots: const <int>{0, 1},
          ),
        ),
      ).readAsString();

      expect(cfg, contains('input_player1_joypad_index = "0"'));
      expect(cfg, contains('input_enable_hotkey_btn = "nul"'));
      expect(cfg, contains('input_save_state_btn = "nul"'));
      expect(cfg, isNot(contains('input_save_state_btn = "8"')));
      expect(cfg, isNot(contains('input_player3_joypad_index')));
    },
  );

  test('a mapping value-equal to the default is byte-identical to '
      'omitting it', () async {
    final resolved = target();
    const writer = RetroArchConfigWriter();

    final implicit = await File(await writer.write(resolved)).readAsString();
    final explicit = await File(
      await writer.write(
        resolved,
        // A runtime copy, not the const instance — output depends only on
        // binding values.
        controllerSetup: setup(
          mapping: ControllerMapping(
            BuiltinControllerTemplates.defaultMapping.bindings,
          ),
        ),
      ),
    ).readAsString();

    expect(explicit, implicit);
  });

  test('a rebound action emits its RetroPad index, and every chordable '
      'action has a config key', () async {
    final resolved = target();
    const writer = RetroArchConfigWriter();
    final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
      const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
        RomdAction.saveState: GamepadButtonPosition.faceEast,
        RomdAction.quit: GamepadButtonPosition.faceSouth,
        RomdAction.fastForward: GamepadButtonPosition.rightTrigger,
        RomdAction.pause: GamepadButtonPosition.leftStickPress,
      }),
    );

    final cfg = await File(
      await writer.write(resolved, controllerSetup: setup(mapping: mapping)),
    ).readAsString();

    expect(cfg, contains('input_save_state_btn = "8"'));
    expect(cfg, contains('input_exit_emulator_btn = "0"'));
    expect(cfg, contains('input_hold_fast_forward_btn = "13"'));
    expect(cfg, contains('input_pause_toggle_btn = "14"'));
    // Untouched defaults still emit.
    expect(cfg, contains('input_menu_toggle_btn = "3"'));
  });

  test('an explicitly unbound action emits an explicit "nul" bind — '
      'autoconfig profiles must not fill it back in', () async {
    final resolved = target();
    const writer = RetroArchConfigWriter();
    final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
      const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
        RomdAction.screenshot: null,
      }),
    );

    final cfg = await File(
      await writer.write(resolved, controllerSetup: setup(mapping: mapping)),
    ).readAsString();

    expect(cfg, contains('input_screenshot_btn = "nul"'));
    expect(cfg, contains('input_save_state_btn = "11"'));
  });

  test('an action bound to the modifier button emits "nul" — holding the '
      'modifier alone must never trigger an action', () async {
    final resolved = target();
    const writer = RetroArchConfigWriter();
    final mapping = BuiltinControllerTemplates.defaultMapping.overlaidWith(
      const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
        RomdAction.menu: GamepadButtonPosition.select,
      }),
    );

    final cfg = await File(
      await writer.write(resolved, controllerSetup: setup(mapping: mapping)),
    ).readAsString();

    expect(cfg, contains('input_enable_hotkey_btn = "2"'));
    expect(cfg, contains('input_menu_toggle_btn = "nul"'));
    expect(cfg, contains('input_save_state_btn = "11"'));
  });

  test('without a mappable hotkey-enable button the modifier and every '
      'chordable action are explicitly nul\'d', () async {
    final resolved = target();
    const writer = RetroArchConfigWriter();
    final unbindModifier = BuiltinControllerTemplates.defaultMapping
        .overlaidWith(
          const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
            RomdAction.hotkeyEnable: null,
          }),
        );
    // `guide` has no RetroPad id, so it must behave exactly like unbound.
    final guideModifier = BuiltinControllerTemplates.defaultMapping
        .overlaidWith(
          const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
            RomdAction.hotkeyEnable: GamepadButtonPosition.guide,
          }),
        );

    for (final mapping in <ControllerMapping>[unbindModifier, guideModifier]) {
      final cfg = await File(
        await writer.write(resolved, controllerSetup: setup(mapping: mapping)),
      ).readAsString();

      // RetroArch treats hotkeys as always active without an enable-hotkey
      // bind, so every chordable key gets nul'd — including actions the
      // mapping never bound (an autoconfig profile could supply them).
      expect(cfg, contains('input_enable_hotkey_btn = "nul"'));
      expect(cfg, contains('input_menu_toggle_btn = "nul"'));
      expect(cfg, contains('input_save_state_btn = "nul"'));
      expect(cfg, contains('input_exit_emulator_btn = "nul"'));
      expect(cfg, contains('input_hold_fast_forward_btn = "nul"'));
      expect(cfg, contains('input_pause_toggle_btn = "nul"'));
      expect(cfg, contains('input_menu_toggle_gamepad_combo = "0"'));
      expect(cfg, contains('input_quit_gamepad_combo = "0"'));
    }
  });

  test(
    'capability-filtered mappings do not emit unavailable buttons',
    () async {
      final resolved = target();
      const writer = RetroArchConfigWriter();
      final mapping = BuiltinControllerTemplates.defaultMapping
          .overlaidWith(
            const ControllerMapping.fixed(<RomdAction, GamepadButtonPosition?>{
              RomdAction.fastForward: GamepadButtonPosition.rightTrigger,
              RomdAction.screenshot: null,
            }),
          )
          .filteredFor(reducedCapabilities);

      final cfg = await File(
        await writer.write(resolved, controllerSetup: setup(mapping: mapping)),
      ).readAsString();

      expect(cfg, contains('input_enable_hotkey_btn = "2"'));
      expect(cfg, contains('input_menu_toggle_btn = "3"'));
      expect(cfg, isNot(contains('input_hold_fast_forward_btn = "13"')));
      expect(cfg, isNot(contains('input_save_state_btn = "11"')));
      expect(cfg, contains('input_screenshot_btn = "nul"'));
    },
  );
}
