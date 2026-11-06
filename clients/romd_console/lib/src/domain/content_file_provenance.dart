/// The source of these exact delivered bytes, independent of their container format.
enum ContentFileProvenance {
  original,
  derived;

  String get wireName => switch (this) {
    original => 'Original',
    derived => 'Derived',
  };

  static ContentFileProvenance parse(Object? value) => switch (value) {
    'Original' => original,
    'Derived' => derived,
    _ => throw const FormatException('Unsupported file provenance.'),
  };
}
