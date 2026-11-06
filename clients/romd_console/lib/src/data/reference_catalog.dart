import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';

final class ReferenceAssetUnavailable implements Exception {
  const ReferenceAssetUnavailable();
}

/// An open display contract: system keys are data, never enum values.
final class ReferenceCatalog {
  ReferenceCatalog(this.json, {this.usedSnapshotRestart = false}) {
    if (json['schemaVersion'] != 1 ||
        json['revision'] is! String ||
        !_hash.hasMatch(revision)) {
      throw const FormatException('Unsupported reference catalog');
    }
    if (json.containsKey('builtInVersion') &&
        (json['builtInVersion'] is! int ||
            (json['builtInVersion'] as int) < 1)) {
      throw const FormatException('Invalid built-in catalog version');
    }
    for (final kind in referenceCatalogFamilies.keys) {
      // These three optional families were omitted by older schema-1 caches.
      if (!json.containsKey(kind) &&
          const ['companies', 'regions', 'languages'].contains(kind)) {
        continue;
      }
      final values = json[kind];
      if (values is! List<Object?>) throw FormatException('Missing $kind');
      final identities = <String>{};
      for (final value in values) {
        if (value is! Map<String, dynamic>) {
          throw const FormatException('Invalid reference entry');
        }
        validateReferenceCatalogEntry(kind, value);
        if (!identities.add(referenceCatalogEntryIdentity(kind, value))) {
          throw FormatException('Duplicate reference $kind identity');
        }
      }
    }
  }

  /// Transport metadata only; never changes the persisted schema-1 document.
  final bool usedSnapshotRestart;
  final Map<String, dynamic> json;
  String get revision => json['revision'] as String;
  List<Map<String, dynamic>> get systems =>
      (json['systems'] as List<Object?>).cast<Map<String, dynamic>>();
  Iterable<Map<String, dynamic>> get icons sync* {
    for (final kind in ['systems', 'ratings']) {
      for (final row
          in (json[kind] as List<Object?>).cast<Map<String, dynamic>>()) {
        if (row['icon'] case final Map<String, dynamic> icon) {
          if (!_hash.hasMatch(icon['sha256'] as String)) {
            throw const FormatException('Invalid asset hash');
          }
          yield icon;
        }
      }
    }
  }

  Map<String, dynamic>? system(String identity) {
    for (final row in systems) {
      if (row['key'] == identity) return row;
    }
    return null;
  }
}

/// Wire family names map to the six fixed endpoint path segments.
const referenceCatalogFamilies = <String, String>{
  'systems': 'systems',
  'companies': 'companies',
  'regions': 'regions',
  'languages': 'languages',
  'ratingBoards': 'rating-boards',
  'ratings': 'ratings',
};

String referenceCatalogEntryIdentity(String family, Map<String, dynamic> row) =>
    family == 'ratings'
    ? jsonEncode([row['board'], row['code']])
    : row['key'] as String;

/// Validate without projecting or filling fields: canonical integrity includes
/// the server's exact null/default properties and the original published order.
void validateReferenceCatalogEntry(
  String family,
  Map<String, dynamic> row, {
  bool strict = false,
}) {
  void text(String field) {
    if (row[field] is! String || (row[field] as String).isEmpty) {
      throw FormatException('Missing reference $family $field');
    }
  }

  void nullableText(String field) {
    if ((strict && !row.containsKey(field)) ||
        (row[field] != null && row[field] is! String)) {
      throw FormatException('Invalid reference $family $field');
    }
  }

  text('name');
  if (family == 'ratings') {
    text('board');
    text('code');
  } else {
    text('key');
  }
  nullableText('description');
  if ((strict && !row.containsKey('retired')) ||
      (row.containsKey('retired') && row['retired'] is! bool)) {
    throw FormatException('Invalid reference $family retired');
  }
  if (family == 'systems') {
    text('compactLabel');
    final keys = row['manufacturerKeys'];
    if ((strict && !row.containsKey('manufacturerKeys')) ||
        (keys != null && keys is! List<Object?>)) {
      throw const FormatException('Invalid manufacturer keys');
    }
    if (keys is List<Object?>) {
      final seen = <String>{};
      if (keys.length > 16) {
        throw const FormatException('Too many reference manufacturers');
      }
      for (final key in keys) {
        if (key is! String || key.isEmpty || !seen.add(key)) {
          throw const FormatException('Invalid manufacturer key');
        }
      }
    }
    final manufacturers = row['manufacturers'];
    if ((strict && !row.containsKey('manufacturers')) ||
        (manufacturers != null && manufacturers is! List<Object?>)) {
      throw const FormatException('Invalid reference manufacturers');
    }
    if (manufacturers is List<Object?>) {
      final seen = <String>{};
      if (manufacturers.length > 16) {
        throw const FormatException('Too many reference manufacturers');
      }
      for (final manufacturer in manufacturers) {
        if (manufacturer is! Map<String, dynamic> ||
            manufacturer['key'] is! String ||
            (manufacturer['key'] as String).isEmpty ||
            manufacturer['name'] is! String ||
            (manufacturer['name'] as String).isEmpty ||
            !seen.add(manufacturer['key'] as String)) {
          throw const FormatException('Invalid reference manufacturer');
        }
      }
      if (keys is List<Object?> &&
          (keys.length != manufacturers.length ||
              !keys.every((key) => seen.contains(key)))) {
        throw const FormatException('Conflicting manufacturer identities');
      }
    }
  }
  if (family == 'regions' || family == 'languages') {
    if (row['sortOrder'] is! int) {
      throw FormatException('Invalid reference $family sort order');
    }
    nullableText('languageCode');
  }
  if (family == 'ratings') {
    nullableText('designation');
    final age = row['minimumAge'];
    if ((strict && !row.containsKey('minimumAge')) ||
        (age != null && (age is! int || age < 0))) {
      throw const FormatException('Invalid reference rating minimum age');
    }
  }
  if (family == 'systems' || family == 'ratings') {
    final icon = row['icon'];
    if ((strict && !row.containsKey('icon')) ||
        (icon != null &&
            (icon is! Map<String, dynamic> ||
                icon['sha256'] is! String ||
                !_hash.hasMatch(icon['sha256'] as String) ||
                icon['url'] is! String ||
                (icon['url'] as String).isEmpty ||
                icon['contentType'] is! String ||
                (icon['contentType'] as String).isEmpty ||
                icon['monochrome'] is! bool))) {
      throw const FormatException('Invalid reference icon');
    }
  }
}

final _hash = RegExp(r'^[a-f0-9]{64}$');

abstract interface class ReferenceCatalogApiClient {
  Future<ReferenceCatalog?> getReferenceCatalog(
    String token, {
    String? revision,
    bool allowSnapshotRestart = true,
    bool Function()? isCurrent,
  });
  Future<Uint8List> getReferenceAsset(String url);
}

/// Immutable files plus an atomically replaced pointer. No credentials are stored.
/// The caller supplies an installation identity, independent of the active profile.
final class ReferenceCatalogCache {
  ReferenceCatalogCache(Directory root, String instance)
    : directory = Directory(
        '${root.path}/${sha256.convert(utf8.encode(instance))}',
      );
  final Directory directory;
  File asset(String hash) {
    if (!_hash.hasMatch(hash))
      throw const FormatException('Invalid asset hash');
    return File('${directory.path}/$hash.asset');
  }

  Future<ReferenceCatalog?> load() async {
    try {
      final revision = await File('${directory.path}/current').readAsString();
      if (!_hash.hasMatch(revision)) return null;
      final json = await File(
        '${directory.path}/$revision.json',
      ).readAsString();
      final catalog = ReferenceCatalog(
        jsonDecode(json) as Map<String, dynamic>,
      );
      return catalog.revision == revision ? catalog : null;
    } on FileSystemException {
      return null;
    } on FormatException {
      return null;
    } on TypeError {
      return null;
    }
  }

  Future<void> save(
    ReferenceCatalog catalog,
    ReferenceCatalogApiClient client, {
    bool Function()? isCurrent,
  }) async {
    await directory.create(recursive: true);
    for (final icon in catalog.icons) {
      if (isCurrent != null && !isCurrent()) return;
      final hash = icon['sha256'] as String;
      final file = asset(hash);
      if (await file.exists() &&
          sha256.convert(await file.readAsBytes()).toString() == hash)
        continue;
      final bytes = await client.getReferenceAsset(icon['url'] as String);
      if (isCurrent != null && !isCurrent()) return;
      if (bytes.length > 2 * 1024 * 1024 ||
          sha256.convert(bytes).toString() != hash) {
        throw const FormatException('Reference asset integrity check failed');
      }
      await _write(file, bytes);
    }
    await _write(
      File('${directory.path}/${catalog.revision}.json'),
      utf8.encode(jsonEncode(catalog.json)),
    );
    if (isCurrent != null && !isCurrent()) return;
    await _write(
      File('${directory.path}/current'),
      utf8.encode(catalog.revision),
      isCurrent: isCurrent,
    );
  }

  Future<void> _write(
    File destination,
    List<int> bytes, {
    bool Function()? isCurrent,
  }) async {
    final pending = await directory.createTemp('.pending-');
    try {
      final temporary = File('${pending.path}/content');
      await temporary.writeAsBytes(bytes, flush: true);
      if (isCurrent == null) {
        await temporary.rename(destination.path);
      } else if (isCurrent()) {
        // Keep the generation check and atomic pointer promotion in the same
        // event-loop turn, so activating another server cannot race the rename.
        temporary.renameSync(destination.path);
      }
    } finally {
      await pending.delete(recursive: true);
    }
  }
}
