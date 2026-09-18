import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/romd_server_instance_id.dart';

void main() {
  const canonical = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';

  test('accepts canonical lowercase UUID-D', () {
    final parsed = RomdServerInstanceId.tryParse(canonical);

    expect(parsed, isNotNull);
    expect(parsed!.value, canonical);
    expect(parsed.toString(), canonical);
  });

  test('rejects absent malformed and noncanonical UUID representations', () {
    const cases = <String?>[
      null,
      '',
      'not-a-uuid',
      'AAAAAAAA-BBBB-4CCC-8DDD-EEEEEEEEEEEE',
      'aaaaaaaabbbb4ccc8dddeeeeeeeeeeee',
      '{aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee}',
      ' aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
      'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee\n',
      'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeee',
    ];

    for (final value in cases) {
      expect(RomdServerInstanceId.tryParse(value), isNull, reason: '$value');
    }
  });

  test('value equality uses the canonical UUID', () {
    final first = RomdServerInstanceId.tryParse(canonical);
    final second = RomdServerInstanceId.tryParse(canonical);
    final other = RomdServerInstanceId.tryParse(
      'ffffffff-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    );

    expect(first, second);
    expect(first.hashCode, second.hashCode);
    expect(first, isNot(other));
  });
}
