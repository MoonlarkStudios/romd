import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:romd_console/src/play/emulator/data/registry/runtime_executable_integrity.dart';

void main() {
  test(
    'exact executable digest accepts pinned bytes and rejects mutation',
    () async {
      final tmp = Directory.systemTemp.createTempSync(
        'romd_runtime_digest_test',
      );
      addTearDown(() => tmp.deleteSync(recursive: true));
      final executable = File(p.join(tmp.path, 'Dolphin'))
        ..writeAsBytesSync(<int>[1, 2, 3]);
      const expected =
          '039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81';

      expect(
        await verifyRuntimeExecutableSha256(
          runtimePath: executable.path,
          processExecutableResolver: (path) => path,
          expectedSha256: expected,
        ),
        isTrue,
      );

      executable.writeAsBytesSync(<int>[1, 2, 4]);
      expect(
        await verifyRuntimeExecutableSha256(
          runtimePath: executable.path,
          processExecutableResolver: (path) => path,
          expectedSha256: expected,
        ),
        isFalse,
      );
    },
  );
}
