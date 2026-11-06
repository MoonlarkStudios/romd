import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

final _directivePattern = RegExp(
  r"""^\s*(?:import|export)\s+['"]([^'"]+)['"]""",
);

void main() {
  test('session module does not import emulator module', () {
    final sessionRoot = Directory('lib/src/play/session');
    expect(sessionRoot.existsSync(), isTrue);

    final violations = <String>[];
    for (final entity in sessionRoot.listSync(recursive: true)) {
      if (entity is! File || !entity.path.endsWith('.dart')) {
        continue;
      }

      final lines = entity.readAsLinesSync();
      for (var index = 0; index < lines.length; index++) {
        final directive = _directivePattern.firstMatch(lines[index]);
        if (directive == null) {
          continue;
        }

        final uri = directive.group(1)!;
        if (_importsEmulator(uri)) {
          violations.add('${entity.path}:${index + 1}: $uri');
        }
      }
    }

    expect(
      violations,
      isEmpty,
      reason: 'session/ must not import emulator/.\n${violations.join('\n')}',
    );
  });
}

bool _importsEmulator(String uri) =>
    uri.startsWith('package:romd_console/src/play/emulator/') ||
    uri.contains('/emulator/');
