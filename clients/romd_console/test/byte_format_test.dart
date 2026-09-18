import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/presentation/widgets/byte_format.dart';

void main() {
  test('bytes below 1 KB render as raw bytes', () {
    expect(formatBytes(0), '0 B');
    expect(formatBytes(512), '512 B');
    expect(formatBytes(1023), '1023 B');
  });

  test('kilobyte and megabyte tiers', () {
    expect(formatBytes(1024), '1.0 KB');
    expect(formatBytes(512 * 1024), '512 KB');
    expect(formatBytes(3 * 1024 * 1024), '3.0 MB');
  });

  test('one decimal below 10 in a unit, none above', () {
    expect(formatBytes((2.5 * 1024 * 1024).round()), '2.5 MB');
    expect(formatBytes(700 * 1024 * 1024), '700 MB');
  });

  test('gigabyte tier', () {
    expect(formatBytes(4 * 1024 * 1024 * 1024), '4.0 GB');
  });
}
