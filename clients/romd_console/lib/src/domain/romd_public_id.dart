import 'package:sqids/sqids.dart';

final class RomdPublicId {
  const RomdPublicId._(this.value, this.decodedValue);

  static const alphabet =
      'abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  static const minimumLength = 6;
  static const maximumValue = 2147483647;

  static final Sqids _sqids = Sqids(
    alphabet: alphabet,
    minLength: minimumLength,
  );

  final String value;
  final int decodedValue;

  static RomdPublicId? tryParse(String? value) {
    if (value == null || value.isEmpty) {
      return null;
    }

    try {
      final decoded = _sqids.decode(value);
      if (decoded.length != 1 ||
          decoded.single <= 0 ||
          decoded.single > maximumValue) {
        return null;
      }

      final decodedValue = decoded.single;
      if (_sqids.encode(<int>[decodedValue]) != value) {
        return null;
      }

      return RomdPublicId._(value, decodedValue);
    } on Object {
      return null;
    }
  }

  static String encode(int value) {
    if (value <= 0 || value > maximumValue) {
      throw ArgumentError.value(
        value,
        'value',
        'Must be between 1 and $maximumValue.',
      );
    }

    return _sqids.encode(<int>[value]);
  }

  @override
  bool operator ==(Object other) =>
      other is RomdPublicId && other.value == value;

  @override
  int get hashCode => value.hashCode;

  @override
  String toString() => value;
}
