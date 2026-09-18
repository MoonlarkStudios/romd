final class RomdServerInstanceId {
  const RomdServerInstanceId._(this.value);

  static final RegExp _canonicalPattern = RegExp(
    r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',
  );

  final String value;

  static RomdServerInstanceId? tryParse(String? value) {
    if (value == null || !_canonicalPattern.hasMatch(value)) {
      return null;
    }

    return RomdServerInstanceId._(value);
  }

  @override
  bool operator ==(Object other) =>
      other is RomdServerInstanceId && other.value == value;

  @override
  int get hashCode => value.hashCode;

  @override
  String toString() => value;
}
