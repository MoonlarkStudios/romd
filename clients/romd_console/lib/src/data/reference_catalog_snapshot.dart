import 'reference_catalog.dart';
import 'reference_catalog_canonical_json.dart';

final class ReferenceSnapshotChanged implements Exception {
  const ReferenceSnapshotChanged();
}

final class ReferenceSnapshotCancelled implements Exception {
  const ReferenceSnapshotCancelled();
}

void requireCurrentReferenceSnapshot(bool Function()? isCurrent) {
  if (isCurrent != null && !isCurrent()) {
    throw const ReferenceSnapshotCancelled();
  }
}

/// The root validator identifies this descriptor representation. CatalogRevision
/// identifies the complete schema-1 document, including its published row order.
final class ReferenceCatalogDescriptor {
  ReferenceCatalogDescriptor(Map<String, dynamic> json, String? etag) {
    final version = json['builtInVersion'];
    final descriptorRevision = json['revision'];
    final catalog = json['catalogRevision'];
    final digest = json['contentHash'];
    final familyCounts = json['counts'];
    if (json['schemaVersion'] != 1 ||
        version is! int ||
        version < 1 ||
        version > 9007199254740991 ||
        descriptorRevision is! String ||
        !_hash.hasMatch(descriptorRevision) ||
        etag != '"$descriptorRevision"' ||
        catalog is! String ||
        !_hash.hasMatch(catalog) ||
        digest is! String ||
        !_hash.hasMatch(digest) ||
        familyCounts is! Map<String, dynamic> ||
        familyCounts.length != referenceCatalogFamilies.length) {
      throw const FormatException('Invalid reference catalog descriptor');
    }
    for (final family in referenceCatalogFamilies.keys) {
      final count = familyCounts[family];
      if (count is! int || count < 0 || count > 9007199254740991) {
        throw FormatException('Invalid reference $family count');
      }
      counts[family] = count;
    }
    builtInVersion = version;
    catalogRevision = catalog;
    contentHash = digest;
  }

  late final int builtInVersion;
  late final String catalogRevision;
  late final String contentHash;
  final counts = <String, int>{};
  static final _hash = RegExp(r'^[a-f0-9]{64}$');
}

/// Accumulation stays private until all six pinned families, nested records,
/// natural keys, counts and canonical content digest have been validated.
Future<ReferenceCatalog> collectReferenceSnapshot(
  ReferenceCatalogDescriptor descriptor,
  Future<Map<String, dynamic>> Function(String path, String? cursor) readPage, {
  bool usedSnapshotRestart = false,
  bool Function()? isCurrent,
}) async {
  final complete = <String, dynamic>{
    'schemaVersion': 1,
    'builtInVersion': descriptor.builtInVersion,
    'revision': descriptor.catalogRevision,
  };
  for (final family in referenceCatalogFamilies.entries) {
    final rows = <Map<String, dynamic>>[];
    final identities = <String>{};
    final cursors = <String>{};
    String? cursor;
    do {
      requireCurrentReferenceSnapshot(isCurrent);
      final page = await readPage(family.value, cursor);
      requireCurrentReferenceSnapshot(isCurrent);
      final items = page['items'];
      final hasNext = page['hasNextPage'];
      final next = page['nextCursor'];
      if (items is! List<Object?> ||
          items.length > 100 ||
          hasNext is! bool ||
          !page.containsKey('nextCursor') ||
          (hasNext
              ? next is! String || next.isEmpty || items.isEmpty
              : next != null)) {
        throw const FormatException('Invalid reference snapshot page');
      }
      for (final item in items) {
        if (item is! Map<String, dynamic>) {
          throw const FormatException('Invalid reference snapshot row');
        }
        validateReferenceCatalogEntry(family.key, item, strict: true);
        if (!identities.add(referenceCatalogEntryIdentity(family.key, item))) {
          throw const FormatException('Duplicate reference snapshot identity');
        }
        rows.add(item);
      }
      final expected = descriptor.counts[family.key]!;
      if (rows.length > expected ||
          (!hasNext && rows.length != expected) ||
          (hasNext && rows.length >= expected)) {
        throw const FormatException('Reference snapshot count mismatch');
      }
      if (hasNext && !cursors.add(next as String)) {
        throw const FormatException('Repeated reference snapshot cursor');
      }
      cursor = next as String?;
    } while (cursor != null);
    complete[family.key] = rows;
  }
  _validateRelationships(complete);
  requireCurrentReferenceSnapshot(isCurrent);
  if (referenceCatalogContentHash(complete) != descriptor.contentHash) {
    throw const FormatException('Reference snapshot content hash mismatch');
  }
  return ReferenceCatalog(complete, usedSnapshotRestart: usedSnapshotRestart);
}

void _validateRelationships(Map<String, dynamic> complete) {
  final companies = <String, String>{
    for (final row in (complete['companies'] as List<Map<String, dynamic>>))
      row['key'] as String: row['name'] as String,
  };
  for (final system in (complete['systems'] as List<Map<String, dynamic>>)) {
    final keys = system['manufacturerKeys'] as List<Object?>?;
    final manufacturers = system['manufacturers'] as List<Object?>?;
    for (final key in keys ?? const <Object?>[]) {
      if (!companies.containsKey(key)) {
        throw const FormatException('Missing reference manufacturer');
      }
    }
    for (final value in manufacturers ?? const <Object?>[]) {
      final manufacturer = value as Map<String, dynamic>;
      if (companies[manufacturer['key']] != manufacturer['name']) {
        throw const FormatException('Conflicting reference manufacturer');
      }
    }
  }
  final boards = <String>{
    for (final row in (complete['ratingBoards'] as List<Map<String, dynamic>>))
      row['key'] as String,
  };
  for (final rating in (complete['ratings'] as List<Map<String, dynamic>>)) {
    if (!boards.contains(rating['board'])) {
      throw const FormatException('Missing reference rating board');
    }
  }
}
