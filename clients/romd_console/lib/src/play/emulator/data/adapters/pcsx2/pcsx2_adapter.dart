import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/adapters/standalone_runtime_adapter.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_adapter.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';

import 'pcsx2_config_writer.dart';
import 'pcsx2_user_directory.dart';

/// Runs PlayStation 2 content through standalone PCSX2.
///
/// PCSX2's Qt CLI supports direct boot with `-batch`, `-fullscreen`, and
/// `-- <boot filename>`. New builds also document `-datapath`, but older Qt
/// builds reject it, so ROMD scopes data through environment overrides instead.
final class Pcsx2Adapter implements RuntimeAdapter {
  Pcsx2Adapter({
    required Pcsx2UserDirectory userDirectory,
    Pcsx2ConfigWriter? configWriter,
    ProcessRunner processRunner = const SystemProcessRunner(),
  }) : _inner = StandaloneRuntimeAdapter(
         logTag: 'pcsx2',
         extraDirectories: (_) => <String>[
           userDirectory.dataDirectoryPath,
           userDirectory.biosDirectoryPath,
         ],
         environmentBuilder: (_) => userDirectory.environmentOverrides,
         prepareAction: (plan) =>
             (configWriter ??
                     Pcsx2IniConfigWriter(processRunner: processRunner))
                 .write(
                   userDirectory: userDirectory,
                   executablePath: _resolvedExecutablePath(plan),
                   saveRoot: plan.target.saveRoot,
                   stateRoot: plan.target.stateRoot,
                 ),
         argumentsBuilder: (plan) => <String>[
           '-batch',
           '-fastboot',
           '-fullscreen',
           '--',
           plan.target.launchAbsolutePath,
         ],
         executableResolver: _pcsx2ProcessExecutable,
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

String _pcsx2ProcessExecutable(String executablePath) =>
    executablePath.endsWith('.app')
    ? p.join(executablePath, 'Contents', 'MacOS', 'PCSX2')
    : executablePath;

String? _resolvedExecutablePath(EmulatorLaunchPlan plan) {
  final executableRequirement = plan.profile.executableRequirement;
  final executablePath = executableRequirement == null
      ? null
      : plan.dependencyPath(executableRequirement.id);
  return executablePath == null
      ? null
      : _pcsx2ProcessExecutable(executablePath);
}
