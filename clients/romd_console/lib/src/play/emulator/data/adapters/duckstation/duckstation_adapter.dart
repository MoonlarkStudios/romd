import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/adapters/standalone_runtime_adapter.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_adapter.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';

import 'duckstation_config_writer.dart';
import 'duckstation_user_directory.dart';

/// Runs PlayStation content through standalone DuckStation.
///
/// DuckStation is not a libretro core, so its adapter owns a different launch
/// contract from RetroArch: direct CLI boot, ROMD-scoped user directory, and no
/// core dependency.
final class DuckStationAdapter implements RuntimeAdapter {
  DuckStationAdapter({
    required DuckStationUserDirectory userDirectory,
    DuckStationConfigWriter configWriter = const DuckStationIniConfigWriter(),
    ProcessRunner processRunner = const SystemProcessRunner(),
  }) : _inner = StandaloneRuntimeAdapter(
         logTag: 'duckstation',
         extraDirectories: (_) => <String>[
           userDirectory.dataDirectoryPath,
           userDirectory.biosDirectoryPath,
         ],
         environmentBuilder: (_) => userDirectory.environmentOverrides,
         prepareAction: (plan) => configWriter.write(
           userDirectory,
           controllerSetup: plan.controllerSetup,
           saveRoot: plan.target.saveRoot,
           stateRoot: plan.target.stateRoot,
         ),
         argumentsBuilder: (plan) => <String>[
           '-batch',
           '-fastboot',
           '-nogui',
           '-fullscreen',
           '--',
           plan.target.launchAbsolutePath,
         ],
         processRunner: processRunner,
       );

  final StandaloneRuntimeAdapter _inner;

  @override
  Future<void> prepare(EmulatorLaunchPlan plan) => _inner.prepare(plan);

  @override
  CommandPlan buildCommand(EmulatorLaunchPlan plan, String executablePath) =>
      _inner.buildCommand(plan, executablePath);

  @override
  Future<LaunchStartResult> launch(EmulatorLaunchPlan plan) =>
      _inner.launch(plan);
}
