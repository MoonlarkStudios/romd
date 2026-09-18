import 'dart:io';

import 'package:crypto/crypto.dart';

typedef RuntimeProcessExecutableResolver = String Function(String runtimePath);

Future<bool> verifyRuntimeExecutableSha256({
  required String runtimePath,
  required RuntimeProcessExecutableResolver processExecutableResolver,
  required String expectedSha256,
}) async {
  try {
    final executable = File(processExecutableResolver(runtimePath));
    if (!await executable.exists()) return false;
    final digest = await sha256.bind(executable.openRead()).first;
    return digest.toString().toLowerCase() == expectedSha256.toLowerCase();
  } on Object {
    return false;
  }
}
