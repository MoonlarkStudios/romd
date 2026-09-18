import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/play/content/data/content_verifier.dart';

void main() {
  const verifier = ContentVerifier();
  // SHA-256 of the ASCII bytes "hello" (5 bytes).
  const helloSha256 =
      '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824';

  late Directory dir;
  late File file;

  setUp(() {
    dir = Directory.systemTemp.createTempSync('romd_verify_test');
    file = File('${dir.path}/content.bin')..writeAsStringSync('hello');
  });

  tearDown(() {
    if (dir.existsSync()) {
      dir.deleteSync(recursive: true);
    }
  });

  test('verify returns true for matching size and digest', () async {
    expect(
      await verifier.verify(
        file: file,
        expectedSize: 5,
        expectedSha256Hex: helloSha256.toUpperCase(),
      ),
      isTrue,
    );
  });

  test('verify returns false on size mismatch', () async {
    expect(
      await verifier.verify(
        file: file,
        expectedSize: 4,
        expectedSha256Hex: helloSha256,
      ),
      isFalse,
    );
  });

  test('verify returns false on digest mismatch', () async {
    expect(
      await verifier.verify(
        file: file,
        expectedSize: 5,
        expectedSha256Hex: '0' * 64,
      ),
      isFalse,
    );
  });

  test('verify returns false when the file is missing', () async {
    expect(
      await verifier.verify(
        file: File('${dir.path}/missing.bin'),
        expectedSize: 5,
        expectedSha256Hex: helloSha256,
      ),
      isFalse,
    );
  });

  test('manifestFingerprint is order-independent', () {
    const a = (relativePath: 'a.sfc', sizeBytes: 10, sha256: 'aa');
    const b = (relativePath: 'b.sfc', sizeBytes: 20, sha256: 'bb');

    expect(
      verifier.manifestFingerprint(<FingerprintItem>[a, b]),
      verifier.manifestFingerprint(<FingerprintItem>[b, a]),
    );
  });

  test('manifestFingerprint changes when content changes', () {
    const a = (relativePath: 'a.sfc', sizeBytes: 10, sha256: 'aa');
    const aBigger = (relativePath: 'a.sfc', sizeBytes: 11, sha256: 'aa');

    expect(
      verifier.manifestFingerprint(<FingerprintItem>[a]),
      isNot(verifier.manifestFingerprint(<FingerprintItem>[aBigger])),
    );
  });
}
