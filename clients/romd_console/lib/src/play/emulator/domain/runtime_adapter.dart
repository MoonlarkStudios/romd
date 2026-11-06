import 'package:romd_console/src/play/emulator/domain/emulator_launch_plan.dart';
import 'package:romd_console/src/play/session/domain/launch_session.dart';

/// A concrete process invocation. [arguments] is a structured list (never a
/// shell string) and the process is started with `runInShell: false` — paths
/// are passed as discrete arguments so a filename can never be interpreted.
final class CommandPlan {
  const CommandPlan({
    required this.executable,
    required this.arguments,
    required this.workingDirectory,
    required this.environment,
  });

  final String executable;
  final List<String> arguments;
  final String workingDirectory;
  final Map<String, String> environment;
}

/// Translates an [EmulatorLaunchPlan] into runtime-specific behavior. The single
/// swappable boundary: a new emulator/port is a new adapter plus one
/// factory-map entry, with no changes above it.
abstract interface class RuntimeAdapter {
  /// Pre-launch side effects (create save/state/config dirs; point the emulator
  /// at the ROMD-controlled save location — never the content root). Idempotent.
  Future<void> prepare(EmulatorLaunchPlan plan);

  /// Pure: build the exact invocation. No IO — the resolved executable is
  /// supplied so this stays deterministically testable.
  CommandPlan buildCommand(EmulatorLaunchPlan plan, String executablePath);

  /// Resolve the runtime, [prepare], and start a provider-owned session.
  Future<LaunchStartResult> launch(EmulatorLaunchPlan plan);
}
