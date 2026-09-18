import 'dart:io';

/// A started child process, narrowed to what supervision needs.
abstract interface class RunningProcess {
  Future<int> get exitCode;
  Stream<List<int>> get stdout;
  Stream<List<int>> get stderr;

  /// Sends a termination signal to the process.
  ///
  /// Returns true when the signal was delivered. Process death is confirmed
  /// separately by [exitCode] completing.
  bool kill();
}

/// Result of a run-to-completion command.
final class ProcessRunResult {
  const ProcessRunResult({
    required this.exitCode,
    required this.stdout,
    required this.stderr,
  });
  final int exitCode;
  final String stdout;
  final String stderr;
  bool get ok => exitCode == 0;
}

/// Seam over process spawning so adapters/installers are testable without
/// launching real binaries.
abstract interface class ProcessRunner {
  /// Start a long-lived, supervised process (the emulator).
  Future<RunningProcess> start({
    required String executable,
    required List<String> arguments,
    required String workingDirectory,
    required Map<String, String> environment,
  });

  /// Run a command to completion (e.g. `hdiutil`, `ditto`, `xattr`), capturing
  /// its exit code and output.
  Future<ProcessRunResult> run({
    required String executable,
    required List<String> arguments,
    String? workingDirectory,
    Map<String, String> environment = const <String, String>{},
  });
}

/// Production runner over `dart:io`. Always `runInShell: false` so arguments are
/// passed verbatim (no shell interpretation of paths).
final class SystemProcessRunner implements ProcessRunner {
  const SystemProcessRunner();

  @override
  Future<RunningProcess> start({
    required String executable,
    required List<String> arguments,
    required String workingDirectory,
    required Map<String, String> environment,
  }) async {
    final process = await Process.start(
      executable,
      arguments,
      workingDirectory: workingDirectory,
      environment: environment,
      // Explicit security guarantee: arguments are passed verbatim, never via a
      // shell. (Matches the default, kept explicit on purpose.)
      // ignore: avoid_redundant_argument_values
      runInShell: false,
    );
    return _SystemRunningProcess(process);
  }

  @override
  Future<ProcessRunResult> run({
    required String executable,
    required List<String> arguments,
    String? workingDirectory,
    Map<String, String> environment = const <String, String>{},
  }) async {
    final result = await Process.run(
      executable,
      arguments,
      workingDirectory: workingDirectory,
      environment: environment,
      // ignore: avoid_redundant_argument_values
      runInShell: false,
    );
    return ProcessRunResult(
      exitCode: result.exitCode,
      stdout: result.stdout is String ? result.stdout as String : '',
      stderr: result.stderr is String ? result.stderr as String : '',
    );
  }
}

final class _SystemRunningProcess implements RunningProcess {
  _SystemRunningProcess(this._process);

  final Process _process;

  @override
  Future<int> get exitCode => _process.exitCode;

  @override
  Stream<List<int>> get stdout => _process.stdout;

  @override
  Stream<List<int>> get stderr => _process.stderr;

  @override
  bool kill() => _process.kill();
}
