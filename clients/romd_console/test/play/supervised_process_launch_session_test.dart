import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';
import 'package:romd_console/src/play/emulator/data/adapters/supervised_process_launch_session.dart';
import 'package:romd_console/src/play/session/domain/launch_service.dart';

final class _ControlledRunningProcess implements RunningProcess {
  _ControlledRunningProcess({this.exitCodeOnKill});

  final int? exitCodeOnKill;
  final Completer<int> _exitCode = Completer<int>();
  int exitCodeReads = 0;
  int killCount = 0;

  @override
  Future<int> get exitCode {
    exitCodeReads += 1;
    return _exitCode.future;
  }

  @override
  Stream<List<int>> get stdout => const Stream<List<int>>.empty();

  @override
  Stream<List<int>> get stderr => const Stream<List<int>>.empty();

  @override
  bool kill() {
    killCount += 1;
    final code = exitCodeOnKill;
    if (code != null) {
      completeExit(code);
    }
    return true;
  }

  void completeExit(int code) {
    if (!_exitCode.isCompleted) {
      _exitCode.complete(code);
    }
  }
}

void main() {
  test(
    'terminate delivers the process signal and completed resolves after exit',
    () async {
      final process = _ControlledRunningProcess(exitCodeOnKill: 143);
      final session = SupervisedProcessLaunchSession(process);

      expect(session.foreground, isNull);
      final termination = session.termination;
      expect(termination, isNotNull);

      final delivered = await termination!.terminate();
      final completed = await session.completed;

      expect(delivered, isTrue);
      expect(process.killCount, 1);
      expect(completed, isA<LaunchExited>());
      expect((completed as LaunchExited).exitCode, 143);
    },
  );

  test('completed is memoized for multiple awaiters', () async {
    final process = _ControlledRunningProcess();
    final session = SupervisedProcessLaunchSession(process);

    final first = session.completed;
    final second = session.completed;
    expect(process.exitCodeReads, 1);

    process.completeExit(7);
    final results = await Future.wait(<Future<LaunchResult>>[first, second]);

    expect(results[0], same(results[1]));
    expect((results[0] as LaunchExited).exitCode, 7);
    expect(process.exitCodeReads, 1);
  });
}
