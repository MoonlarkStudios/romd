import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/adapters/standalone_runtime_adapter.dart';
import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/emulator/domain/runtime_adapter.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';

import 'dolphin_config_writer.dart';
import 'dolphin_executable.dart';
import 'dolphin_user_directory.dart';

/// Runs GameCube content through a ROMD-scoped Dolphin user directory.
final class DolphinAdapter implements RuntimeAdapter {
  DolphinAdapter({
    required DolphinUserDirectory userDirectory,
    DolphinConfigWriter configWriter = const DolphinIniConfigWriter(),
    ProcessRunner processRunner = const SystemProcessRunner(),
  }) : _inner = StandaloneRuntimeAdapter(
         logTag: 'dolphin',
         extraDirectories: (plan) => userDirectory.directories,
         environmentBuilder: configWriter.environmentFor,
         prepareAction: (plan) => configWriter.write(plan, userDirectory),
         argumentsBuilder: (plan) => <String>[
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
           'Dolphin.Core.MemcardAPath=${p.join(plan.target.saveRoot, 'MemoryCardA.raw')}',
           '-C',
           'Dolphin.Core.MemcardBPath=${p.join(plan.target.saveRoot, 'MemoryCardB.raw')}',
           '-e',
           plan.target.launchAbsolutePath,
         ],
         executableResolver: dolphinProcessExecutable,
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
