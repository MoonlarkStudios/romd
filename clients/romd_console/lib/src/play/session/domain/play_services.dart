import 'package:romd_console/src/play/content/domain/install_service.dart';
import 'package:romd_console/src/play/content/domain/profile_local_library.dart';
import 'package:romd_console/src/play/controllers/domain/canonical_controller_mapping.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping_resolver.dart';
import 'package:romd_console/src/play/controllers/domain/controller_preferences.dart';

import 'active_launch_session.dart';
import 'emulator_settings_launcher.dart';
import 'play_activity.dart';
import 'play_coordinator.dart';
import 'profile_play_history.dart';
import 'runtime_override_rules.dart';
import 'runtime_resolver.dart';

/// Bundles the services the Play flow needs, so they thread through the widget
/// tree as a single dependency.
final class PlayServices {
  PlayServices({
    required this.install,
    required this.localLibrary,
    required this.coordinator,
    required this.playHistory,
    required this.runtimeResolver,
    required this.runtimeRules,
    required this.activeLaunchSession,
    required this.controllerPreferences,
    required this.controllerSlotClaims,
    this.playActivity,
    this.controllerHardwareMappings,
    this.controllerMappingResolver,
    this.emulatorSettingsLauncher,
    this.gamepadLister = _noGamepads,
    void Function()? onClose,
  }) : _onClose = onClose;

  final void Function()? _onClose;
  bool _closed = false;

  final InstallService install;
  final ProfileLocalLibraryRepository localLibrary;
  final PlayCoordinator coordinator;
  final ProfilePlayHistoryRepository playHistory;
  final PlayActivityRepository? playActivity;

  /// Read seam for the "Run with" picker: which runtimes could play an
  /// identity and which one is preferred. Shares its rule store with
  /// [coordinator]'s resolver so a persisted pick takes effect on the very
  /// next launch.
  final RuntimeResolver runtimeResolver;

  /// Write seam the picker persists runtime preference rules through.
  final RuntimeOverrideRuleRepository runtimeRules;

  /// Mutable handle to the currently supervised launch session, if any.
  final ActiveLaunchSession activeLaunchSession;

  /// Read/write seam for the Controllers screen: template choice. Shares its
  /// store with the coordinator so a change applies on the very next launch.
  final ControllerPreferencesRepository controllerPreferences;

  /// Session-scoped seating shared with the coordinator, which resolves
  /// claims against the connected pads at launch time. Player order is
  /// per-session (console model), formed by explicit joins and changed via the
  /// Change Order ceremony.
  final SessionControllerSlotClaims controllerSlotClaims;

  /// Device-global physical-to-canonical controller setup. The same instance
  /// is shared by setup UI and launch-time runtime translation.
  final ControllerHardwareMappingRepository? controllerHardwareMappings;

  /// Shared read-only mapping resolver used by launch and by the editor to
  /// calculate the inherited layer beneath the profile currently being edited.
  final ControllerMappingResolver? controllerMappingResolver;

  /// Opens ROMD-managed emulator configuration UIs outside gameplay.
  final EmulatorSettingsLauncher? emulatorSettingsLauncher;

  /// Shared gamepad device lister for controller UI and launch planning.
  final GamepadLister gamepadLister;

  void close() {
    if (_closed) return;
    _closed = true;
    coordinator.close();
    _onClose?.call();
  }
}

Future<List<ConnectedGamepad>> _noGamepads() async =>
    const <ConnectedGamepad>[];
