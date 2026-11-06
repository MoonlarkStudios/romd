import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/emulator/data/adapters/process_runner.dart';

void main() {
  test(
    'kill terminates a started process and exitCode completes',
    () async {
      const runner = SystemProcessRunner();
      final process = await runner.start(
        executable: '/bin/sleep',
        arguments: const <String>['30'],
        workingDirectory: Directory.current.path,
        environment: const <String, String>{},
      );
      addTearDown(process.kill);

      final delivered = process.kill();
      final exitCode = await process.exitCode.timeout(
        const Duration(seconds: 5),
      );

      expect(delivered, isTrue);
      expect(exitCode, isNot(0));
    },
    skip: Platform.isWindows
        ? 'Uses /bin/sleep as the long-lived child process.'
        : false,
  );
}
