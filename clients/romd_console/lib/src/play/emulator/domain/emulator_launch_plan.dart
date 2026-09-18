import 'package:romd_console/src/play/controllers/domain/builtin_controller_templates.dart';
import 'package:romd_console/src/play/controllers/domain/controller_assignments.dart';
import 'package:romd_console/src/play/controllers/domain/controller_mapping.dart';
import 'package:romd_console/src/play/emulator/domain/launch_controller_setup.dart';
import 'package:romd_console/src/play/session/domain/play_target.dart';
import 'package:romd_console/src/play/session/domain/runtime_dependency.dart';
import 'package:romd_console/src/play/session/domain/runtime_profile.dart';

/// The full context an adapter needs: what to play, which profile launches it,
/// and where each resolved requirement lives.
final class EmulatorLaunchPlan {
  EmulatorLaunchPlan({
    required this.target,
    required this.profile,
    required this.dependencies,
    LaunchControllerSetup? controllerSetup,
    ControllerMapping controllerMapping =
        BuiltinControllerTemplates.defaultMapping,
    List<ResolvedControllerSlot> controllerSlots =
        const <ResolvedControllerSlot>[],
    Set<int> reservedPlayerSlots = const <int>{},
    Set<int> blockedPlayerSlots = const <int>{},
  }) : controllerSetup =
           controllerSetup ??
           LaunchControllerSetup(
             mapping: controllerMapping,
             controllerSlots: controllerSlots,
             reservedPlayerSlots: reservedPlayerSlots,
             blockedPlayerSlots: blockedPlayerSlots,
           );

  final ResolvedPlayTarget target;
  final RuntimeProfile profile;
  final List<ResolvedRuntimeDependency> dependencies;

  /// Centralized runtime-neutral controller setup for this launch. Writers
  /// should translate this at their boundary rather than accepting separate
  /// mapping and raw runtime-index arguments.
  final LaunchControllerSetup controllerSetup;

  /// Immutable slot-keyed launch controller entries. Runtime adapters should
  /// consume these entries instead of re-resolving mappings or controller
  /// identity from compatibility views.
  List<LaunchPlayerController> get playerControllers =>
      controllerSetup.playerControllers;

  /// Compatibility getter for call sites still reading the mapping directly.
  ControllerMapping get controllerMapping => controllerSetup.mapping;

  /// Resolved player-slot assignment for this launch. Empty means connection
  /// order. Runtime adapters should prefer [controllerSetup] and translate
  /// [ResolvedControllerSlot.runtimeIndex] only at the final config boundary.
  List<ResolvedControllerSlot> get controllerSlots =>
      controllerSetup.controllerSlots;

  /// Exact offline player-slot reservations for this launch.
  Set<int> get reservedPlayerSlots => controllerSetup.reservedPlayerSlots;

  /// Non-exact player slots that must not be filled from connection order.
  Set<int> get blockedPlayerSlots => controllerSetup.blockedPlayerSlots;

  /// Compatibility bridge for call sites that still inspect runtime indices.
  List<int> get padDeviceIndices => controllerSetup.padDeviceIndices;

  /// Path of the dependency resolved for [requirementId], or null when the
  /// plan carries none — adapters look dependencies up here instead of
  /// scanning the list stringly.
  String? dependencyPath(String requirementId) {
    for (final dependency in dependencies) {
      if (dependency.requirementId == requirementId) {
        return dependency.path;
      }
    }
    return null;
  }
}
