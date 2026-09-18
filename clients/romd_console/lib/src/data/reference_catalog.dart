import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';

final class ReferenceAssetUnavailable implements Exception {
  const ReferenceAssetUnavailable();
}

/// An open display contract: system keys are data, never enum values.
final class ReferenceCatalog {
  ReferenceCatalog(this.json) {
    if (json['schemaVersion'] != 1 ||
        json['revision'] is! String ||
        !_hash.hasMatch(revision)) {
      throw const FormatException('Unsupported reference catalog');
    }
    for (final kind in ['systems', 'ratingBoards', 'ratings']) {
      if (json[kind] is! List) throw FormatException('Missing $kind');
      for (final value in json[kind] as List) {
        if (value is! Map<String, dynamic>)
          throw const FormatException('Invalid reference entry');
        final fields = kind == 'systems'
            ? ['key', 'name', 'compactLabel']
            : kind == 'ratings'
            ? ['board', 'code', 'name']
            : ['key', 'name'];
        for (final field in fields) {
          if (value[field] is! String || (value[field] as String).isEmpty)
            throw FormatException('Missing $field');
        }
        final icon = value['icon'];
        if (icon != null &&
            (icon is! Map<String, dynamic> ||
                icon['sha256'] is! String ||
                !_hash.hasMatch(icon['sha256'] as String) ||
                icon['url'] is! String ||
                icon['contentType'] is! String ||
                icon['monochrome'] is! bool)) {
          throw const FormatException('Invalid reference icon');
        }
      }
    }
  }
  final Map<String, dynamic> json;
  String get revision => json['revision'] as String;
  List<Map<String, dynamic>> get systems =>
      (json['systems'] as List).cast<Map<String, dynamic>>();
  Iterable<Map<String, dynamic>> get icons sync* {
    for (final kind in ['systems', 'ratings']) {
      for (final row in (json[kind] as List).cast<Map<String, dynamic>>()) {
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

final _hash = RegExp(r'^[a-f0-9]{64}$');

abstract interface class ReferenceCatalogApiClient {
  Future<ReferenceCatalog?> getReferenceCatalog(
    String token, {
    String? revision,
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
      return ReferenceCatalog(jsonDecode(json) as Map<String, dynamic>);
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
      final hash = icon['sha256'] as String;
      final file = asset(hash);
      if (await file.exists() &&
          sha256.convert(await file.readAsBytes()).toString() == hash)
        continue;
      final bytes = await client.getReferenceAsset(icon['url'] as String);
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
    );
  }

  Future<void> _write(File destination, List<int> bytes) async {
    final pending = await directory.createTemp('.pending-');
    try {
      final temporary = File('${pending.path}/content');
      await temporary.writeAsBytes(bytes, flush: true);
      await temporary.rename(destination.path);
    } finally {
      await pending.delete(recursive: true);
    }
  }
}

Future<Uint8List> readReferenceResponse(
  HttpClientResponse response,
  int limit,
) async {
  final bytes = BytesBuilder(copy: false);
  await for (final chunk in response) {
    if (bytes.length + chunk.length > limit)
      throw const FormatException('Reference response too large');
    bytes.add(chunk);
  }
  return bytes.takeBytes();
}
