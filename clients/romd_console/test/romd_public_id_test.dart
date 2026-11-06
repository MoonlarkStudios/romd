import 'package:flutter_test/flutter_test.dart';
import 'package:romd_console/src/domain/romd_public_id.dart';
import 'package:sqids/sqids.dart';

void main() {
  final codec = Sqids(
    alphabet: RomdPublicId.alphabet,
    minLength: RomdPublicId.minimumLength,
  );

  test('encodes and parses the exact canonical ROMD public ID', () {
    final encoded = RomdPublicId.encode(1);
    final parsed = RomdPublicId.tryParse(encoded);

    expect(encoded, '6TLEgs');
    expect(parsed, isNotNull);
    expect(parsed!.value, encoded);
    expect(parsed.decodedValue, 1);
  });

  test('accepts the backend signed 32-bit maximum', () {
    final encoded = RomdPublicId.encode(RomdPublicId.maximumValue);
    final parsed = RomdPublicId.tryParse(encoded);

    expect(parsed, isNotNull);
    expect(parsed!.decodedValue, RomdPublicId.maximumValue);
    expect(parsed.value, encoded);
  });

  test('rejects absent malformed zero and noncanonical public IDs', () {
    expect(RomdPublicId.tryParse(null), isNull);
    expect(RomdPublicId.tryParse(''), isNull);
    expect(RomdPublicId.tryParse('!!!!!!'), isNull);
    expect(RomdPublicId.tryParse(codec.encode(<int>[0])), isNull);
    expect(RomdPublicId.tryParse('6TLEgsa'), isNull);
  });

  test('rejects an encoded sequence containing multiple values', () {
    final encoded = codec.encode(<int>[1, 2]);

    expect(codec.decode(encoded), <int>[1, 2]);
    expect(RomdPublicId.tryParse(encoded), isNull);
  });

  test('rejects a canonical Sqids value above the backend integer range', () {
    final oversized = codec.encode(<int>[RomdPublicId.maximumValue + 1]);

    expect(codec.decode(oversized), <int>[RomdPublicId.maximumValue + 1]);
    expect(RomdPublicId.tryParse(oversized), isNull);
  });

  test('value equality is based on the canonical encoded ID', () {
    final first = RomdPublicId.tryParse(RomdPublicId.encode(42));
    final second = RomdPublicId.tryParse(RomdPublicId.encode(42));
    final other = RomdPublicId.tryParse(RomdPublicId.encode(43));

    expect(first, second);
    expect(first.hashCode, second.hashCode);
    expect(first, isNot(other));
  });

  test('encode rejects zero and negative values', () {
    expect(() => RomdPublicId.encode(0), throwsArgumentError);
    expect(() => RomdPublicId.encode(-1), throwsArgumentError);
    expect(
      () => RomdPublicId.encode(RomdPublicId.maximumValue + 1),
      throwsArgumentError,
    );
  });
}
